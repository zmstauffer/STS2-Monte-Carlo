namespace SpireMonteCarlo.Sim;

public readonly record struct FightResult(bool Won, int HpLost, int Turns, int HpAfter);

public static class FightSimulator
{
    /// <summary>Plays one whole combat with the bot and reports how it went. <paramref name="trace"/> receives a readable play-by-play.</summary>
    public static FightResult Run(IEnumerable<CardDef> deck, int hp, int maxHp, IEnumerable<MonsterDef> monsters,
        int ascension, ulong seed, BasicBot? bot = null, Action<string>? trace = null, IReadOnlyList<int>? altStarts = null)
    {
        bot ??= new BasicBot();
        var combat = new Combat(deck, hp, maxHp, monsters, ascension, seed, altStarts: altStarts);
        while (combat.Result == CombatResult.Ongoing)
        {
            if (trace != null) TraceTurnStart(combat, trace);
            while (combat.Result == CombatResult.Ongoing && bot.TryChoose(combat, out int handIndex, out int target))
            {
                trace?.Invoke($"    play {combat.Hand[handIndex]}{(target >= 0 ? $" -> {combat.Enemies[target].Def.Id}#{target}" : "")}");
                combat.Play(handIndex, target);
                if (trace != null && combat.Result == CombatResult.Ongoing)
                    trace($"      (energy {combat.Energy}, block {combat.Block}, enemies: {string.Join(", ", combat.Enemies.Select(e => e.Alive ? $"{e.Hp}hp/{e.Block}b" : "dead"))})");
            }
            if (combat.Result != CombatResult.Ongoing) break;
            int hpBefore = combat.Hp;
            trace?.Invoke("    end turn");
            combat.EndPlayerTurn();
            trace?.Invoke($"    enemy turn: HP {hpBefore} -> {combat.Hp}");
        }
        trace?.Invoke($"  result: {combat.Result}, HP lost {combat.HpLost}, turns {combat.Turn}");
        return new FightResult(combat.Result == CombatResult.Won, combat.HpLost, combat.Turn, Math.Max(0, combat.Hp));
    }

    private static void TraceTurnStart(Combat c, Action<string> trace)
    {
        trace($"  turn {c.Turn}: HP {c.Hp}/{c.MaxHp}, block {c.Block}, energy {c.Energy}, hand [{string.Join(", ", c.Hand)}]");
        foreach (Enemy e in c.Enemies)
        {
            if (!e.Alive) continue;
            string powers = string.Join(" ", Enum.GetValues<PowerKind>().Where(p => e.Powers[(int)p] != 0).Select(p => $"{p}{e.Powers[(int)p]}"));
            string move = e.Move == null ? "no move" : $"{e.Move.Id}{(e.Move.IsAttack ? $" {e.Move.Hits}x{c.EnemyAttackDamage(e.Move.DamagePerHit(c.DeadlyEnemies), e)}" : "")}{(e.Move.Block > 0 ? $" +{e.Move.Block}blk" : "")}";
            trace($"    {e.Def.Id}#{e.Index} {e.Hp}/{e.MaxHp}hp {e.Block}b [{powers}] intends {move}");
        }
    }
}
