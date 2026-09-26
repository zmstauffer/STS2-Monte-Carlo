namespace SpireMonteCarlo.Sim;

/// <summary>One step of a turn plan: play a hand card (found by its tag) or drink a potion.</summary>
public readonly record struct BotAction(int Tag, string? PotionId, int Target)
{
    public bool IsPotion => PotionId != null;
}

/// <summary>
/// A mid-strength combat player. Each turn it looks for the best whole sequence of plays: it tries card and potion orders on
/// copies of the combat (so it sees Vulnerable land before the attacks, energy from Bloodletting being spent, block that is or
/// isn't needed by then) and keeps the sequence whose plays add up to the most value. Each play is valued by what it accomplishes
/// right now (damage after modifiers, kills, block actually needed, debuffs worth applying, buffs, draw, energy). It is not an
/// exhaustive search, and cards it can't see yet (draws) end a plan, after which it plans again. Stateless, so many fights can share one.
/// </summary>
/// <summary>Indexes of the terms the leaf value adds up (see <c>BasicBot.LeafValue</c>).</summary>
public static class LeafFeatures
{
    public const int Lost = 0, Future = 1, Progress = 2, Drawn = 3, ReplanEnergy = 4, PowerGain = 5, WastedEnergy = 6, EnemyWeak = 7, EnemyVulnerable = 8,
        EnemiesAlive = 9, ExhaustedJunk = 10, ExhaustedCards = 11, PlayerDebuffs = 12, SelfDamage = 13, ExhaustLoss = 14, Count = 15;
    public static readonly string[] Names = { "Lost", "Future", "Progress", "Drawn", "ReplanEnergy", "PowerGain", "WastedEnergy", "EnemyWeak", "EnemyVulnerable", "EnemiesAlive", "ExhaustedJunk", "ExhaustedCards", "PlayerDebuffs", "SelfDamage", "ExhaustLoss" };
}

public sealed class BasicBot
{
    public const double MinScoreToPlay = 0.75;

    /// <summary>Most plays in one plan, how many candidate plays are tried at each step, and how many combat copies a plan may use.</summary>
    public BotTuning Tuning { get; init; } = BotTuning.Default;
    public int MaxDepth { get; init; } = 6;
    public int BranchWidth { get; init; } = 3;
    public int NodeBudget { get; init; } = 30;

    /// <summary>The best sequence for the rest of this turn; empty means end the turn.</summary>
    public List<BotAction> Plan(Combat combat)
    {
        foreach (var pile in new[] { combat.DrawPile, combat.Hand, combat.DiscardPile, combat.ExhaustPile })
            foreach (CardDef c in pile) c.Tag = 0;
        for (int i = 0; i < combat.Hand.Count; i++) combat.Hand[i].Tag = i + 1;

        var first = new PlanSearch(this, combat, danger: false, lethal: false);
        first.Run();

        // If even the best card play leaves a real threat standing, spend potions to answer it.
        Combat after = first.BestFinal ?? combat;
        int unblocked = Math.Max(0, after.IncomingDamage() - after.Block);
        bool lethal = unblocked >= after.Hp;
        if (combat.Potions.Count > 0 && (lethal || unblocked >= 0.35 * after.Hp))
        {
            var second = new PlanSearch(this, combat, danger: true, lethal: lethal);
            second.Run();
            if (second.BestScore > first.BestScore) return second.Best;
        }
        return first.Best;
    }

    private sealed class PlanSearch
    {
        private readonly BasicBot _bot;
        private readonly Combat _root;
        private readonly bool _danger, _lethal;
        private readonly Dictionary<ulong, double> _seen = new();
        private readonly ulong _salt;
        private int _nodes;
        private double PotionReserve => _danger ? (_lethal ? 0 : 6) : _root.Stakes switch { 2 => 3, 1 => 9, _ => 22 };

        public List<BotAction> Best { get; private set; } = new();
        public double BestScore { get; private set; }
        public Combat? BestFinal { get; private set; }

        public PlanSearch(BasicBot bot, Combat root, bool danger, bool lethal)
        {
            _bot = bot;
            _root = root;
            _danger = danger;
            _lethal = lethal;
            _salt = 0xC0FFEEUL + (ulong)root.Turn * 7919UL;
        }

        private readonly List<(List<BotAction> Path, Combat State, double Cheap)> _visited = new();
        public IReadOnlyList<(List<BotAction> Path, Combat State, double Cheap)> Visited => _visited;

        public void Run()
        {
            Visit(_root, 0, new List<BotAction>(), 0, 0);
            if (_bot.Tuning.Leaf <= 0) return;
            // Every plan was judged cheaply (the enemy phase once); the few best are judged again looking several turns ahead.
            BestScore = double.NegativeInfinity;
            foreach (var (path, state, _) in _visited.OrderByDescending(v => v.Cheap).Take((int)_bot.Tuning.Finalists))
            {
                double deep = _bot.LeafValue(_root, state, _salt, deep: true, plays: path.Count(a => !a.IsPotion)) - path.Count(a => a.IsPotion) * PotionReserve;
                if (deep > BestScore) { BestScore = deep; Best = path; BestFinal = state; }
            }
        }

