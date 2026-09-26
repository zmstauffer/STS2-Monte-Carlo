using SpireMonteCarlo.Advisor;
using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>The advice files the watch command writes, and the formatter behind them.</summary>
public class WatchTests
{
    private static SimData? Data()
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadMonsterAi() == null ? null : new SimData(cache);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string TempFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spire-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void AdviceResultsSurviveAJsonRoundTrip()
    {
        var result = new AdviceResult
        {
            SnapshotFile = "a.json", Decision = "shop", Best = "Buy nothing", Baseline = "Buy nothing", Suggestion = "Buy nothing.",
            Options = { new AdviceOption { Label = "Buy nothing", IsBaseline = true, SurvivalPct = 70.5, HpLeft = 21.3, NextActEliteWinPct = null } },
            Why = { "because" }, Notes = { "note" },
        };
        string json = AdviceSerializer.Serialize(result);
        Assert.Contains("\"snapshot_file\"", json);       // snake_case, like the snapshots
        Assert.DoesNotContain("next_act_elite_win_pct", json);   // no probe: left out entirely
        AdviceResult back = AdviceSerializer.Deserialize(json);
        Assert.Equal("Buy nothing", back.Best);
        Assert.Equal(70.5, back.Options[0].SurvivalPct);
        Assert.Equal("because", Assert.Single(back.Why));
    }

    [Fact]
    public void AdvisingOnASnapshotWritesTheJsonAndTextFiles()
    {
        SimData? data = Data();
        if (data == null) return;
        string advice = TempFolder();
        try
        {
            Assert.True(WatchCommand.Advise(data, Fixture("rest_site_act1.json"), advice, fixedRollouts: 60, seed: 1));
            Assert.True(File.Exists(Path.Combine(advice, "rest_site_act1.json")));
            Assert.True(File.Exists(Path.Combine(advice, "latest.txt")));
            AdviceResult latest = AdviceSerializer.Deserialize(File.ReadAllText(Path.Combine(advice, "latest.json")));
            Assert.Equal("rest_site_act1.json", latest.SnapshotFile);
            Assert.Equal("rest_site", latest.Decision);
            Assert.Equal(60, latest.Rollouts);
            Assert.NotEmpty(latest.Options);
            Assert.Equal(latest.Best, latest.Options[0].Label);
            Assert.Contains(latest.Options, o => o.IsBaseline);
            Assert.NotEmpty(latest.Why);
        }
        finally { Directory.Delete(advice, recursive: true); }
    }

    [Fact]
    public void ADecisionWeCannotAdviseOnIsSkippedWithoutFuss()
    {
        SimData? data = Data();
        if (data == null) return;
        string dir = TempFolder();
        try
        {
            string path = Path.Combine(dir, "combat.json");
            var snapshot = SnapshotSerializer.Deserialize(File.ReadAllText(Fixture("rest_site_act1.json")));
            snapshot.Decision = DecisionType.Combat;
            File.WriteAllText(path, SnapshotSerializer.Serialize(snapshot));
            Assert.False(WatchCommand.Advise(data, path, dir, fixedRollouts: 30, seed: 1));
            Assert.False(File.Exists(Path.Combine(dir, "latest.json")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ABrokenSnapshotFileDoesNotStopTheWatch()
    {
        SimData? data = Data();
        if (data == null) return;
        string dir = TempFolder();
        try
        {
            string path = Path.Combine(dir, "broken.json");
            File.WriteAllText(path, "{ this is not json");
            Assert.False(WatchCommand.Advise(data, path, dir, fixedRollouts: 30, seed: 1));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
