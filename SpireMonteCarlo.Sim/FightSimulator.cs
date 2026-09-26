namespace SpireMonteCarlo.Sim;

public readonly record struct FightResult(bool Won, int HpLost, int Turns, int HpAfter, IReadOnlyList<PotionDef>? PotionsLeft = null);

public static class FightSimulator
{
    private const int MaxActionsPerTurn = 60;

    /// <summary>Plays one whole combat with the bot and reports how it went. <paramref name="trace"/> receives a readable play-by-play.</summary>
    public static FightResult Run(IEnumerable<CardDef> deck, int hp, int maxHp, IEnumerable<MonsterDef> monsters,
        int ascension, ulong seed, BasicBot? bot = null, Action<string>? trace = null, IReadOnlyList<int>? altStarts = null, double enemyDamageScale = 1.0, CombatServices? services = null,
        IEnumerable<PotionDef>? potions = null, int stakes = 0, IEnumerable<RelicKind>? relics = null, double hpScale = 1.0)
    {
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
        return new FightResult(combat.Result == CombatResult.Won, combat.HpLost, combat.Turn, Math.Max(0, combat.Hp), combat.Potions.ToList());
    }

    /// <summary>Plans the turn and carries the plan out, planning again whenever a draw or generated card changes the hand.</summary>
    private static void PlayTurn(Combat combat, BasicBot bot, Action<string>? trace)
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
                if (!BasicBot.Apply(combat, action)) break;
                actions++;
                if (trace != null && combat.Result == CombatResult.Ongoing)
                    trace($"      (energy {combat.Energy}, block {combat.Block}, enemies: {string.Join(", ", combat.Enemies.Select(e => e.Alive ? $"{e.Hp}hp/{e.Block}b" : "dead"))})");
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
        foreach (Enemy e in c.Enemies)
        {
            if (!e.Alive) continue;
            string powers = string.Join(" ", Enum.GetValues<PowerKind>().Where(p => e.Powers[(int)p] != 0).Select(p => $"{p}{e.Powers[(int)p]}"));
            string move = e.Stunned ? "STUNNED" : e.Move == null ? "no move"
                : $"{e.Move.Id}{(c.IntendsAttack(e) ? $" {(e.Move.Id == "EXPLODE" ? 1 : e.Move.HitsAt(c.Ascension))}x{c.EnemyAttackDamage(c.MoveBaseDamage(e), e)}" : "")}{(e.Move.Block > 0 ? $" +{e.Move.Block}blk" : "")}{(e.Move.Adds.Length > 0 ? $" +{string.Join("/", e.Move.Adds.Select(a => $"{a.CountAt(c.Ascension)} {a.CardId}"))}" : "")}";
            trace($"    {e.Def.Id}#{e.Index} {e.Hp}/{e.MaxHp}hp {e.Block}b [{powers}] intends {move}");
        }
    }
}