        private void Visit(Combat state, double score, List<BotAction> path, int depth, ulong mask)
        {
            if (_bot.Tuning.Leaf > 0)
            {
                double cheap = _bot.LeafValue(_root, state, _salt, deep: false, plays: path.Count(a => !a.IsPotion)) - path.Count(a => a.IsPotion) * PotionReserve;
                _visited.Add((new List<BotAction>(path), state, cheap));
            }
            else if (score > BestScore)
            {
                BestScore = score;
                Best = new List<BotAction>(path);
                BestFinal = state;
            }
            if (depth >= _bot.MaxDepth || state.Result != CombatResult.Ongoing) return;
            // A card we haven't drawn for real yet can't be named in a plan; stop here and plan again after drawing.
            if (state.Hand.Any(c => c.Tag == 0)) return;

            List<(BotAction Action, double Score)> options = _bot.Candidates(state, _danger, _lethal);
            options.Sort((a, b) => b.Score.CompareTo(a.Score));
            for (int k = 0; k < options.Count && k < _bot.BranchWidth; k++)
            {
                if (_nodes >= _bot.NodeBudget) return;
                _nodes++;
                (BotAction action, double s) = options[k];
                Combat next = state.Clone(_salt);
                if (!Apply(next, action)) continue;
                double total = score + s;
                if (next.Result == CombatResult.Won) total += 100;
                else if (next.Result == CombatResult.Lost) continue;

                ulong nextMask = action.Tag > 0 && action.Tag < 64 ? mask | (1UL << action.Tag) : mask + (ulong)(action.PotionId?.Length ?? 0) * 0x1F3D5B79UL;
                ulong fp = Fingerprint(next, nextMask);
                if (_seen.TryGetValue(fp, out double old) && (_bot.Tuning.Leaf > 0 || old >= total - 1e-9)) continue;
                _seen[fp] = total;

                path.Add(action);
                Visit(next, total, path, depth + 1, nextMask);
                path.RemoveAt(path.Count - 1);
            }
        }

        private static ulong Fingerprint(Combat c, ulong mask)
        {
            ulong h = mask * 0x9E3779B97F4A7C15UL;
            h = (h ^ (ulong)(c.Energy + 31 * c.Block + 977 * c.Hp)) * 0xBF58476D1CE4E5B9UL;
            foreach (Enemy e in c.Enemies)
                h = (h ^ (ulong)(e.Hp + 4099 * e.Block + 65537 * e.Powers[(int)PowerKind.Vulnerable] + 1048583 * e.Powers[(int)PowerKind.Weak])) * 0x94D049BB133111EBUL;
            return h;
        }
    }

    /// <summary>Leaf mode: what stopping the turn in <paramref name="state"/> is worth. Plays out the enemy phase on a copy, then prices what is left: the HP lost now, plus each enemy's remaining HP at the rate its attacks cost us per point of damage we can deal.</summary>
    private double LeafValue(Combat root, Combat state, ulong salt, bool deep, int plays)
    {
        Span<double> f = stackalloc double[LeafFeatures.Count];
        return LeafValue(root, state, salt, deep, plays, f, out _);
    }

    /// <summary>Research: the leaf value of a plan's end state with its terms (<see cref="LeafFeatures"/>); <paramref name="terminal"/> is true when the fight ended by then and the terms don't apply.</summary>
    public double LeafValue(Combat root, Combat state, bool deep, int plays, Span<double> features, out bool terminal) =>
        LeafValue(root, state, 0xC0FFEEUL + (ulong)root.Turn * 7919UL, deep, plays, features, out terminal);

