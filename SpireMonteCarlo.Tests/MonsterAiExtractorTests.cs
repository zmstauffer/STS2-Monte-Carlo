using SpireMonteCarlo.Codex;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>Uses synthetic source in the same shape as the decompiled monster classes (no game code is stored in the repo).</summary>
public class MonsterAiExtractorTests
{
    private const string RandomMonster = """
        List<MonsterState> list = new List<MonsterState>();
        MoveState moveState = new MoveState("SPORE_MOVE", SporeMove, new DebuffIntent());
        MoveState moveState2 = new MoveState("SMASH_MOVE", SmashMove, new SingleAttackIntent(SmashDamage));
        MoveState moveState3 = new MoveState("SLAM_MOVE", SlamMove, new SingleAttackIntent(SlamDamage));
        RandomBranchState randomBranchState = new RandomBranchState("RAND");
        RandomBranchState randomBranchState2 = new RandomBranchState("INITIAL");
        moveState.FollowUpState = randomBranchState;
        moveState2.FollowUpState = randomBranchState;
        moveState3.FollowUpState = randomBranchState;
        randomBranchState.AddBranch(moveState, 3, MoveRepeatType.CannotRepeat);
        randomBranchState.AddBranch(moveState2, 2, MoveRepeatType.CannotRepeat);
        randomBranchState.AddBranch(moveState3, MoveRepeatType.CannotRepeat);
        randomBranchState2.AddBranch(moveState2, 2, MoveRepeatType.CannotRepeat);
        randomBranchState2.AddBranch(moveState3, MoveRepeatType.UseOnlyOnce, 1.5f);
        return new MonsterMoveStateMachine(list, randomBranchState2);
        """;

    private const string CycleWithSwitch = """
        MoveState moveState = new MoveState("WHIP_MOVE", WhipMove, new MultiAttackIntent(WhipDamage, WhipRepeat));
        MoveState moveState2 = new MoveState("GOOP_MOVE", GoopMove, new DebuffIntent());
        moveState.FollowUpState = moveState2;
        moveState2.FollowUpState = moveState;
        return new MonsterMoveStateMachine(list, (StarterMoveIdx % 2) switch
        {
            0 => (MonsterState)moveState,
            _ => moveState2,
        });
        """;

    private const string Conditional = """
        MoveState moveState = new MoveState("BUTT_MOVE", ButtMove, new SingleAttackIntent(ButtDamage));
        MoveState moveState2 = new MoveState("HISS_MOVE", HissMove, new BuffIntent());
        ConditionalBranchState conditionalBranchState = new ConditionalBranchState("INIT_MOVE");
        conditionalBranchState.AddState(moveState, () => ((Nibbit)Creature.Monster).IsAlone);
        conditionalBranchState.AddState(moveState2, () => !((Nibbit)Creature.Monster).IsFront);
        moveState.FollowUpState = moveState2;
        moveState2.FollowUpState = moveState;
        return new MonsterMoveStateMachine(list, conditionalBranchState);
        """;

    [Fact]
    public void ParsesRandomBranchWeightsAndRepeatRules()
    {
        ExtractedMachine m = MonsterAiExtractor.Parse(RandomMonster)!;
        Assert.Equal("INITIAL", m.Initial);

        ExtractedState rand = m.States.Single(s => s.Id == "RAND");
        Assert.Equal("random", rand.Kind);
        Assert.Equal(new[] { 3.0, 2.0, 1.0 }, rand.Branches.Select(b => b.Weight));
        Assert.All(rand.Branches, b => Assert.Equal("CannotRepeat", b.Repeat));

        ExtractedBranch once = m.States.Single(s => s.Id == "INITIAL").Branches[1];
        Assert.Equal("UseOnlyOnce", once.Repeat);
        Assert.Equal(1.5, once.Weight);
    }

    [Fact]
    public void ParsesFollowUpsAndMoveIds()
    {
        ExtractedMachine m = MonsterAiExtractor.Parse(RandomMonster)!;
        ExtractedState spore = m.States.Single(s => s.Id == "SPORE_MOVE");
        Assert.Equal("SPORE", spore.MoveId);
        Assert.Equal("RAND", spore.Next);
    }

    [Fact]
    public void ParsesAStarterIndexSwitch()
    {
        ExtractedMachine m = MonsterAiExtractor.Parse(CycleWithSwitch)!;
        Assert.Null(m.Initial);
        Assert.Equal(new[] { "WHIP_MOVE", "GOOP_MOVE" }, m.StarterSwitch);
        Assert.Equal("GOOP_MOVE", m.States.Single(s => s.Id == "WHIP_MOVE").Next);
    }

