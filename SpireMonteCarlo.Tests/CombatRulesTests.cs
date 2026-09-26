using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

public class CombatRulesTests
{
    private static CardDef Card(string id, int cost, CardKind kind, params Effect[] effects) =>
        new() { Id = id, Kind = kind, Cost = cost, Effects = effects };

    private static readonly CardDef Strike = Card("STRIKE", 1, CardKind.Attack, new Effect(EffectOp.Damage, 6));
    private static readonly CardDef Defend = Card("DEFEND", 1, CardKind.Skill, new Effect(EffectOp.Block, 5));
    private static readonly CardDef Bash = Card("BASH", 2, CardKind.Attack,
        new Effect(EffectOp.Damage, 8), new Effect(EffectOp.DebuffEnemy, 2, Power: PowerKind.Vulnerable));

    /// <summary>A dummy that attacks for <paramref name="damage"/> every turn (a one-state cycle).</summary>
    private static MonsterDef Dummy(int hp, int damage, int hits = 1) => new()
    {
        Id = "DUMMY",
        HpMin = hp, HpMax = hp, HpMinTough = hp, HpMaxTough = hp,
        Moves = { ["HIT"] = new MoveDef { Id = "HIT", Intent = "Attack", Damage = damage, DamageDeadly = damage, Hits = hits } },
        States = { ["HIT_MOVE"] = new StateDef { Id = "HIT_MOVE", Kind = StateKind.Move, MoveId = "HIT", Next = "HIT_MOVE" } },
        InitialState = "HIT_MOVE",
    };

    private static Combat NewCombat(CardDef[] deck, MonsterDef monster, int hp = 80, ulong seed = 1) =>
        new(deck, hp, 80, new[] { monster }, ascension: 0, seed);

    private static int IndexInHand(Combat c, CardDef card) => c.Hand.FindIndex(x => ReferenceEquals(x, card));

    [Fact]
    public void FirstTurnDrawsFiveAndGivesThreeEnergy()
    {
        var c = NewCombat(Enumerable.Repeat(Strike, 10).ToArray(), Dummy(100, 5));
        Assert.Equal(5, c.Hand.Count);
        Assert.Equal(3, c.Energy);
    }

    [Fact]
    public void VulnerableAddsFiftyPercentAndStrengthComesFirst()
    {
        var c = NewCombat(new[] { Strike, Strike, Strike, Strike, Strike }, Dummy(100, 5));
        Enemy e = c.Enemies[0];
        Assert.Equal(6, c.PlayerAttackDamage(6, e));
        e.Powers[(int)PowerKind.Vulnerable] = 1;
        Assert.Equal(9, c.PlayerAttackDamage(6, e));
        c.PlayerPowers[(int)PowerKind.Strength] = 2;
        Assert.Equal(12, c.PlayerAttackDamage(6, e));   // (6 + 2) * 1.5
        c.PlayerPowers[(int)PowerKind.Weak] = 1;
        Assert.Equal(9, c.PlayerAttackDamage(6, e));    // (6 + 2) * 0.75 * 1.5 = 9
    }

    [Fact]
    public void FrailReducesBlockByAQuarterRoundedDown()
    {
        var c = NewCombat(new[] { Defend, Defend, Defend, Defend, Defend }, Dummy(100, 5));
        Assert.Equal(5, c.PlayerBlockGain(5));
        c.PlayerPowers[(int)PowerKind.Frail] = 1;
        Assert.Equal(3, c.PlayerBlockGain(5));          // 3.75 -> 3
        c.PlayerPowers[(int)PowerKind.Dexterity] = 2;
        Assert.Equal(5, c.PlayerBlockGain(5));          // 7 * 0.75 = 5.25 -> 5
    }

    [Fact]
    public void BashMakesTheNextAttackHitHarder()
    {
        var c = NewCombat(new[] { Bash, Strike, Strike, Strike, Strike }, Dummy(100, 5));
        c.Play(IndexInHand(c, Bash), 0);
        Enemy e = c.Enemies[0];
        Assert.Equal(100 - 8, e.Hp);
        Assert.Equal(2, e.Powers[(int)PowerKind.Vulnerable]);
        Assert.Equal(1, c.Energy);
        c.Play(IndexInHand(c, Strike), 0);
        Assert.Equal(100 - 8 - 9, e.Hp);
    }

    [Fact]
    public void EnemyAttackIsAbsorbedByBlockThenHitsHp()
    {
        var c = NewCombat(new[] { Defend, Defend, Defend, Defend, Defend }, Dummy(100, 10), hp: 50);
        c.Play(0, -1);
        Assert.Equal(5, c.Block);
        c.EndPlayerTurn();
        Assert.Equal(45, c.Hp);                          // 10 damage, 5 blocked
        Assert.Equal(0, c.Block);                        // block was cleared for the new turn
        Assert.Equal(2, c.Turn);
    }