    private double LeafValue(Combat root, Combat state, ulong salt, bool deep, int plays, Span<double> f, out bool terminal)
    {
        f.Clear();
        terminal = true;
        // A plan that stops at a draw is planned again once the cards are seen. The planning copy has already drawn a sample of them,
        // so finish the turn on a copy (greedily, with the energy left) and judge the whole turn, instead of pricing the leftover energy
        // at a flat rate, which overrated drawing when the hand already had more cards than energy (Burning Pact, Drum of Battle).
        if (Tuning.FinishDraws > 0 && state.Result == CombatResult.Ongoing && state.Energy > 0 && state.Hand.Any(c => c.Tag == 0))
        {
            Combat finished = state.Clone(salt + 7);
            plays += GreedyFinish(finished);
            state = finished;
        }
        // Cards drawn this turn (by Shrug It Off, Battle Trance, Pommel Strike, ...) cycle the deck and give options later; the search can't play them now.
        int drawn = Math.Max(0, state.Hand.Count - root.Hand.Count + plays);
        f[LeafFeatures.Drawn] = drawn * (state.Energy > 0 ? 1.0 : 0.6);
        // A plan that ends on a draw is planned again with the new cards, so the energy it leaves is not wasted.
        // What that energy will buy depends on the deck: a Strike-and-Defend deck gets about half as much per energy as the bench decks
        // the weight was fit on, and valuing it at the bench rate made Burning Pact and Drum of Battle look better than playing a card.
        if (state.Hand.Any(c => c.Tag == 0)) f[LeafFeatures.ReplanEnergy] = state.Energy * EnergyScale(root);
        else f[LeafFeatures.WastedEnergy] = state.Energy;
        foreach (PowerKind kind in Enum.GetValues<PowerKind>())
        {
            int gained = state.PlayerPowers[(int)kind] - root.PlayerPowers[(int)kind];
            if (gained > 0 && PowerRules.IsPermanentBuff(kind)) f[LeafFeatures.PowerGain] += PowerValue(state, kind, gained);
        }
        for (int i = root.ExhaustPile.Count; i < state.ExhaustPile.Count; i++)
            if (state.ExhaustPile[i].Kind is CardKind.Status or CardKind.Curse) f[LeafFeatures.ExhaustedJunk]++;
            else f[LeafFeatures.ExhaustedCards]++;
        f[LeafFeatures.SelfDamage] = Math.Max(0, root.Hp - state.Hp);
        double powers = Tuning.DrawValue * f[LeafFeatures.Drawn] + Tuning.ReplanEnergy * f[LeafFeatures.ReplanEnergy] + Tuning.PowerGain * f[LeafFeatures.PowerGain]
            + Tuning.SelfDamage * f[LeafFeatures.SelfDamage] + Tuning.ExhaustedJunk * f[LeafFeatures.ExhaustedJunk] + Tuning.ExhaustedCards * f[LeafFeatures.ExhaustedCards];
        if (state.Result == CombatResult.Won) return powers + 50;
        Combat c = state.Clone(salt);
        int hpBefore = c.Hp;
        c.EndPlayerTurn(startNextTurn: false);
        if (c.Result == CombatResult.Lost) return -100000;
        double lost = hpBefore - c.Hp;
        if (c.Result == CombatResult.Won) return powers + 50 - Tuning.LostWeight * lost;
        terminal = false;
        f[LeafFeatures.Lost] = lost;
        double dpt = DamagePerTurn(c);
        // How hard the enemies will hit over the next few turns if we did nothing: this sees Ritual-style growth and charge-up turns.
        int horizon = deep ? (int)Math.Max(1, Tuning.Horizon) : 1;
        var perEnemy = new Dictionary<int, double>();
        foreach (Enemy e in c.Enemies) perEnemy[e.Index] = c.IntentDamage(e);
        if (horizon > 1)
        {
            Combat ahead = c.Clone(salt + 1);
            for (int k = 1; k < horizon && ahead.Result == CombatResult.Ongoing; k++)
            {
                if (ahead.PlayerPowers[(int)PowerKind.Barricade] == 0) ahead.Block = 0;   // the next turn would have cleared it
                ahead.EndPlayerTurn(startNextTurn: false);
            }
            foreach (Enemy e in ahead.Enemies)
                if (e.DamageDealt > 0) perEnemy[e.Index] = perEnemy.GetValueOrDefault(e.Index) + e.DamageDealt;
        }
        double incoming = perEnemy.Values.Sum() / horizon;
        double now = incoming;
        double scale = now > 0 ? Math.Max(0, incoming - Tuning.SpareBlock) / now : 0;
        // Killing the enemies in the best order (highest damage per HP first) decides how long each keeps hitting: the future cost is each
        // enemy's damage per turn times the turns until it dies, and the turns are the HP killed up to and including it over our damage per turn.
        var threats = new List<(double Threat, double Hp)>();
        double progress = 0;
        foreach (Enemy e in c.Enemies)
        {
            if (!e.Alive || e.Dying) continue;
            int vulnerable = e.Powers[(int)PowerKind.Vulnerable];
            double effective = vulnerable > 0 ? e.Hp - Math.Min(e.Hp / 3.0, dpt * Math.Min(vulnerable, 2) / 3.0) : e.Hp;   // Vulnerable is worth a third of the damage we deal while it lasts
            progress += effective;
            double threat = perEnemy.GetValueOrDefault(e.Index) / horizon * scale + (e.Primary ? 0 : 0.5);
            // A sleeping or charging enemy still has attacks coming later, so it never counts as harmless.
            var attacks = e.Def.Moves.Values.Where(m => m.Damage > 0).ToList();
            if (attacks.Count > 0) threat = Math.Max(threat, Tuning.BaseThreat * attacks.Average(m => (double)m.Damage * m.HitsAt(c.Ascension)) * scale);
            // Enemies that leave others behind when they die (Infested's four Wrigglers, the Gremlin Merc's gremlins) aren't finished by killing them.
            if (e.Powers[(int)PowerKind.Infested] > 0) { effective += 80; threat += 12 * scale; }
            else if (e.Powers[(int)PowerKind.Surprise] > 0 || e.Def.Innate.Any(p => p.Power == PowerKind.Surprise)) { effective += 45; threat += 10 * scale; }
            threats.Add((threat, Math.Max(1, effective)));
            f[LeafFeatures.EnemiesAlive]++;
            if (attacks.Count > 0) f[LeafFeatures.EnemyWeak] += Math.Min(3, e.Powers[(int)PowerKind.Weak]);
            f[LeafFeatures.EnemyVulnerable] += Math.Min(3, vulnerable);
        }
        threats.Sort((a, b) => (b.Threat / b.Hp).CompareTo(a.Threat / a.Hp));
        double cumulative = 0, future = 0;
        foreach (var (threat, hp) in threats)
        {
            cumulative += hp;
            future += threat * cumulative / dpt;
        }
        f[LeafFeatures.Future] = future;
        f[LeafFeatures.Progress] = progress;
        f[LeafFeatures.ExhaustLoss] = ExhaustLoss(root, state, c, progress, dpt);
        foreach (PowerKind debuff in new[] { PowerKind.Weak, PowerKind.Frail, PowerKind.Vulnerable })
            f[LeafFeatures.PlayerDebuffs] += Math.Min(3, c.PlayerPowers[(int)debuff]);
        // The Insatiable's Sandpit ends the run when its countdown reaches 0; every point of margin is worth a lot when it is short.
        int sand = c.PlayerPowers[(int)PowerKind.Sandpit];
        double sandpit = sand <= 0 ? 0 : 300 * Math.Pow(0.35, sand - 1);
        return powers - Tuning.LostWeight * lost - Tuning.FutureWeight * future - Tuning.Progress * progress - sandpit
            + Tuning.WastedEnergy * f[LeafFeatures.WastedEnergy] + Tuning.EnemyWeak * f[LeafFeatures.EnemyWeak] + Tuning.EnemyVulnerable * f[LeafFeatures.EnemyVulnerable]
            + Tuning.EnemiesAlive * f[LeafFeatures.EnemiesAlive] + Tuning.PlayerDebuffs * f[LeafFeatures.PlayerDebuffs]
            - Tuning.ExhaustLoss * f[LeafFeatures.ExhaustLoss];
    }

