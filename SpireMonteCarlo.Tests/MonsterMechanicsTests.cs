using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>Each monster mechanic, on a small synthetic monster. Rules come from the decompiled power classes.</summary>
public class MonsterMechanicsTests
{
    private static CardDef Attack(int damage, int cost = 1, int hits = 1) => new()
    {
        Id = "ATTACK" + damage, Kind = CardKind.Attack, Cost = cost, Effects = new[] { new Effect(EffectOp.Damage, damage, hits) },
    };

    private static readonly CardDef Idle = new() { Id = "IDLE", Kind = CardKind.Skill, Cost = 3 };

    private static MoveDef Move(string id, int damage = 0, int hits = 1, params MovePower[] powers) => new()
    {
        Id = id, Damage = damage, DamageDeadly = damage, Hits = hits, Powers = powers,
    };

    /// <summary>A monster that repeats a single move forever.</summary>
    private static MonsterDef Monster(int hp, MoveDef move, params (PowerKind, int)[] innate) => new()
    {
        Id = "TEST", HpMin = hp, HpMax = hp, HpMinTough = hp, HpMaxTough = hp,
        Moves = { [move.Id] = move },
        States = { [move.Id + "_MOVE"] = new StateDef { Id = move.Id + "_MOVE", Kind = StateKind.Move, MoveId = move.Id, Next = move.Id + "_MOVE" } },
        InitialState = move.Id + "_MOVE",
        Innate = innate,
    };

    private static Combat Fight(CardDef[] deck, MonsterDef[] monsters, int hp = 100, CombatServices? services = null) =>
        new(deck, hp, hp, monsters, ascension: 0, seed: 1, services: services);

    private static void PlayFirst(Combat c, CardDef card, int target = 0) => c.Play(c.Hand.FindIndex(x => ReferenceEquals(x, card)), target);

    [Fact]
    public void SlowRaisesDamageTenPercentPerCardPlayedThisTurnAndResets()
    {
        CardDef strike = Attack(20, cost: 0);
        var c = Fight(Enumerable.Repeat(strike, 5).ToArray(), new[] { Monster(1000, Move("WAIT"), (PowerKind.Slow, 1)) });
        Enemy e = c.Enemies[0];
        int before = e.Hp;
        c.Play(0, 0); c.Play(0, 0); c.Play(0, 0);
        Assert.Equal(before - (20 + 22 + 24), e.Hp);      // x1.0, x1.1, x1.2
        c.EndPlayerTurn();
        Assert.Equal(0, e.SlowCards);
    }

    [Fact]
    public void TerritorialGainsStrengthEveryEnemyTurnIncludingTheFirst()
    {
        var c = Fight(new[] { Idle, Idle, Idle, Idle, Idle }, new[] { Monster(1000, Move("WAIT"), (PowerKind.Territorial, 2)) });
        c.EndPlayerTurn();
        Assert.Equal(2, c.Enemies[0].Powers[(int)PowerKind.Strength]);
        c.EndPlayerTurn();
        Assert.Equal(4, c.Enemies[0].Powers[(int)PowerKind.Strength]);
    }

    private static MonsterDef Sleeper() => new()
    {
        Id = "SLEEPER", HpMin = 500, HpMax = 500, HpMinTough = 500, HpMaxTough = 500,
        Moves = { ["SLEEP"] = Move("SLEEP"), ["SLASH"] = Move("SLASH", 10) },
        States =
        {
            ["SLEEP_MOVE"] = new StateDef { Id = "SLEEP_MOVE", Kind = StateKind.Move, MoveId = "SLEEP", Next = "SLEEP_BRANCH" },
            ["SLASH_MOVE"] = new StateDef { Id = "SLASH_MOVE", Kind = StateKind.Move, MoveId = "SLASH", Next = "SLASH_MOVE" },
            ["SLEEP_BRANCH"] = new StateDef
            {
                Id = "SLEEP_BRANCH", Kind = StateKind.Conditional,
                Branches = new[]
                {
                    new BranchDef { StateId = "SLEEP_MOVE", Condition = "Creature.HasPower<AsleepPower>()" },
                    new BranchDef { StateId = "SLASH_MOVE", Condition = "!Creature.HasPower<AsleepPower>()" },
                },
            },
        },
        InitialState = "SLEEP_MOVE",
        Innate = new[] { (PowerKind.Asleep, 3), (PowerKind.Plating, 12) },
    };

