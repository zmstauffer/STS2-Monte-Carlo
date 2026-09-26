using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>Ravenous, Suck, Constrict, Tangled, Smoggy, and Surprise (the small Act 1 and 2 monster powers), from the decompiled power classes. Skipped when the local Codex cache isn't built.</summary>
public class SmallPowerTests
{
    private static readonly Lazy<SimData?> Shared = new(() =>
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadMonsterClasses() == null ? null : new SimData(cache);
    });

    private static SimData? Data => Shared.Value;

    private static MonsterDef Dummy(int hp = 1000, int damage = 0, params InnatePower[] innate) => new()
    {
        Id = "DUMMY", HpMin = hp, HpMax = hp, HpMinTough = hp, HpMaxTough = hp, Innate = innate,
        Moves = { ["HIT"] = new MoveDef { Id = "HIT", Intent = damage > 0 ? "Attack" : "Buff", Damage = damage, DamageDeadly = damage, Hits = 1 } },
        States = { ["HIT_MOVE"] = new StateDef { Id = "HIT_MOVE", Kind = StateKind.Move, MoveId = "HIT", Next = "HIT_MOVE" } },
        InitialState = "HIT_MOVE",
    };

    private static Combat Fight(string hand, params MonsterDef[] monsters)
    {
        SimData data = Data!;
        var c = new Combat(Array.Empty<CardDef>(), 300, 300, monsters, 10, 3, services: data.Services);
        c.Hand.AddRange(data.ParseDeck(hand).Select(x => x.Instantiate()));
        return c;
    }

    private static void Play(Combat c, string id, int target = 0) => c.Play(c.Hand.FindIndex(x => x.Id == id), target);

    [Fact]
    public void RavenousMakesAMonsterLoseItsMoveAndGainStrengthWhenAnAllyDies()
    {
        if (Data == null) return;
        Combat c = Fight("STRIKE_IRONCLAD", Dummy(hp: 1), Dummy(innate: new InnatePower(PowerKind.Ravenous, 2)));
        Play(c, "STRIKE_IRONCLAD", 0);
        Assert.False(c.Enemies[0].Alive);
        Assert.Equal(2, c.Enemies[1].Powers[(int)PowerKind.Strength]);
        Assert.True(c.Enemies[1].Stunned);
    }

    [Fact]
    public void SuckGivesStrengthForEveryHitThatGetsThroughBlock()
    {
        if (Data == null) return;
        Combat c = Fight("", Dummy(damage: 5, innate: new InnatePower(PowerKind.Suck, 2)));
        c.EndPlayerTurn();   // 5 damage, no block
        Assert.Equal(2, c.Enemies[0].Powers[(int)PowerKind.Strength]);
    }

    [Fact]
    public void SuckGetsNothingWhenBlockSoaksTheHit()
    {
        if (Data == null) return;
        Combat c = Fight("DEFEND_IRONCLAD*2", Dummy(damage: 5, innate: new InnatePower(PowerKind.Suck, 2)));
        Play(c, "DEFEND_IRONCLAD");
        Play(c, "DEFEND_IRONCLAD");
        c.EndPlayerTurn();
        Assert.Equal(0, c.Enemies[0].Powers[(int)PowerKind.Strength]);
    }

    [Fact]
    public void ConstrictHurtsAtTheEndOfTheTurnAndBlockHelps()
    {
        if (Data == null) return;
        Combat c = Fight("", Dummy());
        c.PlayerPowers[(int)PowerKind.Constrict] = 3;
        c.EndPlayerTurn();
        Assert.Equal(297, c.Hp);
        Combat blocked = Fight("DEFEND_IRONCLAD", Dummy());
        blocked.PlayerPowers[(int)PowerKind.Constrict] = 3;
        Play(blocked, "DEFEND_IRONCLAD");
        blocked.EndPlayerTurn();
        Assert.Equal(300, blocked.Hp);
    }

    [Fact]
    public void TangledMakesAttacksCostOneMoreUntilTheTurnEnds()
    {
        if (Data == null) return;
        Combat c = Fight("STRIKE_IRONCLAD,DEFEND_IRONCLAD", Dummy());
        c.PlayerPowers[(int)PowerKind.Tangled] = 1;
        Assert.Equal(2, c.EffectiveCost(c.Hand.First(x => x.Id == "STRIKE_IRONCLAD")));
        Assert.Equal(1, c.EffectiveCost(c.Hand.First(x => x.Id == "DEFEND_IRONCLAD")));
        c.EndPlayerTurn();
        Assert.Equal(0, c.PlayerPowers[(int)PowerKind.Tangled]);
    }

    [Fact]
    public void SmoggyAllowsOnlyOneSkillPerTurn()
    {
        if (Data == null) return;
        Combat c = Fight("DEFEND_IRONCLAD,DEFEND_IRONCLAD,STRIKE_IRONCLAD", Dummy());
        c.PlayerPowers[(int)PowerKind.Smoggy] = 1;
        Play(c, "DEFEND_IRONCLAD");
        Assert.False(c.CanPlay(c.Hand.First(x => x.Id == "DEFEND_IRONCLAD")));
        Assert.True(c.CanPlay(c.Hand.First(x => x.Id == "STRIKE_IRONCLAD")));
    }

    [Fact]
    public void KillingTheGremlinMercBringsAFatAndASneakyGremlinAndTheFightGoesOn()
    {
        if (Data == null) return;
        var c = new Combat(Array.Empty<CardDef>(), 300, 300, new[] { Data.Monsters.Get("GREMLIN_MERC") }, 10, 3, services: Data.Services);
        c.Hand.AddRange(Data.ParseDeck("STRIKE_IRONCLAD").Select(x => x.Instantiate()));
        c.Enemies[0].Hp = 1;
        c.Play(0, 0);
        Assert.Equal(CombatResult.Ongoing, c.Result);
        Assert.Contains(c.Enemies, e => e.Alive && e.Def.Id == "FAT_GREMLIN");
        Assert.Contains(c.Enemies, e => e.Alive && e.Def.Id == "SNEAKY_GREMLIN");
    }
}