    [Fact]
    public void DebuffsTickDownWhenTheEnemyTurnEnds()
    {
        var c = NewCombat(new[] { Bash, Strike, Strike, Strike, Strike }, Dummy(100, 1));
        c.Play(IndexInHand(c, Bash), 0);
        c.EndPlayerTurn();
        Assert.Equal(1, c.Enemies[0].Powers[(int)PowerKind.Vulnerable]);
        c.EndPlayerTurn();
        Assert.Equal(0, c.Enemies[0].Powers[(int)PowerKind.Vulnerable]);
    }

    [Fact]
    public void CannotPlayWithoutEnoughEnergy()
    {
        var c = NewCombat(new[] { Bash, Bash, Bash, Bash, Bash }, Dummy(100, 5));
        c.Play(0, 0);
        Assert.False(c.CanPlay(c.Hand[0]));              // 1 energy left, Bash costs 2
        Assert.Throws<InvalidOperationException>(() => c.Play(0, 0));
    }

    [Fact]
    public void KillingTheLastEnemyWinsAndLosingAllHpLoses()
    {
        var win = NewCombat(new[] { Strike, Strike, Strike, Strike, Strike }, Dummy(6, 5));
        win.Play(0, 0);
        Assert.Equal(CombatResult.Won, win.Result);

        var lose = NewCombat(new[] { Strike, Strike, Strike, Strike, Strike }, Dummy(1000, 30), hp: 20);
        lose.EndPlayerTurn();
        Assert.Equal(CombatResult.Lost, lose.Result);
    }

    [Fact]
    public void DiscardIsReshuffledIntoTheDrawPileWhenItRunsOut()
    {
        var c = NewCombat(Enumerable.Repeat(Strike, 6).ToArray(), Dummy(1000, 0));
        c.EndPlayerTurn();                                // 5 discarded, 1 left in the draw pile
        Assert.Equal(5, c.Hand.Count);                    // 1 from the pile + reshuffle for the rest
        Assert.Equal(6, c.Hand.Count + c.DrawPile.Count + c.DiscardPile.Count);
    }

    [Fact]
    public void CycleMonsterFollowsItsStateMachine()
    {
        var a = new MoveDef { Id = "A", Damage = 1, DamageDeadly = 1 };
        var b = new MoveDef { Id = "B", Damage = 2, DamageDeadly = 2 };
        var def = new MonsterDef
        {
            Id = "CYCLER", HpMin = 500, HpMax = 500, HpMinTough = 500, HpMaxTough = 500,
            Moves = { ["A"] = a, ["B"] = b },
            States =
            {
                ["A_MOVE"] = new StateDef { Id = "A_MOVE", Kind = StateKind.Move, MoveId = "A", Next = "B_MOVE" },
                ["B_MOVE"] = new StateDef { Id = "B_MOVE", Kind = StateKind.Move, MoveId = "B", Next = "A_MOVE" },
            },
            InitialState = "A_MOVE",
        };
        var c = new Combat(new[] { Defend, Defend, Defend, Defend, Defend }, 80, 80, new[] { def }, 0, 1);
        var seen = new List<string>();
        for (int i = 0; i < 4; i++) { seen.Add(c.Enemies[0].Move!.Id); c.EndPlayerTurn(); }
        Assert.Equal(new[] { "A", "B", "A", "B" }, seen);
    }

    [Fact]
    public void ArtifactBlocksOneDebuff()
    {
        var c = NewCombat(new[] { Bash, Bash, Bash, Bash, Bash }, Dummy(100, 1));
        c.Enemies[0].Powers[(int)PowerKind.Artifact] = 1;
        c.Play(0, 0);
        Assert.Equal(0, c.Enemies[0].Powers[(int)PowerKind.Vulnerable]);
        Assert.Equal(0, c.Enemies[0].Powers[(int)PowerKind.Artifact]);
    }

    [Fact]
    public void SameSeedGivesTheSameFight()
    {
        CardDef[] deck = Enumerable.Repeat(Strike, 5).Concat(Enumerable.Repeat(Defend, 4)).Append(Bash).ToArray();
        FightResult a = FightSimulator.Run(deck, 80, 80, new[] { Dummy(60, 9) }, 0, seed: 42);
        FightResult b = FightSimulator.Run(deck, 80, 80, new[] { Dummy(60, 9) }, 0, seed: 42);
        Assert.Equal(a, b);
    }

    [Fact]
    public void BotBlocksWhenItMustAndAttacksWhenItCan()
    {
        // 14 incoming against 20 HP, a full hand of both kinds: it should survive a short fight by mixing block and damage.
        CardDef[] deck = Enumerable.Repeat(Strike, 5).Concat(Enumerable.Repeat(Defend, 5)).ToArray();
        int wins = Enumerable.Range(0, 50).Count(s => FightSimulator.Run(deck, 30, 30, new[] { Dummy(40, 6) }, 0, (ulong)s).Won);
        Assert.True(wins >= 45, $"expected the bot to win nearly every easy fight, won {wins}/50");
    }
}