    [Fact]
    public void AsleepMonsterDoesNothingForThreeTurnsThenAttacks()
    {
        var c = Fight(new[] { Idle, Idle, Idle, Idle, Idle }, new[] { Sleeper() });
        for (int turn = 1; turn <= 3; turn++)
        {
            c.EndPlayerTurn();
            Assert.Equal(100, c.Hp);                      // still asleep
        }
        c.EndPlayerTurn();
        Assert.Equal(90, c.Hp);                            // awake on the fourth turn
    }

    [Fact]
    public void DamageWakesASleeperItLosesItsPlatingAndItsNextMove()
    {
        CardDef strike = Attack(20);
        var c = Fight(Enumerable.Repeat(strike, 5).ToArray(), new[] { Sleeper() });
        Enemy e = c.Enemies[0];
        c.Play(0, 0);                                      // the plating is only block for the enemy's own turn, so this gets through
        Assert.Equal(0, e.Powers[(int)PowerKind.Asleep]);
        Assert.Equal(0, e.Powers[(int)PowerKind.Plating]);
        Assert.True(e.Stunned);
        c.EndPlayerTurn();
        Assert.Equal(100, c.Hp);                           // it lost this turn's move
        c.EndPlayerTurn();
        Assert.Equal(90, c.Hp);                            // then attacks
    }

    [Fact]
    public void SkittishGivesBlockAfterTheFirstDamagingCardEachTurn()
    {
        CardDef strike = Attack(5, cost: 0);
        var c = Fight(Enumerable.Repeat(strike, 5).ToArray(), new[] { Monster(500, Move("WAIT"), (PowerKind.Skittish, 6)) });
        Enemy e = c.Enemies[0];
        c.Play(0, 0);
        Assert.Equal(6, e.Block);                          // gained after the first hit
        c.Play(0, 0);
        Assert.Equal(1, e.Block);                          // the second card ate 5 of it; no new block
        Assert.Equal(500 - 5, e.Hp);
    }

    [Fact]
    public void HardenedShellCapsHpLossPerTurn()
    {
        CardDef big = Attack(50, cost: 0);
        var c = Fight(Enumerable.Repeat(big, 5).ToArray(), new[] { Monster(500, Move("WAIT"), (PowerKind.HardenedShell, 20)) });
        c.Play(0, 0); c.Play(0, 0);
        Assert.Equal(500 - 20, c.Enemies[0].Hp);
        c.EndPlayerTurn();
        c.Play(0, 0);
        Assert.Equal(500 - 40, c.Enemies[0].Hp);           // the cap resets each turn
    }

    [Fact]
    public void SlipperyReducesEachHitToOneAndUsesUpStacks()
    {
        CardDef strike = Attack(30, cost: 0);
        var c = Fight(Enumerable.Repeat(strike, 5).ToArray(), new[] { Monster(500, Move("WAIT"), (PowerKind.Slippery, 2)) });
        Enemy e = c.Enemies[0];
        c.Play(0, 0);
        Assert.Equal(499, e.Hp);
        c.Play(0, 0);
        Assert.Equal(498, e.Hp);
        Assert.Equal(0, e.Powers[(int)PowerKind.Slippery]);
        c.Play(0, 0);
        Assert.Equal(468, e.Hp);                           // stacks gone: full damage
    }

    [Fact]
    public void IntangibleCapsHitsAtOneAndTicksDownAfterTheEnemyTurn()
    {
        CardDef strike = Attack(30, cost: 0);
        var c = Fight(Enumerable.Repeat(strike, 5).ToArray(), new[] { Monster(500, Move("WAIT"), (PowerKind.Intangible, 1)) });
        c.Play(0, 0);
        Assert.Equal(499, c.Enemies[0].Hp);
        c.EndPlayerTurn();
        Assert.Equal(0, c.Enemies[0].Powers[(int)PowerKind.Intangible]);
    }

    private static MonsterDef WithPhaseState(MonsterDef def, string stateId)
    {
        def.Moves["PHASE"] = Move("PHASE", 40);
        def.States[stateId] = new StateDef { Id = stateId, Kind = StateKind.Move, MoveId = "PHASE", Next = stateId };
        return def;
    }

