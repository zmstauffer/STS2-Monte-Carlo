using SpireMonteCarlo.Advisor;
using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>Reading the owner's real fights out of the decision log for <c>sim calibrate-real</c>. Skipped when the local Codex cache isn't built.</summary>
public class CalibrateRealTests
{
    private static readonly Lazy<SimData?> Shared = new(() =>
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadMonsterAi() == null ? null : new SimData(cache);
    });

    private static RunSnapshot Snap(string decision, int floor, int hp, string[] normal, string[] elite, MapCoordinate? at = null) => new()
    {
        Decision = decision,
        Run = new RunInfo { Act = 2, TotalFloor = floor, CurrentHp = hp, MaxHp = 80, Seed = "S", Ascension = 10, Character = "ironclad" },
        Deck = new List<CardSnapshot> { new() { Id = "BASH" } },
        Plan = new ActPlan { ActId = "HIVE", Normal = normal.ToList(), Elite = elite.ToList(), Boss = "THE_INSATIABLE_BOSS" },
        Map = new MapSnapshot { Current = at ?? new MapCoordinate(1, floor - 17), Boss = new MapCoordinate(3, 16) },
    };

    private static List<CalibrateRealCommand.RealFight> Extract(params RunSnapshot[] snaps) =>
        CalibrateRealCommand.Extract(new[] { new DecisionLog.Run("S", snaps.Select((s, i) => new DecisionLog.Decision($"{i}.json", s, null, null)).ToList(), null) }, Shared.Value!).ToList();

    [Fact]
    public void AFightIsTheEncounterThatLeftThePlanBetweenAMapChoiceAndTheCardRewardAfterIt()
    {
        if (Shared.Value == null) return;
        var fights = Extract(
            Snap(DecisionType.Map, 20, 60, new[] { "EXOSKELETONS_NORMAL", "BOWLBUGS_NORMAL" }, new[] { "ENTOMANCER_ELITE" }),
            Snap(DecisionType.CardReward, 21, 49, new[] { "BOWLBUGS_NORMAL" }, new[] { "ENTOMANCER_ELITE" }));
        var fight = Assert.Single(fights);
        Assert.Equal("EXOSKELETONS_NORMAL", fight.Encounter);
        Assert.Equal((60, 49), (fight.HpBefore, fight.HpAfter));
    }

    [Fact]
    public void ADeathOneFloorAfterAMapChoiceIntoAnEliteIsAFatalFight()
    {
        if (Shared.Value == null) return;
        RunSnapshot map = Snap(DecisionType.Map, 20, 40, new[] { "BOWLBUGS_NORMAL" }, new[] { "ENTOMANCER_ELITE" }, at: new MapCoordinate(1, 3));
        map.Map!.Points.Add(new MapPointSnapshot { Col = 1, Row = 3, Type = "RestSite", Children = { new MapCoordinate(1, 4) } });
        map.Map.Points.Add(new MapPointSnapshot { Col = 1, Row = 4, Type = "Elite" });
        var end = new RunEnd { Seed = "S", Won = false, Act = 2, Floor = 21 };
        var run = new DecisionLog.Run("S", new List<DecisionLog.Decision> { new("0.json", map, null, null) }, end);
        var fight = Assert.Single(CalibrateRealCommand.Extract(new[] { run }, Shared.Value!));
        Assert.Equal("ENTOMANCER_ELITE", fight.Encounter);
        Assert.Equal((40, 0), (fight.HpBefore, fight.HpAfter));

        Assert.Empty(CalibrateRealCommand.Extract(new[] { run with { End = new RunEnd { Seed = "S", Won = false, Act = 2, Floor = 23 } } }, Shared.Value!));   // died later
    }

    [Fact]
    public void TheBossIsTheFightWhenTheCardRewardStandsOnTheBossNode()
    {
        if (Shared.Value == null) return;
        var fights = Extract(
            Snap(DecisionType.Map, 32, 70, new[] { "BOWLBUGS_NORMAL" }, new[] { "ENTOMANCER_ELITE" }),
            Snap(DecisionType.CardReward, 33, 52, new[] { "BOWLBUGS_NORMAL" }, new[] { "ENTOMANCER_ELITE" }, at: new MapCoordinate(3, 16)));
        Assert.Equal("THE_INSATIABLE_BOSS", Assert.Single(fights).Encounter);
    }

    [Fact]
    public void SnapshotsThatAreNotAMapChoiceFollowedByTheNextFloorsRewardAreNotFights()
    {
        if (Shared.Value == null) return;
        Assert.Empty(Extract(   // an event in between: the HP before the fight isn't known
            Snap(DecisionType.Event, 20, 60, new[] { "EXOSKELETONS_NORMAL" }, Array.Empty<string>()),
            Snap(DecisionType.CardReward, 21, 49, Array.Empty<string>(), Array.Empty<string>())));
        Assert.Empty(Extract(   // two floors apart
            Snap(DecisionType.Map, 19, 60, new[] { "EXOSKELETONS_NORMAL" }, Array.Empty<string>()),
            Snap(DecisionType.CardReward, 21, 49, Array.Empty<string>(), Array.Empty<string>())));
    }
}
