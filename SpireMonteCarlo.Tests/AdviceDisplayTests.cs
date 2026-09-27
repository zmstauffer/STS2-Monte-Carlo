using SpireMonteCarlo.Advisor;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>What the in-game Spire MC panel reads: option names as a player would say them, and the advisor's status file.</summary>
public class AdviceDisplayTests
{
    private static AdviceReport Report(string decision, params string[] labels) => new()
    {
        Decision = decision,
        Options = labels.Select(l => new OptionReport { Label = l }).ToList(),
    };

    [Fact]
    public void MapChoicesAreNamedByRoomAndPositionNotByColumn()
    {
        var snapshot = new RunSnapshot { Decision = DecisionType.Map };
        var names = AdviceFormatter.DisplayLabels(snapshot, Report(DecisionType.Map, "RestSite (column 4, row 8)", "Unknown (column 1, row 8)", "Elite (column 2, row 8)"));
        Assert.Equal("? room (left)", names["Unknown (column 1, row 8)"]);
        Assert.Equal("Elite (middle)", names["Elite (column 2, row 8)"]);
        Assert.Equal("Rest site (right)", names["RestSite (column 4, row 8)"]);

        var one = AdviceFormatter.DisplayLabels(snapshot, Report(DecisionType.Map, "Monster (column 3, row 2)"));
        Assert.Equal("Monster", one["Monster (column 3, row 2)"]);
    }

    [Fact]
    public void OtherChoicesUseReadableCardNames()
    {
        var names = AdviceFormatter.DisplayLabels(new RunSnapshot { Decision = DecisionType.CardReward }, Report(DecisionType.CardReward, "Skip", "PERFECTED_STRIKE"));
        Assert.Equal("Perfected Strike", names["PERFECTED_STRIKE"]);
        Assert.Equal("Skip", names["Skip"]);
    }

    [Fact]
    public void TheWatcherStatusRoundTripsInSnakeCase()
    {
        var status = new WatcherStatus { SnapshotFile = "x_map.json", State = WatcherStatus.Thinking, UpdatedAt = DateTimeOffset.Now };
        string json = AdviceSerializer.Serialize(status);
        Assert.Contains("\"snapshot_file\"", json);
        WatcherStatus back = AdviceSerializer.DeserializeStatus(json);
        Assert.Equal(("x_map.json", WatcherStatus.Thinking), (back.SnapshotFile, back.State));
    }
}
