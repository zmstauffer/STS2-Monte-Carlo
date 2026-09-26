namespace SpireMonteCarlo.Sim;

public readonly record struct FightResult(bool Won, int HpLost, int Turns, int HpAfter, IReadOnlyList<PotionDef>? PotionsLeft = null, IReadOnlyList<string>? LostCards = null, int GoldSpent = 0, int MaxHpGained = 0);

public static class FightSimulator
{
    private const int MaxActionsPerTurn = 60;

    /// <summary>Research switch: when above 0 every fight is played by the slow exhaustive-lookahead player with this many rollouts per candidate.</summary>
    public static int ExhaustiveRollouts { get; set; } = int.Parse(Environment.GetEnvironmentVariable("BOT_EXHAUSTIVE") ?? "0");

    /// <summary>Plays one whole combat with the bot and reports how it went. <paramref name="trace"/> receives a readable play-by-play.</summary>
    public static FightResult Run(IEnumerable<CardDef> deck, int hp, int maxHp, IEnumerable<MonsterDef> monsters,
        int ascension, ulong seed, BasicBot? bot = null, Action<string>? trace = null, IReadOnlyList<int>? altStarts = null, double enemyDamageScale = 1.0, CombatServices? services = null,
        IEnumerable<PotionDef>? potions = null, int stakes = 0, IEnumerable<RelicKind>? relics = null, double hpScale = 1.0)
    {
        if (ExhaustiveRollouts > 0 && trace == null)
        {
            var list = monsters.ToList();
            return RunExhaustive(deck, hp, maxHp, list, ascension, seed, ExhaustiveRollouts, 5, 300, services, null, altStarts, enemyDamageScale, potions, stakes, relics, hpScale);
        }
        bot ??= new BasicBot();
        var combat = new Combat(deck, hp, maxHp, monsters, ascension, seed, altStarts: altStarts, enemyDamageScale: enemyDamageScale, services: services, potions: potions, relics: relics, stakes: stakes, hpScale: hpScale);
        while (combat.Result == CombatResult.Ongoing)
        {
            if (trace != null) TraceTurnStart(combat, trace);
            PlayTurn(combat, bot, trace);
            if (combat.Result != CombatResult.Ongoing) break;
            int hpBefore = combat.Hp;
            trace?.Invoke("    end turn");
            combat.EndPlayerTurn();
            trace?.Invoke($"    enemy turn: HP {hpBefore} -> {combat.Hp}");
        }
        trace?.Invoke($"  result: {combat.Result}, HP lost {combat.HpLost}, turns {combat.Turn}");
        return new FightResult(combat.Result == CombatResult.Won, combat.HpLost, combat.Turn, Math.Max(0, combat.Hp), combat.Potions.ToList(), combat.LostCardIds.ToList(), combat.GoldSpent, combat.MaxHpGained);
    }

