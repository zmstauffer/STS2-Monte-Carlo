using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>
/// Act 2 monster mechanics, each pinned to the decompiled power or monster class it was written from (v0.111):
/// Hard to Kill, Thorns, Curl Up, Flutter, Burrowed, Slumber, the Insatiable's sandpit, Vital Spark's Tainted, Tender, Imbalanced,
/// the Entomancer, and Crab Rage. Skipped when the local Codex cache isn't built.
/// </summary>
public class Act2MechanicsTests
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
    public void HardToKillCapsEveryHitAtItsAmount()
    {
        if (Data == null) return;
        Combat c = Fight("STRIKE_IRONCLAD", Dummy(innate: new InnatePower(PowerKind.HardToKill, 10)));
        c.PlayerPowers[(int)PowerKind.Strength] = 20;   // a 26-damage Strike
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(990, c.Enemies[0].Hp);
    }

    [Fact]
    public void EnemyThornsHurtThePlayerOnEveryPoweredAttackHit()
    {
        if (Data == null) return;
        Combat c = Fight("STRIKE_IRONCLAD,STRIKE_IRONCLAD", Dummy(innate: new InnatePower(PowerKind.Thorns, 5)));
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(295, c.Hp);
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(290, c.Hp);
    }

    [Fact]
    public void ThornsAreBlockedLikeAnyOtherDamage()
    {
        if (Data == null) return;
        Combat c = Fight("STRIKE_IRONCLAD", Dummy(innate: new InnatePower(PowerKind.Thorns, 5)));
        c.PlayerPowers[(int)PowerKind.Dexterity] = 0;
        c.Hand.AddRange(Data!.ParseDeck("DEFEND_IRONCLAD").Select(x => x.Instantiate()));
        Play(c, "DEFEND_IRONCLAD");
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(300, c.Hp);
    }

    [Fact]
    public void CurlUpGivesItsBlockOnceTheCardThatHitItIsDone()
    {
        if (Data == null) return;
        Combat c = Fight("STRIKE_IRONCLAD,STRIKE_IRONCLAD", Dummy(innate: new InnatePower(PowerKind.CurlUp, 8)));
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(994, c.Enemies[0].Hp);   // the first hit landed in full
        Assert.Equal(8, c.Enemies[0].Block);
        Assert.Equal(0, c.Enemies[0].Powers[(int)PowerKind.CurlUp]);
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(994, c.Enemies[0].Hp);   // now the block soaks it
        Assert.Equal(2, c.Enemies[0].Block);
    }

    [Fact]
    public void FlutterHalvesAttackDamageAndStunsAfterEnoughUnblockedHits()
    {
        if (Data == null) return;
        Combat c = Fight("STRIKE_IRONCLAD,STRIKE_IRONCLAD", Dummy(innate: new InnatePower(PowerKind.Flutter, 2)));
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(997, c.Enemies[0].Hp);   // 6 halved
        Assert.False(c.Enemies[0].Stunned);
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(994, c.Enemies[0].Hp);
        Assert.True(c.Enemies[0].Stunned);
    }

    [Fact]
    public void ABurrowedEnemyKeepsItsBlockUntilItIsBroken()
    {
        if (Data == null) return;
        MonsterDef def = Dummy(damage: 5, innate: new InnatePower(PowerKind.Burrowed, 1));
        Combat c = Fight("STRIKE_IRONCLAD,STRIKE_IRONCLAD", def);
        c.Enemies[0].Block = 10;
        c.EndPlayerTurn();
        Assert.Equal(10, c.Enemies[0].Block);   // not cleared at the start of its turn
        // Two Strikes (6 each) break 10 block: the burrowed enemy is stunned and surfaces.
        c.Hand.AddRange(Data.ParseDeck("STRIKE_IRONCLAD,STRIKE_IRONCLAD").Select(x => x.Instantiate()));
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(4, c.Enemies[0].Block);
        Assert.Equal(1, c.Enemies[0].Powers[(int)PowerKind.Burrowed]);
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(0, c.Enemies[0].Powers[(int)PowerKind.Burrowed]);
        Assert.Equal(0, c.Enemies[0].Block);
        Assert.True(c.Enemies[0].Stunned);
    }

    [Fact]
    public void SlumberWakesTheBeetleAfterThreeUnblockedHits()
    {
        if (Data == null) return;
        Combat c = Fight("STRIKE_IRONCLAD*3", Dummy(innate: new[] { new InnatePower(PowerKind.Slumber, 3), new InnatePower(PowerKind.Plating, 15) }));
        c.Enemies[0].Block = 0;
        Play(c, "STRIKE_IRONCLAD");
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(15, c.Enemies[0].Powers[(int)PowerKind.Plating]);
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(0, c.Enemies[0].Powers[(int)PowerKind.Slumber]);
        Assert.Equal(0, c.Enemies[0].Powers[(int)PowerKind.Plating]);   // woken: the plating is gone
        Assert.True(c.Enemies[0].Stunned);
    }

    [Fact]
    public void TheSandpitCountsDownEachEnemyTurnAndSwallowsThePlayerAtZero()
    {
        if (Data == null) return;
        Combat c = Fight("", Dummy());
        c.PlayerPowers[(int)PowerKind.Sandpit] = 2;
        c.EndPlayerTurn();
        Assert.Equal(1, c.PlayerPowers[(int)PowerKind.Sandpit]);
        Assert.Equal(CombatResult.Ongoing, c.Result);
        c.EndPlayerTurn();
        Assert.Equal(CombatResult.Lost, c.Result);
    }

    [Fact]
    public void FranticEscapePushesTheSandpitBackAndCostsOneMoreEachTime()
    {
        if (Data == null) return;
        Combat c = Fight("", Dummy());
        CardDef escape = Data.Cards.Get("FRANTIC_ESCAPE", false).Instantiate();
        c.Hand.Add(escape);
        c.PlayerPowers[(int)PowerKind.Sandpit] = 2;
        Assert.Equal(1, c.EffectiveCost(escape));
        c.Play(0, -1);
        Assert.Equal(3, c.PlayerPowers[(int)PowerKind.Sandpit]);
        Assert.Equal(2, escape.CurrentCost);
    }

    [Fact]
    public void VitalSparkTaintsSkillsSoEnemyAttacksHitHarderUntilTheirTurnEnds()
    {
        if (Data == null) return;
        Combat c = Fight("DEFEND_IRONCLAD,DEFEND_IRONCLAD", Dummy(damage: 10, innate: new InnatePower(PowerKind.VitalSpark, 3)));
        Play(c, "DEFEND_IRONCLAD");
        Play(c, "DEFEND_IRONCLAD");
        Assert.Equal(6, c.PlayerPowers[(int)PowerKind.Tainted]);
        Assert.Equal(10, c.Block);
        c.EndPlayerTurn();   // the attack does 10 + 6 = 16 into 10 block
        Assert.Equal(294, c.Hp);
        Assert.Equal(0, c.PlayerPowers[(int)PowerKind.Tainted]);
    }

    [Fact]
    public void TenderCostsAPointOfStrengthAndDexterityPerCardUntilTheTurnEnds()
    {
        if (Data == null) return;
        Combat c = Fight("STRIKE_IRONCLAD,STRIKE_IRONCLAD", Dummy());
        c.PlayerPowers[(int)PowerKind.Tender] = 1;
        Play(c, "STRIKE_IRONCLAD");
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(-2, c.PlayerPowers[(int)PowerKind.Strength]);
        Assert.Equal(-2, c.PlayerPowers[(int)PowerKind.Dexterity]);
        c.EndPlayerTurn();
        Assert.Equal(0, c.PlayerPowers[(int)PowerKind.Strength]);
        Assert.Equal(0, c.PlayerPowers[(int)PowerKind.Dexterity]);
    }

    [Fact]
    public void ABowlbugRockWhoseHeadbuttIsFullyBlockedIsKnockedOffBalanceAndLosesItsNextTurn()
    {
        if (Data == null) return;
        var c = new Combat(Array.Empty<CardDef>(), 300, 300, new[] { Data.Monsters.Get("BOWLBUG_ROCK") }, 10, 3, services: Data.Services);
        c.Hand.AddRange(Data.ParseDeck("DEFEND_IRONCLAD").Select(x => x.Instantiate()));
        c.Play(0, -1);
        c.PlayerPowers[(int)PowerKind.Dexterity] = 50;   // more than enough block to soak the headbutt
        c.Hand.AddRange(Data.ParseDeck("DEFEND_IRONCLAD").Select(x => x.Instantiate()));
        c.Play(0, -1);
        int hpBefore = c.Hp;
        c.EndPlayerTurn();
        Assert.Equal(hpBefore, c.Hp);
        Assert.Equal("DIZZY", c.Enemies[0].Move?.Id);   // the state machine's POST_HEADBUTT branch chose the dizzy turn
    }

    [Fact]
    public void ThePheromoneSpitAddsHiveAndStrengthUntilTheHiveIsThree()
    {
        if (Data == null) return;
        var c = new Combat(Array.Empty<CardDef>(), 300, 300, new[] { Data.Monsters.Get("ENTOMANCER") }, 10, 3, services: Data.Services);
        Enemy e = c.Enemies[0];
        Assert.Equal(1, e.Powers[(int)PowerKind.PersonalHive]);   // starts with one
        var spit = new MoveDef { Id = "PHEROMONE_SPIT" };
        var behavior = new EntomancerBehavior();
        behavior.OnMove(c, e, spit);
        Assert.Equal(2, e.Powers[(int)PowerKind.PersonalHive]);
        Assert.Equal(1, e.Powers[(int)PowerKind.Strength]);
        behavior.OnMove(c, e, spit);
        Assert.Equal(3, e.Powers[(int)PowerKind.PersonalHive]);
        behavior.OnMove(c, e, spit);
        Assert.Equal(3, e.Powers[(int)PowerKind.PersonalHive]);   // capped: now it only gains 2 Strength
        Assert.Equal(4, e.Powers[(int)PowerKind.Strength]);
    }

    [Fact]
    public void EachHitOnAnEntomancerShufflesADazedIntoTheDrawPile()
    {
        if (Data == null) return;
        var c = new Combat(Array.Empty<CardDef>(), 300, 300, new[] { Data.Monsters.Get("ENTOMANCER") }, 10, 3, services: Data.Services);
        c.Hand.AddRange(Data.ParseDeck("STRIKE_IRONCLAD").Select(x => x.Instantiate()));
        int before = c.DrawPile.Count;
        c.Play(0, 0);
        Assert.Equal(before + 1, c.DrawPile.Count);
        Assert.Contains(c.DrawPile, x => x.Id == "DAZED");
    }

    [Fact]
    public void WhenOneKaiserCrabClawDiesTheOtherRagesOnce()
    {
        if (Data == null) return;
        var c = new Combat(Array.Empty<CardDef>(), 300, 300, new[] { Data.Monsters.Get("CRUSHER"), Data.Monsters.Get("ROCKET") }, 10, 3, services: Data.Services);
        c.Hand.AddRange(Data.ParseDeck("STRIKE_IRONCLAD").Select(x => x.Instantiate()));
        c.Enemies[0].Hp = 1;
        int strength = c.Enemies[1].Powers[(int)PowerKind.Strength];
        c.Play(0, 0);
        Assert.False(c.Enemies[0].Alive);
        Assert.Equal(strength + 6, c.Enemies[1].Powers[(int)PowerKind.Strength]);
        Assert.True(c.Enemies[1].Block >= 99);
        Assert.Equal(0, c.Enemies[1].Powers[(int)PowerKind.CrabRage]);
    }

    [Fact]
    public void TheKnowledgeDemonCursesThreeTimesThenStopsAndDisintegrationHurtsAtTheEndOfEachTurn()
    {
        if (Data == null) return;
        var c = new Combat(Array.Empty<CardDef>(), 300, 300, new[] { Data.Monsters.Get("KNOWLEDGE_DEMON") }, 10, 3, services: Data.Services);
        Enemy demon = c.Enemies[0];
        var behavior = new KnowledgeDemonBehavior();
        var curse = new MoveDef { Id = "CURSE_OF_KNOWLEDGE" };
        Assert.True(behavior.EvaluateCondition(c, demon, "_curseOfKnowledgeCounter < 3"));
        for (int i = 0; i < 3; i++) behavior.OnMove(c, demon, curse);
        Assert.False(behavior.EvaluateCondition(c, demon, "_curseOfKnowledgeCounter < 3"));
        Assert.True(behavior.EvaluateCondition(c, demon, "_curseOfKnowledgeCounter >= 3"));
        Assert.Equal(6 + 7 + 8, c.PlayerPowers[(int)PowerKind.Disintegration]);
        int hp = c.Hp;
        c.EndPlayerTurn();
        Assert.True(hp - c.Hp >= 21);
    }

    [Fact]
    public void ANewLineupOfBowlbugsHasARockAndTwoDifferentWorkers()
    {
        if (Data == null) return;
        EncounterDef bugs = Data.Encounters.Get("BOWLBUGS_NORMAL");
        Assert.False(bugs.Approximate);
        for (ulong seed = 1; seed <= 30; seed++)
        {
            string[] lineup = bugs.Generate(new SimRng(seed));
            Assert.Equal("BOWLBUG_ROCK", lineup[0]);
            Assert.Equal(3, lineup.Distinct().Count());
        }
    }
}