    /// <summary>
    /// What the cards exhausted this turn would still have done in this fight, in HP: each would have been drawn about (turns left x 5 /
    /// cards in the deck, at most once a turn) more times, and while the deck is bigger than a hand each of those draws becomes a draw of an
    /// average card instead, so the loss per card is (its worth - the average card's worth) x those draws. Exhausting a Bash costs, a Strike in a deck of Strikes costs about nothing, and a status
    /// or curse (worth nothing) gains. Turns left come from the enemies' remaining HP over the deck's damage per turn.
    /// </summary>
    private static double ExhaustLoss(Combat root, Combat state, Combat after, double enemyHpLeft, double dpt)
    {
        int exhausted = state.ExhaustPile.Count - root.ExhaustPile.Count;
        if (exhausted <= 0) return 0;
        var deck = after.DrawPile.Concat(after.DiscardPile).Concat(after.Hand).ToList();
        if (deck.Count == 0) return 0;
        double mean = deck.Average(PlayWorth);
        // A card can be drawn at most once a turn, and an exhausted card is only replaced by other draws while the deck is bigger than a
        // hand: at 10+ cards an average card takes its place, at 5 or fewer nothing does (without this the bot thinned its deck down
        // to Burning Pact alone and lost).
        double draws = Math.Clamp(enemyHpLeft / dpt, 0, 10) * Math.Min(1.0, 5.0 / deck.Count);
        double replaced = Math.Clamp((deck.Count - 5) / 5.0, 0, 1);
        double loss = 0;
        for (int i = root.ExhaustPile.Count; i < state.ExhaustPile.Count; i++)
        {
            CardDef card = state.ExhaustPile[i];
            // Thinning a playable card out gains little (a hand already holds more cards than energy; an unplayable card costs only ~1.5
            // HP per draw by sim cardvalue), so only exhausting a better-than-average card counts, as a loss. A status or curse gains
            // that ~1.5 per draw it would have clogged.
            if (card.Kind is CardKind.Status or CardKind.Curse || card.Cost == CardDef.Unplayable) loss -= JunkDrawCost * draws;
            else loss += Math.Max(0, PlayWorth(card) - replaced * mean) * draws;
        }
        return loss;
    }

    /// <summary>The bench decks' average damage per energy (<see cref="DamageRate"/>: starter 3.5, mid 3.5, built 3.9, blood 5.1), the rate <see cref="BotTuning.Dpt"/> was fit at.</summary>
    public const double BenchDamageRate = 4.0;

    /// <summary>
    /// The damage per turn this deck deals, for how long enemies live: <see cref="BotTuning.Dpt"/> (fit on the bench decks) scaled by this
    /// deck's damage per energy, Strength included, against the bench decks'. A fixed rate made a Strike-and-Defend deck think its
    /// attacks shortened fights less than they do, so it blocked where it should have attacked (Expect a Fight, Drum of Battle).
    /// </summary>
    private double DamagePerTurn(Combat c) =>
        Tuning.Dpt * Math.Clamp(DamageRate(c.DrawPile.Concat(c.DiscardPile).Concat(c.Hand), c.PlayerPowers[(int)PowerKind.Strength]) / BenchDamageRate, 0.4, 2.0);

    /// <summary>Damage per energy of the playable cards (0-cost cards count as half an energy, X cards as one), with this much Strength.</summary>
    public static double DamageRate(IEnumerable<CardDef> cards, int strength)
    {
        double damage = 0, energy = 0;
        foreach (CardDef c in cards)
        {
            if (c.Kind is CardKind.Status or CardKind.Curse || c.Cost == CardDef.Unplayable) continue;
            foreach (Effect e in c.Effects)
                if (e.Op is EffectOp.Damage or EffectOp.DamageAll or EffectOp.DamageRandom)
                    damage += Math.Max(0, e.Amount + c.BonusDamage + strength) * Math.Max(1, e.Hits);
            energy += c.Cost < 0 ? 1 : Math.Max(0.5, c.Cost);
        }
        return energy == 0 ? 0 : damage / energy;
    }

    /// <summary>The bench decks' average worth per energy (<see cref="EnergyWorth"/>: starter 5.7, mid 10.3, built 12.1, blood 10.1), the rate <see cref="BotTuning.ReplanEnergy"/> was fit at.</summary>
    public const double BenchEnergyWorth = 9.5;

    /// <summary>This deck's worth per energy against the bench decks', bounded to 0.3-1.5.</summary>
    private static double EnergyScale(Combat root) => Math.Clamp(EnergyWorth(root.DrawPile.Concat(root.DiscardPile).Concat(root.Hand)) / BenchEnergyWorth, 0.3, 1.5);

    /// <summary>Average worth per energy (rough HP, before the cost penalty in <c>KeepValue</c>) of the playable cards that cost energy.</summary>
    public static double EnergyWorth(IEnumerable<CardDef> cards)
    {
        double total = 0;
        int n = 0;
        foreach (CardDef c in cards)
        {
            if (c.Kind is CardKind.Status or CardKind.Curse || c.Cost == CardDef.Unplayable || c.Cost < 1) continue;
            total += (CardChoices.KeepValue(c) + 0.4 * c.Cost) / 0.3 / c.Cost;
            n++;
        }
        return n == 0 ? BenchEnergyWorth : total / n;
    }

    /// <summary>What drawing an unplayable card costs in a fight, in HP (Injury in <c>sim cardvalue</c>: 3-5 HP a fight over ~3 draws).</summary>
    private const double JunkDrawCost = 1.5;

    /// <summary>A card's worth per draw in rough HP (a point of damage or block is about one): 0 for statuses, curses and unplayable cards.</summary>
    private static double PlayWorth(CardDef card) =>
        card.Kind is CardKind.Status or CardKind.Curse || card.Cost == CardDef.Unplayable ? 0 : Math.Max(0, CardChoices.KeepValue(card) / 0.3);