    [Fact]
    public void ParsesConditionalBranchesInOrder()
    {
        ExtractedMachine m = MonsterAiExtractor.Parse(Conditional)!;
        ExtractedState init = m.States.Single(s => s.Id == "INIT_MOVE");
        Assert.Equal("conditional", init.Kind);
        Assert.Equal(new[] { "BUTT_MOVE", "HISS_MOVE" }, init.Branches.Select(b => b.StateId));
        Assert.Contains("IsAlone", init.Branches[0].Condition);
        Assert.StartsWith("!", init.Branches[1].Condition);
    }

    [Fact]
    public void ParsesAStateCreatedInlineAsAFollowUp()
    {
        const string source = """
            MoveState moveState = new MoveState("BUFF_MOVE", BuffMove, new BuffIntent());
            MoveState moveState2 = (MoveState)(moveState.FollowUpState = new MoveState("STRIKE_MOVE", StrikeMove, new SingleAttackIntent(StrikeDamage)));
            moveState2.FollowUpState = moveState2;
            return new MonsterMoveStateMachine(list, moveState);
            """;
        ExtractedMachine m = MonsterAiExtractor.Parse(source)!;
        Assert.Equal("BUFF_MOVE", m.Initial);
        Assert.Equal("STRIKE_MOVE", m.States.Single(s => s.Id == "BUFF_MOVE").Next);
        Assert.Equal("STRIKE_MOVE", m.States.Single(s => s.Id == "STRIKE_MOVE").Next);
    }

    [Fact]
    public void ParsesOneStateChainedAsTheFollowUpOfSeveral()
    {
        const string source = """
            MoveState moveState = new MoveState("A", AMove, new SingleAttackIntent(D));
            MoveState moveState2 = new MoveState("B", BMove, new BuffIntent());
            RandomBranchState randomBranchState = (RandomBranchState)(moveState2.FollowUpState = (moveState.FollowUpState = new RandomBranchState("RAND")));
            randomBranchState.AddBranch(moveState, MoveRepeatType.CannotRepeat);
            randomBranchState.AddBranch(moveState2, MoveRepeatType.CannotRepeat);
            return new MonsterMoveStateMachine(list, randomBranchState);
            """;
        ExtractedMachine m = MonsterAiExtractor.Parse(source)!;
        Assert.Equal("RAND", m.Initial);
        Assert.Equal("RAND", m.States.Single(s => s.Id == "A").Next);
        Assert.Equal("RAND", m.States.Single(s => s.Id == "B").Next);
        Assert.Equal(new[] { "A", "B" }, m.States.Single(s => s.Id == "RAND").Branches.Select(b => b.StateId));
    }

    [Fact]
    public void ParsesAFollowUpSetInAnObjectInitializer()
    {
        const string source = """
            MoveState moveState = new MoveState("PECK_MOVE", PeckMove, new MultiAttackIntent(PeckDamage, PeckRepeat));
            MoveState moveState2 = (MoveState)(moveState.FollowUpState = new MoveState("SWOOP_MOVE", SwoopMove, new SingleAttackIntent(SwoopDamage))
            {
                FollowUpState = moveState
            });
            return new MonsterMoveStateMachine(list, moveState2);
            """;
        ExtractedMachine m = MonsterAiExtractor.Parse(source)!;
        Assert.Equal("SWOOP_MOVE", m.Initial);
        Assert.Equal("SWOOP_MOVE", m.States.Single(s => s.Id == "PECK_MOVE").Next);
        Assert.Equal("PECK_MOVE", m.States.Single(s => s.Id == "SWOOP_MOVE").Next);
    }

    [Fact]
    public void AcceptsLowercaseStateIds()
    {
        const string source = """
            MoveState moveState = new MoveState("CONSTRICT", ConstrictMove, new DebuffIntent());
            MoveState moveState2 = new MoveState("LASH", LashMove, new SingleAttackIntent(LashDamage));
            RandomBranchState randomBranchState = (RandomBranchState)(moveState.FollowUpState = new RandomBranchState("rand"));
            moveState2.FollowUpState = moveState;
            randomBranchState.AddBranch(moveState2, MoveRepeatType.CanRepeatForever);
            return new MonsterMoveStateMachine(list, moveState);
            """;
        ExtractedMachine m = MonsterAiExtractor.Parse(source)!;
        Assert.Equal("rand", m.States.Single(s => s.Id == "CONSTRICT").Next);
        Assert.Equal("LASH", m.States.Single(s => s.Id == "rand").Branches.Single().StateId);
    }

    [Fact]
    public void ReturnsNullWhenThereIsNoStateMachine()
    {
        Assert.Null(MonsterAiExtractor.Parse("return 5;"));
    }

    [Fact]
    public void ConvertsClassNamesToIds()
    {
        Assert.Equal("CORPSE_SLUGS_NORMAL", DecompiledExtractor.ToSnakeCase("CorpseSlugsNormal"));
        Assert.Equal("LEAF_SLIME_M", DecompiledExtractor.ToSnakeCase("LeafSlimeM"));
        Assert.Equal("THE_KIN_BOSS", DecompiledExtractor.ToSnakeCase("TheKinBoss"));
    }
}