    [Fact]
    public void ShriekStunsOnceHpFallsToTheThresholdThenGoesToItsNextState()
    {
        MonsterDef eel = WithPhaseState(Monster(100, Move("CRASH", 10), (PowerKind.Shriek, 70)), "TERROR_MOVE");
        CardDef strike = Attack(40);
        var c = Fight(Enumerable.Repeat(strike, 5).ToArray(), new[] { eel });
        c.Play(0, 0);                                      // 60 HP left, at or under 70
        Assert.True(c.Enemies[0].Stunned);
        Assert.Equal(0, c.Enemies[0].Powers[(int)PowerKind.Shriek]);
        c.EndPlayerTurn();
        Assert.Equal(100, c.Hp);                           // stunned turn
        c.EndPlayerTurn();
        Assert.Equal(60, c.Hp);                            // TERROR_MOVE (the 40-damage stand-in)
    }

    [Fact]
    public void PlowWipesStrengthAndStunsAtItsThreshold()
    {
        MonsterDef beast = WithPhaseState(Monster(100, Move("PLOW", 10), (PowerKind.Plow, 70), (PowerKind.Strength, 6)), "BEAST_CRY_MOVE");
        CardDef strike = Attack(40);
        var c = Fight(Enumerable.Repeat(strike, 5).ToArray(), new[] { beast });
        c.Play(0, 0);
        Assert.Equal(0, c.Enemies[0].Powers[(int)PowerKind.Strength]);
        Assert.True(c.Enemies[0].Stunned);
    }

    [Fact]
    public void RingingLimitsThePlayerToOneCardOnTheNextTurnOnly()
    {
        CardDef strike = Attack(1, cost: 0);
        MonsterDef banshee = Monster(500, Move("CRY", 0, 1, new MovePower(PowerKind.Ringing, OnPlayer: true, 1)));
        var c = Fight(Enumerable.Repeat(strike, 10).ToArray(), new[] { banshee });
        c.Play(0, 0); c.Play(0, 0);                        // free to play several cards this turn
        c.EndPlayerTurn();                                 // the Cry lands
        c.Play(0, 0);
        Assert.False(c.CanPlay(c.Hand[0]));                // one card only
        c.EndPlayerTurn();
        Assert.True(c.CanPlay(c.Hand[0]));                 // gone again
    }

    [Fact]
    public void MinionsDoNotKeepTheFightGoing()
    {
        MonsterDef leader = Monster(10, Move("WAIT"));
        MonsterDef minion = Monster(500, Move("WAIT"), (PowerKind.Minion, 1));
        CardDef strike = Attack(20);
        var c = Fight(Enumerable.Repeat(strike, 5).ToArray(), new[] { minion, leader });
        c.Play(0, 1);
        Assert.Equal(CombatResult.Won, c.Result);
        Assert.True(c.Enemies[0].Alive);
    }

    [Fact]
    public void InfestedSpawnsFourStunnedMonstersAndTheFightContinues()
    {
        MonsterDef host = Monster(10, Move("WAIT"), (PowerKind.Infested, 4));
        MonsterDef wriggler = new()
        {
            Id = "WRIGGLER", HpMin = 5, HpMax = 5, HpMinTough = 5, HpMaxTough = 5,
            Moves = { ["BITE"] = Move("BITE", 5), ["SPAWNED"] = Move("SPAWNED") },
            States =
            {
                ["SPAWNED_MOVE"] = new StateDef { Id = "SPAWNED_MOVE", Kind = StateKind.Move, MoveId = "SPAWNED", Next = "BITE_MOVE" },
                ["BITE_MOVE"] = new StateDef { Id = "BITE_MOVE", Kind = StateKind.Move, MoveId = "BITE", Next = "BITE_MOVE" },
            },
            InitialState = "BITE_MOVE", AltInitialState = "SPAWNED_MOVE",
        };
        var services = new CombatServices(_ => null, id => id == "WRIGGLER" ? wriggler : null);
        CardDef strike = Attack(20);
        var c = Fight(Enumerable.Repeat(strike, 5).ToArray(), new[] { host }, services: services);
        c.Play(0, 0);
        Assert.Equal(CombatResult.Ongoing, c.Result);
        Assert.Equal(4, c.Enemies.Count(e => e.Alive));
        Assert.Equal("SPAWNED", c.Enemies[1].Move!.Id);    // arrive with a wasted first move
    }