    /// <summary>Research tool: each turn tries every ordered sequence of the cards now in hand, finishes the fight from each with the default bot on shuffled futures, and plays the sequence with the lowest average HP loss.</summary>
    public static FightResult RunExhaustive(IEnumerable<CardDef> deck, int hp, int maxHp, IEnumerable<MonsterDef> monsters,
        int ascension, ulong seed, int rollouts, int maxDepth, int maxSequences, CombatServices? services = null, Action<string>? trace = null,
        IReadOnlyList<int>? altStarts = null, double enemyDamageScale = 1.0, IEnumerable<PotionDef>? potions = null, int stakes = 0, IEnumerable<RelicKind>? relics = null, double hpScale = 1.0)
    {
        var combat = new Combat(deck, hp, maxHp, monsters, ascension, seed, altStarts: altStarts, enemyDamageScale: enemyDamageScale, services: services, potions: potions, relics: relics, stakes: stakes, hpScale: hpScale);
        var fallback = new BasicBot();
        ulong salt = 1;
        while (combat.Result == CombatResult.Ongoing)
        {
            foreach (CardDef c in combat.Hand) c.Tag = 0;
            for (int i = 0; i < combat.Hand.Count; i++) combat.Hand[i].Tag = i + 1;

            var sequences = new List<List<BotAction>>();
            void Enumerate(Combat state, List<BotAction> prefix)
            {
                if (sequences.Count >= maxSequences) return;
                sequences.Add(new List<BotAction>(prefix));
                if (prefix.Count >= maxDepth) return;
                var seen = new HashSet<(string, bool, int)>();
                foreach (CardDef card in state.Hand.Where(c => c.Tag > 0).ToList())
                {
                    if (!state.CanPlay(card) || !seen.Add((card.Id, card.Upgraded, card.BonusDamage))) continue;
                    IEnumerable<int> targets = Combat.NeedsTarget(card) ? state.Enemies.Where(e => e.Alive && !e.Dying).Select(e => e.Index) : new[] { -1 };
                    foreach (int t in targets)
                    {
                        Combat next = state.Clone(salt++);
                        var action = new BotAction(card.Tag, null, t);
                        if (!BasicBot.Apply(next, action) || next.Result != CombatResult.Ongoing) continue;
                        prefix.Add(action);
                        Enumerate(next, prefix);
                        prefix.RemoveAt(prefix.Count - 1);
                    }
                }
            }
            Enumerate(combat, new List<BotAction>());

            List<BotAction> best = sequences[0];
            double bestScore = double.MaxValue;
            foreach (var seq in sequences)
            {
                double total = 0;
                for (int r = 0; r < rollouts; r++)
                {
                    Combat c = combat.Clone(salt++);
                    c.Rng.Shuffle(c.DrawPile);
                    foreach (BotAction a in seq) if (!BasicBot.Apply(c, a)) break;
                    if (c.Result == CombatResult.Ongoing) PlayTurn(c, fallback, null);
                    while (c.Result == CombatResult.Ongoing)
                    {
                        c.EndPlayerTurn();
                        if (c.Result != CombatResult.Ongoing || c.Turn > 40) break;
                        PlayTurn(c, fallback, null);
                    }
                    total += c.Result == CombatResult.Won ? c.HpLost : 1000;
                }
                if (total < bestScore) { bestScore = total; best = seq; }
            }
            if (trace != null)
            {
                TraceTurnStart(combat, trace);
                trace($"    best of {sequences.Count} (avg loss {bestScore / rollouts:0.0}): " + string.Join(", ", best.Select(a => Describe(combat, a).Trim())));
            }
            foreach (BotAction a in best) if (combat.Result == CombatResult.Ongoing) BasicBot.Apply(combat, a);
            if (combat.Result == CombatResult.Ongoing) PlayTurn(combat, fallback, null);
            if (combat.Result != CombatResult.Ongoing) break;
            combat.EndPlayerTurn();
        }
        return new FightResult(combat.Result == CombatResult.Won, combat.HpLost, combat.Turn, Math.Max(0, combat.Hp), combat.Potions.ToList(), combat.LostCardIds.ToList(), combat.GoldSpent, combat.MaxHpGained);
    }

    /// <summary>Research tool: plays a fight with the normal bot and, each turn, compares its plan with the best ordering of the hand (judged by rollouts of the normal bot), reporting the turns where the bot's plan is clearly worse.</summary>
    public static void Audit(IEnumerable<CardDef> deck, int hp, int maxHp, IEnumerable<MonsterDef> monsters, int ascension, ulong seed, int rollouts, CombatServices? services, Action<string> report)
    {
        var combat = new Combat(deck, hp, maxHp, monsters, ascension, seed, services: services);
        var bot = new BasicBot();
        ulong salt = 1;
        double Judge(Combat root, List<BotAction> seq)
        {
            double total = 0;
            for (int r = 0; r < rollouts; r++)
            {
                Combat c = root.Clone(salt++);
                c.Rng.Shuffle(c.DrawPile);
                foreach (BotAction a in seq) if (!BasicBot.Apply(c, a)) break;
                if (c.Result == CombatResult.Ongoing) PlayTurn(c, bot, null);
                while (c.Result == CombatResult.Ongoing)
                {
                    c.EndPlayerTurn();
                    if (c.Result != CombatResult.Ongoing || c.Turn > 40) break;
                    PlayTurn(c, bot, null);
                }
                total += c.Result == CombatResult.Won ? c.HpLost : 1000;
            }
            return total / rollouts;
        }
        while (combat.Result == CombatResult.Ongoing)
        {
            foreach (CardDef c in combat.Hand) c.Tag = 0;
            for (int i = 0; i < combat.Hand.Count; i++) combat.Hand[i].Tag = i + 1;
            List<BotAction> mine = bot.Plan(combat);
            foreach (CardDef c in combat.Hand) c.Tag = 0;
            for (int i = 0; i < combat.Hand.Count; i++) combat.Hand[i].Tag = i + 1;

            var sequences = new List<List<BotAction>>();
            void Enumerate(Combat state, List<BotAction> prefix)
            {
                if (sequences.Count >= 400) return;
                sequences.Add(new List<BotAction>(prefix));
                if (prefix.Count >= 5) return;
                var seen = new HashSet<(string, bool, int)>();
                foreach (CardDef card in state.Hand.Where(c => c.Tag > 0).ToList())
                {
                    if (!state.CanPlay(card) || !seen.Add((card.Id, card.Upgraded, card.BonusDamage))) continue;
                    IEnumerable<int> targets = Combat.NeedsTarget(card) ? state.Enemies.Where(e => e.Alive && !e.Dying).Select(e => e.Index) : new[] { -1 };
                    foreach (int t in targets)
                    {
                        Combat next = state.Clone(salt++);
                        var action = new BotAction(card.Tag, null, t);
                        if (!BasicBot.Apply(next, action) || next.Result != CombatResult.Ongoing) continue;
                        prefix.Add(action);
                        Enumerate(next, prefix);
                        prefix.RemoveAt(prefix.Count - 1);
                    }
                }
            }
            Enumerate(combat, new List<BotAction>());
            double mineScore = Judge(combat, mine);
            List<BotAction> best = mine;
            double bestScore = mineScore;
            foreach (var seq in sequences)
            {
                double v = Judge(combat, seq);
                if (v < bestScore) { bestScore = v; best = seq; }
            }
            string Show(List<BotAction> seq) => string.Join(", ", seq.Select(a => Describe(combat, a).Trim()));
            if (mineScore - bestScore >= 4)
            {
                TraceTurnStart(combat, report);
                report($"    bot  ({mineScore:0.0}): {Show(mine)}");
                report($"    best ({bestScore:0.0}): {Show(best)}");
            }
            foreach (BotAction a in mine) if (combat.Result == CombatResult.Ongoing && !BasicBot.Apply(combat, a)) break;
            if (combat.Result == CombatResult.Ongoing) PlayTurn(combat, bot, null);
            if (combat.Result != CombatResult.Ongoing) break;
            combat.EndPlayerTurn();
        }
    }

