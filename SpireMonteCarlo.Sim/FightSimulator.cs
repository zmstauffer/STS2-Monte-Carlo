namespace SpireMonteCarlo.Sim;

public readonly record struct FightResult(bool Won, int HpLost, int Turns, int HpAfter);

public static class FightSimulator
{
    /// <summary>Plays one whole combat with the bot and reports how it went.</summary>
    public static FightResult Run(IEnumerable<CardDef> deck, int hp, int maxHp, IEnumerable<MonsterDef> monsters,
        int ascension, ulong seed, BasicBot? bot = null)
    {
        bot ??= new BasicBot();
        var combat = new Combat(deck, hp, maxHp, monsters, ascension, seed);
        while (combat.Result == CombatResult.Ongoing)
        {
            while (combat.Result == CombatResult.Ongoing && bot.TryChoose(combat, out int handIndex, out int target))
                combat.Play(handIndex, target);
            if (combat.Result == CombatResult.Ongoing) combat.EndPlayerTurn();
        }
        return new FightResult(combat.Result == CombatResult.Won, combat.HpLost, combat.Turn, Math.Max(0, combat.Hp));
    }
}