    [Fact]
    public void SteamEruptionMakesTheHostUntouchableThenItExplodesForThePressureItBuilt()
    {
        var giant = new MonsterDef
        {
            Id = "WATERFALL_GIANT", HpMin = 10, HpMax = 10, HpMinTough = 10, HpMaxTough = 10,
            Moves = { ["WAIT"] = Move("WAIT"), ["ABOUT_TO_BLOW"] = Move("ABOUT_TO_BLOW"), ["EXPLODE"] = Move("EXPLODE") },
            States =
            {
                ["WAIT_MOVE"] = new StateDef { Id = "WAIT_MOVE", Kind = StateKind.Move, MoveId = "WAIT", Next = "WAIT_MOVE" },
                ["ABOUT_TO_BLOW_MOVE"] = new StateDef { Id = "ABOUT_TO_BLOW_MOVE", Kind = StateKind.Move, MoveId = "ABOUT_TO_BLOW", Next = "EXPLODE_MOVE" },
                ["EXPLODE_MOVE"] = new StateDef { Id = "EXPLODE_MOVE", Kind = StateKind.Move, MoveId = "EXPLODE", Next = "EXPLODE_MOVE" },
            },
            InitialState = "WAIT_MOVE",
            Innate = new[] { (PowerKind.SteamEruption, 25) },
        };
        CardDef strike = Attack(20, cost: 0);
        var c = Fight(Enumerable.Repeat(strike, 5).ToArray(), new[] { giant });
        c.Play(0, 0);
        Assert.Equal(CombatResult.Ongoing, c.Result);
        Assert.True(c.Enemies[0].Dying);
        int hpBefore = c.Enemies[0].Hp;
        c.Play(0, 0);
        Assert.Equal(hpBefore, c.Enemies[0].Hp);           // can't be hurt any more

        c.EndPlayerTurn();                                 // about to blow
        Assert.Equal(100, c.Hp);
        c.EndPlayerTurn();                                 // explodes
        Assert.Equal(75, c.Hp);
        Assert.Equal(CombatResult.Won, c.Result);
    }

    [Fact]
    public void VigorBoostsTheNextAttackThenIsUsedUp()
    {
        MonsterDef eel = Monster(500, Move("CRASH", 10), (PowerKind.Vigor, 6));
        var c = Fight(new[] { Idle, Idle, Idle, Idle, Idle }, new[] { eel });
        c.EndPlayerTurn();
        Assert.Equal(100 - 16, c.Hp);
        c.EndPlayerTurn();
        Assert.Equal(100 - 16 - 10, c.Hp);
    }

    [Fact]
    public void StatusCardsLeftInHandHurtAtTheEndOfTheTurnAndBlockAbsorbsTheDamage()
    {
        CardDef infection = new() { Id = "INFECTION", Kind = CardKind.Status, Cost = CardDef.Unplayable, EndTurnDamage = 3 };
        CardDef beckon = new() { Id = "BECKON", Kind = CardKind.Status, Cost = 1, EndTurnHpLoss = 6 };
        var c = Fight(new[] { infection, beckon, Idle, Idle, Idle }, new[] { Monster(500, Move("WAIT")) });
        Assert.Contains(infection, c.Hand);
        c.EndPlayerTurn();
        Assert.Equal(100 - 3 - 6, c.Hp);
    }

    [Fact]
    public void MovesCanHandStatusCardsToThePlayer()
    {
        CardDef wound = new() { Id = "WOUND", Kind = CardKind.Status, Cost = CardDef.Unplayable };
        MoveDef haunt = Move("HAUNT") with { Adds = new[] { new CardAdd("WOUND", AddPile.Discard, 2, CountAlt: 4, AltAscension: 9) } };
        var services = new CombatServices(id => id == "WOUND" ? wound : null, _ => null);
        var c = Fight(new[] { Idle, Idle, Idle, Idle, Idle }, new[] { Monster(500, haunt) }, services: services);
        c.EndPlayerTurn();
        Assert.Equal(2, (c.Hand.Concat(c.DrawPile).Concat(c.DiscardPile)).Count(x => x.Id == "WOUND"));
    }
}
