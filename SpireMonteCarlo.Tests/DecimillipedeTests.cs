using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>The Decimillipede's segments (DecimillipedeSegment and ReattachPower in the decompiled game). Skipped when the local Codex cache isn't built.</summary>
public class DecimillipedeTests
{
    private static readonly Lazy<SimData?> Shared = new(() =>
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadMonsterClasses() == null ? null : new SimData(cache);
    });

    private static SimData? Data => Shared.Value;

    private static Combat Fight(int strikes = 3)
    {
        SimData data = Data!;
        string[] ids = { "DECIMILLIPEDE_SEGMENT_FRONT", "DECIMILLIPEDE_SEGMENT_MIDDLE", "DECIMILLIPEDE_SEGMENT_BACK" };
        var c = new Combat(Array.Empty<CardDef>(), 300, 300, ids.Select(data.Monsters.Get), 10, 5, services: data.Services);
        c.Hand.AddRange(data.ParseDeck($"STRIKE_IRONCLAD*{strikes}").Select(x => x.Instantiate()));
        return c;
    }

    private static void Strike(Combat c, int target) => c.Play(c.Hand.FindIndex(x => x.Id == "STRIKE_IRONCLAD"), target);

    [Fact]
    public void TheSegmentsComeFromTheGamesOwnClassNotFromCodex()
    {
        if (Data == null) return;
        foreach (string id in new[] { "DECIMILLIPEDE_SEGMENT_FRONT", "DECIMILLIPEDE_SEGMENT_MIDDLE", "DECIMILLIPEDE_SEGMENT_BACK" })
        {
            MonsterDef def = Data.Monsters.Get(id);
            Assert.True(def.ExactAi, id);
            Assert.Equal(46, def.HpMinTough);   // 40-46 normally, 46-52 at Tough Enemies (A8)
            Assert.Equal(52, def.HpMaxTough);
        }
    }

    [Fact]
    public void AKilledSegmentGoesDownInsteadOfDyingWhileAnotherIsUp()
    {
        if (Data == null) return;
        Combat c = Fight();
        c.Enemies[0].Hp = 1;
        Strike(c, 0);
        Assert.False(c.Enemies[0].Alive);
        Assert.True(c.Enemies[0].Reviving);
        Assert.Equal(CombatResult.Ongoing, c.Result);
        Assert.False(c.Enemies.Where(e => e.Alive).Any(e => e == c.Enemies[0]));
    }

    [Fact]
    public void ADownedSegmentReattachesWith25HpTwoEnemyPhasesLater()
    {
        if (Data == null) return;
        Combat c = Fight(strikes: 1);
        c.Enemies[0].Hp = 1;
        Strike(c, 0);
        c.EndPlayerTurn();
        Assert.False(c.Enemies[0].Alive);   // this phase was its "dead" move
        c.EndPlayerTurn();
        Assert.True(c.Enemies[0].Alive);
        Assert.False(c.Enemies[0].Reviving);
        Assert.Equal(25, c.Enemies[0].Hp);
    }

    [Fact]
    public void TheFightEndsWhenTheLastStandingSegmentIsKilledWhileTheOthersAreDown()
    {
        if (Data == null) return;
        Combat c = Fight();
        foreach (Enemy e in c.Enemies) e.Hp = 1;
        Strike(c, 0);
        Strike(c, 1);
        Assert.Equal(CombatResult.Ongoing, c.Result);
        Strike(c, 2);
        Assert.Equal(CombatResult.Won, c.Result);
    }

    [Fact]
    public void ADownedSegmentCannotBeTargeted()
    {
        if (Data == null) return;
        Combat c = Fight();
        c.Enemies[0].Hp = 1;
        Strike(c, 0);
        int hpBefore = c.Enemies[0].Hp;
        Strike(c, 0);   // the target is gone, so the card goes to the first segment that is still up
        Assert.Equal(hpBefore, c.Enemies[0].Hp);
    }

    [Fact]
    public void TheEliteLineupIsTheThreeSegments()
    {
        if (Data == null) return;
        EncounterDef elite = Data.Encounters.Get("DECIMILLIPEDE_ELITE");
        Assert.False(elite.Approximate);
        Assert.Equal(new[] { "DECIMILLIPEDE_SEGMENT_FRONT", "DECIMILLIPEDE_SEGMENT_MIDDLE", "DECIMILLIPEDE_SEGMENT_BACK" }, elite.Generate(new SimRng(1)));
    }
}