    /// <summary>Plans the turn and carries the plan out, planning again whenever a draw or generated card changes the hand.</summary>
    public static void PlayTurn(Combat combat, BasicBot bot, Action<string>? trace = null)
    {
        int actions = 0;
        while (combat.Result == CombatResult.Ongoing && actions < MaxActionsPerTurn)
        {
            List<BotAction> plan = bot.Plan(combat);
            if (plan.Count == 0) return;
            foreach (BotAction action in plan)
            {
                if (combat.Result != CombatResult.Ongoing || actions >= MaxActionsPerTurn) return;
                if (trace != null) trace(Describe(combat, action));
                int exhaustedBefore = combat.ExhaustPile.Count;
                if (!BasicBot.Apply(combat, action)) break;
                actions++;
                if (trace != null && combat.Result == CombatResult.Ongoing)
                    trace($"      (energy {combat.Energy}, block {combat.Block}, enemies: {string.Join(", ", combat.Enemies.Select(e => e.Alive ? $"{e.Hp}hp/{e.Block}b" : "dead"))}){(combat.ExhaustPile.Count > exhaustedBefore ? $" exhausted {string.Join(", ", combat.ExhaustPile.Skip(exhaustedBefore))}" : "")}");
                // A card we didn't know about is in hand now (drawn or generated): the rest of the plan is stale.
                if (combat.Hand.Any(c => c.Tag == 0)) break;
            }
        }
    }

    private static string Describe(Combat c, BotAction a)
    {
        string target = a.Target >= 0 ? $" -> {c.Enemies[a.Target].Def.Id}#{a.Target}" : "";
        return a.IsPotion ? $"    drink {a.PotionId}{target}" : $"    play {c.Hand.FirstOrDefault(x => x.Tag == a.Tag)}{target}";
    }

    private static void TraceTurnStart(Combat c, Action<string> trace)
    {
        trace($"  turn {c.Turn}: HP {c.Hp}/{c.MaxHp}, block {c.Block}, energy {c.Energy}, hand [{string.Join(", ", c.Hand)}]{(c.Potions.Count > 0 ? $", potions [{string.Join(", ", c.Potions.Select(p => p.Id))}]" : "")}");
        string mine = string.Join(" ", Enum.GetValues<PowerKind>().Where(p => c.PlayerPowers[(int)p] != 0).Select(p => $"{p}{c.PlayerPowers[(int)p]}"));
        if (mine.Length > 0) trace($"    player powers: {mine}");
        foreach (Enemy e in c.Enemies)
        {
            if (!e.Alive) continue;
            string powers = string.Join(" ", Enum.GetValues<PowerKind>().Where(p => e.Powers[(int)p] != 0).Select(p => $"{p}{e.Powers[(int)p]}"));
            string move = e.Stunned ? "STUNNED" : e.Move == null ? "no move"
                : $"{e.Move.Id}{(c.IntendsAttack(e) ? $" {(e.Move.Id == "EXPLODE" ? 1 : c.MoveHits(e, e.Move))}x{c.EnemyAttackDamage(c.MoveBaseDamage(e), e)}" : "")}{(e.Move.Block > 0 ? $" +{e.Move.Block}blk" : "")}{(e.Move.Adds.Length > 0 ? $" +{string.Join("/", e.Move.Adds.Select(a => $"{a.CountAt(c.Ascension)} {a.CardId}"))}" : "")}";
            trace($"    {e.Def.Id}#{e.Index} {e.Hp}/{e.MaxHp}hp {e.Block}b [{powers}] intends {move}");
        }
    }
}