    /// <summary>Plays the rest of a turn greedily by the per-play scores (no search), by hand position so unseen cards need no tags. Returns the plays made.</summary>
    private int GreedyFinish(Combat combat)
    {
        int played = 0;
        for (int step = 0; step < 10 && combat.Result == CombatResult.Ongoing; step++)
        {
            int incoming = combat.IncomingDamage();
            int needBlock = Math.Max(0, incoming - combat.Block);
            bool lethalNow = incoming - combat.Block >= combat.Hp;
            double best = MinScoreToPlay;
            int bestIndex = -1, bestTarget = -1;
            for (int i = 0; i < combat.Hand.Count; i++)
            {
                CardDef card = combat.Hand[i];
                if (!combat.CanPlay(card)) continue;
                int cost = combat.EffectiveCost(card);
                int energyAfter = cost == CardDef.XCost ? 0 : combat.Energy - cost;
                foreach (int t in CandidateTargets(combat, Combat.NeedsTarget(card)))
                {
                    double score = ScoreEffects(combat, card.Effects, card, i, t, needBlock, lethalNow, energyAfter, combat.Hand.Count - 1);
                    if (score > best) { best = score; bestIndex = i; bestTarget = t; }
                }
            }
            if (bestIndex < 0) break;
            combat.Play(bestIndex, bestTarget);
            played++;
        }
        return played;
    }

    /// <summary>Research: the plans the leaf search considered for this turn (path and end state), without potions.</summary>
    public List<(List<BotAction> Path, Combat State)> CandidatePlans(Combat combat)
    {
        foreach (var pile in new[] { combat.DrawPile, combat.Hand, combat.DiscardPile, combat.ExhaustPile })
            foreach (CardDef c in pile) c.Tag = 0;
        for (int i = 0; i < combat.Hand.Count; i++) combat.Hand[i].Tag = i + 1;
        var search = new PlanSearch(this, combat, danger: false, lethal: false);
        search.Run();
        return search.Visited.Where(v => v.Path.All(a => !a.IsPotion)).Select(v => (v.Path, v.State)).ToList();
    }

    /// <summary>Carries out one planned action on a combat (a copy while planning, the real one when playing). False if it can't be done.</summary>
    public static bool Apply(Combat combat, BotAction action)
    {
        if (action.IsPotion)
        {
            int p = combat.Potions.FindIndex(x => x.Id == action.PotionId);
            if (p < 0 || !combat.CanUsePotion(combat.Potions[p])) return false;
            combat.UsePotion(p, action.Target);
            return true;
        }
        int i = combat.Hand.FindIndex(c => c.Tag == action.Tag);
        if (i < 0 || !combat.CanPlay(combat.Hand[i])) return false;
        combat.Play(i, action.Target);
        return true;
    }

    private List<(BotAction, double)> Candidates(Combat combat, bool danger, bool lethalDanger)
    {
        var list = new List<(BotAction, double)>();
        int incoming = combat.IncomingDamage();
        int needBlock = Math.Max(0, incoming - combat.Block);
        bool lethalNow = incoming - combat.Block >= combat.Hp;

        var seenKeys = new HashSet<(string, bool, int, int, bool)>();
        for (int i = 0; i < combat.Hand.Count; i++)
        {
            CardDef card = combat.Hand[i];
            if (!combat.CanPlay(card)) continue;
            int cost = combat.EffectiveCost(card);
            if (!seenKeys.Add((card.Id, card.Upgraded, card.BonusDamage, cost, card.FreeThisTurn))) continue;
            int energyAfter = cost == CardDef.XCost ? 0 : combat.Energy - cost;

            foreach (int t in CandidateTargets(combat, Combat.NeedsTarget(card)))
            {
                double score = ScoreEffects(combat, card.Effects, card, i, t, needBlock, lethalNow, energyAfter, combat.Hand.Count - 1);
                if (Tuning.Leaf > 0 && GivesSpareEnergy(combat, card, cost)) score = Math.Max(score, 50);
                if (score >= MinScoreToPlay) list.Add((new BotAction(card.Tag, null, t), score));
            }
        }

        double reserve = danger ? (lethalDanger ? 0 : 6) : combat.Stakes switch { 2 => 3, 1 => 9, _ => 22 };
        var seenPotions = new HashSet<string>();
        foreach (PotionDef potion in combat.Potions)
        {
            if (!combat.CanUsePotion(potion) || !seenPotions.Add(potion.Id)) continue;
            foreach (int t in CandidateTargets(combat, potion.NeedsTarget))
            {
                double score = ScoreEffects(combat, potion.Effects, potion.AsCard, -1, t, needBlock, lethalNow, combat.Energy, combat.Hand.Count) - reserve;
                if (potion.Id == "BLOOD_POTION") score += HealBonus(combat);
                if (score >= MinScoreToPlay) list.Add((new BotAction(0, potion.Id, t), score));
            }
        }
        return list;
    }

    /// <summary>
    /// A play that gives back more energy than it costs (Bloodletting) while the rest of the hand costs more than the energy left. Its
    /// own score can't see what the energy buys, so without this the search cut it before trying it and then spent the energy
    /// elsewhere, leaving Bloodletting a dead card. The plan's judgement still decides whether it is worth its drawback.
    /// </summary>
    private static bool GivesSpareEnergy(Combat combat, CardDef card, int cost)
    {
        if (cost == CardDef.XCost) return false;
        int gain = card.Effects.Where(e => e.Op == EffectOp.Energy && e.AmountSource == Source.None).Sum(e => e.Amount);
        if (gain <= cost) return false;
        int wanted = combat.Hand.Where(o => o != card && combat.CanPlayIgnoringEnergy(o)).Sum(o => combat.EffectiveCost(o) == CardDef.XCost ? 2 : Math.Max(0, combat.EffectiveCost(o)));   // an X card wants whatever is spare
        return wanted > combat.Energy - cost;
    }

    /// <summary>Healing is worth much more when HP is low.</summary>
    private static double HealBonus(Combat combat) => (double)combat.Hp / combat.MaxHp < 0.4 ? 0.2 * combat.MaxHp : 0;

    private static IEnumerable<int> CandidateTargets(Combat combat, bool needsTarget)
    {
        if (!needsTarget) { yield return -1; yield break; }
        foreach (Enemy e in combat.Enemies)
            if (e.Alive && !e.Dying) yield return e.Index;
    }

