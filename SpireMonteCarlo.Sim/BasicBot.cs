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
        // Cards drawn this turn (by Shrug It Off, Battle Trance, Pommel Strike, ...) cycle the deck and give options later; the search can't play them now.
        int drawn = Math.Max(0, state.Hand.Count - root.Hand.Count + plays);
        double powers = Tuning.DrawValue * drawn * (state.Energy > 0 ? 1.0 : 0.6);
        // A plan that ends on a draw is planned again with the new cards, so the energy it leaves is not wasted.
        if (state.Hand.Any(c => c.Tag == 0)) powers += Tuning.ReplanEnergy * state.Energy;
        foreach (PowerKind kind in Enum.GetValues<PowerKind>())
        {
            int gained = state.PlayerPowers[(int)kind] - root.PlayerPowers[(int)kind];
            if (gained > 0 && PowerRules.IsPermanentBuff(kind)) powers += PowerValue(state, kind, gained) * Tuning.PowerGain;
        }
        if (state.Result == CombatResult.Won) return powers + 50;
        Combat c = state.Clone(salt);
        int hpBefore = c.Hp;
        c.EndPlayerTurn(startNextTurn: false);
        if (c.Result == CombatResult.Lost) return -100000;
        double lost = hpBefore - c.Hp;
        if (c.Result == CombatResult.Won) return powers + 50 - lost;
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
            double effective = vulnerable > 0 ? e.Hp - Math.Min(e.Hp / 3.0, Tuning.Dpt * Math.Min(vulnerable, 2) / 3.0) : e.Hp;   // Vulnerable is worth a third of the damage we deal while it lasts
            progress += effective;
            double threat = perEnemy.GetValueOrDefault(e.Index) / horizon * scale + (e.Primary ? 0 : 0.5);
            // A sleeping or charging enemy still has attacks coming later, so it never counts as harmless.
            var attacks = e.Def.Moves.Values.Where(m => m.Damage > 0).ToList();
            if (attacks.Count > 0) threat = Math.Max(threat, Tuning.BaseThreat * attacks.Average(m => (double)m.Damage * m.HitsAt(c.Ascension)) * scale);
            // Enemies that leave others behind when they die (Infested's four Wrigglers, the Gremlin Merc's gremlins) aren't finished by killing them.
            if (e.Powers[(int)PowerKind.Infested] > 0) { effective += 80; threat += 12 * scale; }
            else if (e.Powers[(int)PowerKind.Surprise] > 0 || e.Def.Innate.Any(p => p.Power == PowerKind.Surprise)) { effective += 45; threat += 10 * scale; }
            threats.Add((threat, Math.Max(1, effective)));
        }
        threats.Sort((a, b) => (b.Threat / b.Hp).CompareTo(a.Threat / a.Hp));
        double cumulative = 0, future = 0;
        foreach (var (threat, hp) in threats)
        {
            cumulative += hp;
            future += threat * cumulative / Tuning.Dpt;
        }
        // The Insatiable's Sandpit ends the run when its countdown reaches 0; every point of margin is worth a lot when it is short.
        int sand = c.PlayerPowers[(int)PowerKind.Sandpit];
        double sandpit = sand <= 0 ? 0 : 300 * Math.Pow(0.35, sand - 1);
        return powers - lost - future - Tuning.Progress * progress - sandpit;
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
                        int gain = combat.PlayerBlockGain(amount) * Math.Max(1, hits);
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
                        int gain = combat.PlayerBlockGain(amount) * count;
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
            PowerKind.Barricade => 12,
            PowerKind.Corruption => 12,
            PowerKind.CrimsonMantle => amount * turnsLeft * 0.4 - turnsLeft * 0.8,
            PowerKind.CrimsonSelfDamage or PowerKind.InfernoSelfDamage => 0,
            PowerKind.Cruelty => 4,
            PowerKind.DarkEmbrace => 8,
            PowerKind.DemonForm => amount * turnsLeft * 0.9,
            PowerKind.FeelNoPain => amount * 2.5,
            PowerKind.Hellraiser => 8,
            PowerKind.Inferno => amount * 1.2,
            PowerKind.Juggernaut => amount * 1.5,
            PowerKind.Juggling => 6,
            PowerKind.Pyre => amount * turnsLeft * 1.2,
            PowerKind.Rage => amount * 2.5,
            PowerKind.Rupture => amount * 4,
            PowerKind.Stampede => 8,
            PowerKind.Unmovable => 6,
            PowerKind.Vicious => amount * 3,
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