    /// <summary>What playing these effects is worth right now. <paramref name="card"/> is the card they belong to (a potion's effects use a stand-in); handIndex is -1 for a potion.</summary>
    private double ScoreEffects(Combat combat, Effect[] effects, CardDef card, int handIndex, int target, int needBlock, bool lethalDanger, int energyAfter, int handOthers)
    {
        double score = 0;
        Enemy? focus = target >= 0 ? combat.Enemies[target] : null;
        int x = combat.Energy;
        bool isCard = handIndex >= 0;

        foreach (Effect effect in effects)
        {
            int amount = combat.EffectAmount(effect, card, focus, x, handOthers);
            int hits = combat.EffectHits(effect, card, focus, x, handOthers);
            switch (effect.Op)
            {
                case EffectOp.Damage when focus != null:
                    score += DamageValue(combat, focus, amount, hits, card: isCard ? card : null);
                    break;
                case EffectOp.DamageFlat when focus != null:
                    score += DamageValue(combat, focus, amount, 1, powered: false);
                    break;
                case EffectOp.DamageAll:
                    foreach (Enemy e in combat.Enemies)
                        if (e.Alive && !e.Dying) score += DamageValue(combat, e, amount, hits, card: isCard ? card : null);
                    break;
                case EffectOp.DamageAllFlat:
                    foreach (Enemy e in combat.Enemies)
                        if (e.Alive && !e.Dying) score += DamageValue(combat, e, amount, 1, powered: false);
                    break;
                case EffectOp.DamageRandom:
                    {
                        var alive = combat.Enemies.Where(e => e.Alive && !e.Dying).ToList();
                        foreach (Enemy e in alive) score += DamageValue(combat, e, amount, hits, card: isCard ? card : null) / alive.Count;
                        break;
                    }
                case EffectOp.Block:
                    {
                        int gain = combat.PlayerBlockGain(amount, isCard ? card : null) * Math.Max(1, hits);
                        score += BlockValue(Tuning, gain, needBlock, lethalDanger);
                        break;
                    }
                case EffectOp.BlockFlat:
                    score += BlockValue(Tuning, amount, needBlock, lethalDanger);
                    break;
                case EffectOp.DoubleBlock:
                    score += BlockValue(Tuning, combat.Block, needBlock, lethalDanger);
                    break;
                case EffectOp.ExhaustNonAttacksForBlock:
                    {
                        int count = combat.Hand.Count(c => c.Kind != CardKind.Attack && c != card);
                        int gain = combat.PlayerBlockGain(amount, isCard ? card : null) * count;
                        score += BlockValue(Tuning, gain, needBlock, lethalDanger) - 0.4 * count;
                        break;
                    }
                case EffectOp.Draw:
                    score += amount * (energyAfter > 0 ? Tuning.Draw : 0.8);
                    break;
                case EffectOp.DrawUntilNonAttack:
                    score += energyAfter > 0 ? Tuning.Draw : 0.8;
                    break;
                case EffectOp.Energy:
                    score += amount * (HasPlayableCardLeft(combat, handIndex) ? Tuning.Energy : 0.5);
                    break;
                case EffectOp.LoseHp:
                    {
                        double hpFraction = (double)combat.Hp / combat.MaxHp;
                        score -= amount * (1 + Math.Max(0, (0.5 - hpFraction) * 4));
                        break;
                    }
                case EffectOp.Heal:
                    score += Math.Min(amount, combat.MaxHp - combat.Hp) * 1.0;
                    break;
                case EffectOp.HealPercent:
                    score += Math.Min(amount * combat.MaxHp / 100.0, combat.MaxHp - combat.Hp) * 1.0;
                    break;
                case EffectOp.GainMaxHp:
                    score += amount * 0.5;
                    break;
                case EffectOp.GainMaxHpIfFatal:
                    if (focus != null && focus.Primary && combat.PlayerAttackDamage(card.Effects[0].Amount, focus) >= focus.Hp + focus.Block) score += amount * 1.5;
                    break;
                case EffectOp.DebuffEnemy when focus != null:
                    score += DebuffValue(combat, focus, effect.Power, amount, handIndex);
                    break;
                case EffectOp.DebuffAll:
                    foreach (Enemy e in combat.Enemies)
                        if (e.Alive && !e.Dying) score += DebuffValue(combat, e, effect.Power, amount, handIndex);
                    break;
                case EffectOp.DoubleDebuff when focus != null:
                    score += focus.Powers[(int)effect.Power] * 1.5;
                    break;
                case EffectOp.BuffEnemy:
                    score -= amount * 3;
                    break;
                case EffectOp.StrengthFromVulnerable when focus != null:
                    score += Math.Max(focus.Powers[(int)PowerKind.Vulnerable], 1) * 6;
                    break;
                case EffectOp.DebuffSelf:
                    score -= effect.Power == PowerKind.NoDraw ? 3 : amount * 2;
                    break;
                case EffectOp.BuffSelf:
                    score += PowerValue(combat, effect.Power, amount) * (effect.Power is PowerKind.Strength or PowerKind.Dexterity ? 1 : Tuning.Powers);
                    break;
                case EffectOp.ExhaustRandomFromHand:
                    score -= 0.7;
                    break;
                case EffectOp.ExhaustChosenFromHand:
                    {
                        CardDef? worst = CardChoices.WorstToExhaust(combat.Hand.Where(c => c != card).ToList());
                        score += worst == null ? 0 : worst.Kind is CardKind.Status or CardKind.Curse ? 3 : -0.5;
                        break;
                    }
                case EffectOp.ExhaustHand:
                    foreach (CardDef c in combat.Hand)
                        if (c != card) score += c.Kind is CardKind.Status or CardKind.Curse ? 1 : -0.4;
                    break;
                case EffectOp.UpgradeChosenInHand:
                    score += combat.Hand.Any(c => c != card && c.UpgradedForm != null) ? 2 : 0;
                    break;
                case EffectOp.UpgradeAllInHand:
                    score += 2 * combat.Hand.Count(c => c != card && c.UpgradedForm != null);
                    break;
                case EffectOp.CopyToDiscard:
                    score += 1;
                    break;
                case EffectOp.PlayTopOfDraw:
                    score += amount * 4;
                    break;
                case EffectOp.DiscardToDrawTop:
                    score += combat.DiscardPile.Count > 0 ? 1.5 : 0;
                    break;
                case EffectOp.GenerateForExhausted:
                    score += 3 * Math.Max(0, handOthers);
                    break;
                case EffectOp.GenerateFreeAttack:
                    score += 6;
                    break;
                case EffectOp.AbsorbRandomAttack:
                    score += combat.Hand.Any(c => c != card && c.Kind == CardKind.Attack) ? 2 : 0;
                    break;
                case EffectOp.TransformAttacks:
                    break;
                case EffectOp.Special:
                    score += SpecialValue(combat, (SpecialEffect)effect.Arg, amount);
                    break;
            }
        }

        if (!isCard) return score;

        // Getting a Beckon/Toxic-style status card out of the hand avoids what it would cost at the end of the turn.
        score += card.EndTurnHpLoss + 0.7 * card.EndTurnDamage;

        if (card.Exhaust && card.Kind != CardKind.Power) score -= 1.5;
        if (card.Approximate && score < 1) score = 1;
        return score;
    }

    /// <summary>Block is worth its full price only up to what the incoming attacks need (much more if they would be lethal).</summary>
    private static double BlockValue(BotTuning t, int gain, int needBlock, bool lethalDanger)
    {
        int useful = Math.Min(gain, needBlock);
        return useful * (lethalDanger ? 3.0 : t.Block) + (gain - useful) * t.ExcessBlock;
    }

    /// <summary>What the potion effects that need their own code are worth, in the same rough units as damage dealt.</summary>
    private static double SpecialValue(Combat combat, SpecialEffect kind, int amount)
    {
        int hand = combat.Hand.Count;
        return kind switch
        {
            SpecialEffect.Clarity => 9,
            SpecialEffect.RadiantTincture => 10,
            SpecialEffect.ShipInABottle => 2 * amount * 0.9,
            SpecialEffect.Duplicator => 10,
            SpecialEffect.Gigantification => 20,
            SpecialEffect.StableSerum => 4,
            SpecialEffect.BottledPotential => 8,
            SpecialEffect.Ashwater => 2,
            SpecialEffect.GamblersBrew => 3,
            SpecialEffect.DropletOfPrecognition => 6,
            SpecialEffect.LiquidMemories => 7,
            SpecialEffect.EntropicBrew => 6,
            SpecialEffect.Glowwater => hand <= 2 ? 10 : 4,
            SpecialEffect.SneckoOil => 8,
            SpecialEffect.SoldiersStew => 10,
            SpecialEffect.OrobicAcid => 16,
            SpecialEffect.GenerateColorless => 9,
            SpecialEffect.GenerateSkill => 9,
            SpecialEffect.GeneratePower => 11,
            SpecialEffect.TouchOfInsanity => 6,
            SpecialEffect.FoulPotion => 0.5 * amount,
            _ => 0,
        };
    }

    /// <summary>A card drawn and a point of block, in the leaf's rough HP units (a draw is about what a dead card costs, sim cardvalue).</summary>
    private const double DrawWorth = 1.5, BlockWorth = 0.7;

    /// <summary>
    /// How often this deck does something per turn (cards that exhaust, lose HP, apply Vulnerable, gain block or are Strikes, per card
    /// drawn, times five draws). Powers that trigger on it (Dark Embrace, Feel No Pain, Rupture, Vicious, Juggernaut, Hellraiser) are worth
    /// that rate times the turns left: fixed values made the bot spend 2 energy on Dark Embrace in decks that rarely exhaust (worse than
    /// a dead card in sim cardvalue) and undervalue it in decks built around it.
    /// </summary>
    private static double ThemeRate(Combat combat, Theme theme)
    {
        double total = 0;
        int n = 0;
        foreach (var pile in new[] { combat.DrawPile, combat.DiscardPile, combat.Hand })
            foreach (CardDef c in pile) { total += Synergy.Enables(c, theme); n++; }
        return n == 0 ? 0 : 5.0 * total / n;
    }

    /// <summary>What a power is worth to have from now on, in the same rough units as damage dealt.</summary>
    private double PowerValue(Combat combat, PowerKind power, int amount)
    {
        double turnsLeft = Math.Max(2, 8 - combat.Turn);
        return power switch
        {
            PowerKind.Strength => amount * Tuning.Strength,
            PowerKind.Dexterity => amount * Tuning.Dexterity,
            PowerKind.Plating or PowerKind.Metallicize => amount * 5,
            PowerKind.TempStrength => amount * 3,
            PowerKind.TempDexterity => amount * 2.5,
            PowerKind.Buffer => amount * 10,
            PowerKind.Regen => amount * (amount + 1) / 2.0 * 0.9,
            PowerKind.Ritual => amount * turnsLeft * 3,
            PowerKind.Barricade => 12,
            PowerKind.Corruption => 12,
            PowerKind.CrimsonMantle => amount * turnsLeft * 0.4 - turnsLeft * 0.8,
            PowerKind.CrimsonSelfDamage or PowerKind.InfernoSelfDamage => 0,
            PowerKind.Cruelty => 4,
            PowerKind.DarkEmbrace => ThemeRate(combat, Theme.Exhaust) * turnsLeft * amount * DrawWorth,
            PowerKind.DemonForm => amount * turnsLeft * 0.9,
            PowerKind.FeelNoPain => ThemeRate(combat, Theme.Exhaust) * turnsLeft * amount * BlockWorth,
            PowerKind.Hellraiser => ThemeRate(combat, Theme.Strike) * turnsLeft * 4,
            PowerKind.Inferno => amount * 1.2,
            PowerKind.Juggernaut => ThemeRate(combat, Theme.Block) * turnsLeft * amount * 0.8,
            PowerKind.Juggling => 6,
            PowerKind.Pyre => amount * turnsLeft * 1.2,
            PowerKind.Rage => amount * 2.5,
            PowerKind.Rupture => ThemeRate(combat, Theme.SelfDamage) * turnsLeft * amount * Tuning.Strength * 0.5,
            PowerKind.Stampede => 8,
            PowerKind.Unmovable => 6,
            PowerKind.Vicious => ThemeRate(combat, Theme.Vulnerable) * turnsLeft * amount * DrawWorth,
            PowerKind.Aggression => 8,
            PowerKind.Colossus => amount * 4,
            PowerKind.FlameBarrier => amount * Math.Max(1, combat.Enemies.Count(e => e.Alive && combat.IntendsAttack(e))) * 1.5,
            PowerKind.OneTwoPunch => amount * 5,
            PowerKind.FreeAttack => 4,
            // Every point of sandpit is a turn of life; it is worth a lot when the countdown is short and little while it is long.
            PowerKind.Sandpit => amount * (combat.PlayerPowers[(int)PowerKind.Sandpit] <= 2 ? 60 : combat.PlayerPowers[(int)PowerKind.Sandpit] <= 3 ? 14 : 1.5),
            _ => amount * 4,
        };
    }

    /// <summary>HP an enemy would actually lose from these hits, given its block and defensive powers.</summary>
    private static int HpLoss(Enemy enemy, int perHit, int hits)
    {
        int block = enemy.Block, lost = 0;
        int slippery = enemy.Powers[(int)PowerKind.Slippery];
        int shellLeft = enemy.Powers[(int)PowerKind.HardenedShell] > 0 ? enemy.Powers[(int)PowerKind.HardenedShell] - enemy.ShellDamage : int.MaxValue;
        bool intangible = enemy.Powers[(int)PowerKind.Intangible] > 0;
        for (int h = 0; h < hits; h++)
        {
            int d = intangible ? Math.Min(perHit, 1) : perHit;
            int absorbed = Math.Min(block, d);
            block -= absorbed;
            int through = d - absorbed;
            if (through > 0 && slippery > 0) { through = 1; slippery--; }
            through = Math.Max(0, Math.Min(through, shellLeft));
            shellLeft -= through;
            lost += through;
        }
        return lost;
    }

    private double DamageValue(Combat combat, Enemy enemy, int baseDamage, int hits, bool powered = true, CardDef? card = null)
    {
        int perHit = powered ? combat.PlayerAttackDamage(baseDamage, enemy, card) : baseDamage;
        int total = perHit * hits;
        int hpDamage = Math.Min(HpLoss(enemy, perHit, hits), enemy.Hp);
        double value = (hpDamage + Math.Min(total - hpDamage, enemy.Block) * 0.3) * Tuning.Damage;

        double threat = 3 + combat.IntentDamage(enemy);
        // Damage that finishes an enemy also removes everything it would have done later.
        if (hpDamage >= enemy.Hp) value += (4 + threat) * Tuning.Kill;
        else value += threat * hpDamage / enemy.Hp * Tuning.Threat;

        // Minions don't end the fight; don't spend the turn on them while a real enemy is still up.
        if (!enemy.Primary && combat.Enemies.Any(e => e.Alive && e.Primary)) value *= 0.6;
        return value;
    }

    private double DebuffValue(Combat combat, Enemy enemy, PowerKind power, int amount, int handIndex)
    {
        int turns = Math.Min(amount, 2);
        switch (power)
        {
            case PowerKind.Vulnerable when enemy.Powers[(int)PowerKind.Vulnerable] == 0:
                {
                    // Extra damage from the other attacks we can still afford this turn, plus a little for later turns.
                    double followUp = 0;
                    int energy = combat.Energy;
                    for (int i = 0; i < combat.Hand.Count; i++)
                    {
                        if (i == handIndex) continue;
                        CardDef other = combat.Hand[i];
                        int cost = combat.EffectiveCost(other);
                        if (!other.IsAttack || cost > energy || cost < 0) continue;
                        foreach (Effect e in other.Effects)
                            if (e.Op is EffectOp.Damage or EffectOp.DamageAll) followUp += e.Amount * Math.Max(1, e.Hits) * 0.5;
                    }
                    return (followUp + turns * 1.5) * (Tuning.Vulnerable / 0.5);
                }
            case PowerKind.Vulnerable:
                return 0.5 + turns * 0.3;
            case PowerKind.Weak when enemy.Powers[(int)PowerKind.Weak] == 0:
                {
                    double incoming = combat.IntentDamage(enemy);
                    return incoming * Tuning.Weak * turns + 0.5;
                }
            case PowerKind.Poison:
                return amount * 2.5;
            case PowerKind.Shrink:
                return Math.Min(amount, 4) * combat.IntentDamage(enemy) * 0.3 + 0.5;
            case PowerKind.Demise:
                return amount * 4;
            case PowerKind.TempStrengthDown:
                {
                    int hits = enemy.Move is { IsAttack: true } m ? m.HitsAt(combat.Ascension) : 0;
                    return Math.Min(amount, 12) * hits * 0.9 + 0.5;
                }
            default:
                return 0.5;
        }
    }

    /// <summary>True when another paid card is waiting in hand, so extra energy would actually get used.</summary>
    private static bool HasPlayableCardLeft(Combat combat, int excludingHandIndex)
    {
        for (int i = 0; i < combat.Hand.Count; i++)
            if (i != excludingHandIndex && combat.Hand[i].Cost >= 1) return true;
        return false;
    }
}
