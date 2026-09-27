using System.IO.Compression;
using System.Net;
using System.Text;
using SpireMonteCarlo.Codex;
using Xunit;

namespace SpireMonteCarlo.Tests;

public sealed class CodexCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smc-codex-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (string dir in new[] { _root, _root + ".staging" })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private const string CardsJson = """
        [
          { "id": "STRIKE_REGENT", "name": "Strike", "cost": 1, "type": "Attack", "rarity": "Basic", "color": "regent", "damage": 6, "block": null, "hit_count": null },
          { "id": "FIREBALL", "name": "Fireball", "cost": -1, "is_x_cost": true, "type": "Attack", "rarity": "Rare", "color": "defect", "damage": "+3", "block": null, "hit_count": 2 }
        ]
        """;

    private const string MetricsJson = """
        { "entity_type": "cards", "bracket": "wr50", "baseline_win_rate": 66.6, "total_runs": 100,
          "rows": [
            { "id": "STRIKE_REGENT", "upgraded": false, "elo": 1400.5, "win_rate": 60.0, "picks": 10, "offered": 30, "picked": 10 },
            { "id": "STRIKE_REGENT", "upgraded": true,  "elo": null,   "win_rate": 61.0, "picks": 2,  "offered": 0,  "picked": 0 }
          ] }
        """;

    private static byte[] ExportZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry cards = zip.CreateEntry("cards.json");
            using (var w = new StreamWriter(cards.Open(), new UTF8Encoding(false))) w.Write(CardsJson);
            // A hostile path must not escape the export folder.
            ZipArchiveEntry evil = zip.CreateEntry("../../evil.json");
            using (var w = new StreamWriter(evil.Open())) w.Write("{}");
        }
        return ms.ToArray();
    }

    private sealed class FakeCodex : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public Func<string, HttpResponseMessage>? Override { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.PathAndQuery;
            Requests.Add(path);
            if (Override?.Invoke(path) is { } custom) return Task.FromResult(custom);
            HttpContent content = path.Contains("exports/eng") ? new ByteArrayContent(ExportZip())
                : new StringContent(path.Contains("runs/metrics") ? MetricsJson : "{}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static CodexClient Client(FakeCodex handler) =>
        new(http: new HttpClient(handler), baseUrl: "https://codex.test/api", minInterval: TimeSpan.Zero);

    [Fact]
    public async Task UpdateWritesEveryFileAndMeta()
    {
        var cache = new CodexCache(_root);
        var handler = new FakeCodex();
        using CodexClient client = Client(handler);

        CodexMeta meta = await cache.UpdateAsync(client, "v9.9.9", new[] { "wr50" });

        Assert.Equal("v9.9.9", cache.ReadMeta()!.GameVersion);
        Assert.Contains("export/cards.json", meta.Files);
        Assert.Contains("metrics/cards_wr50.json", meta.Files);
        Assert.Contains("metrics/potions_wr50.json", meta.Files);
        Assert.Contains("encounter_stats.json", meta.Files);
        Assert.Contains("metrics/relics_act1_a10.json", meta.Files);
        Assert.Equal(1 + 3 + 3 + 1, handler.Requests.Count);   // export, metrics, relic scores per act, encounter stats
        Assert.False(File.Exists(Path.Combine(_root, "..", "evil.json")));
        Assert.False(Directory.Exists(_root + ".staging"));
    }

    [Fact]
    public async Task LoadsTypedDataIncludingMixedNumberAndStringFields()
    {
        var cache = new CodexCache(_root);
        using CodexClient client = Client(new FakeCodex());
        await cache.UpdateAsync(client, "v9.9.9", new[] { "wr50" });

        var cards = cache.LoadCards();
        Assert.Equal(6, cards["STRIKE_REGENT"].DamageAmount);
        Assert.Null(cards["FIREBALL"].DamageAmount);   // "+3" is a scaling string, not a plain amount
        Assert.Equal(2, cards["FIREBALL"].Hits);
        Assert.Equal(-1, cards["FIREBALL"].Cost);

        var metrics = cache.LoadMetrics("cards", "wr50");
        Assert.Single(metrics);                        // upgraded rows are dropped
        Assert.Equal(1400.5, metrics["STRIKE_REGENT"].Elo);
    }

    [Fact]
    public async Task NeedsUpdateWhenMissingOrGameVersionChanged()
    {
        var cache = new CodexCache(_root);
        Assert.True(cache.NeedsUpdate("v1"));

        using CodexClient client = Client(new FakeCodex());
        await cache.UpdateAsync(client, "v1", new[] { "wr50" });

        Assert.False(cache.NeedsUpdate("v1"));
        Assert.True(cache.NeedsUpdate("v2"));
        Assert.False(cache.NeedsUpdate(""));           // unknown game version: don't refresh needlessly
    }

    [Fact]
    public async Task FailedUpdateLeavesTheExistingCacheUntouched()
    {
        var cache = new CodexCache(_root);
        using (CodexClient good = Client(new FakeCodex()))
            await cache.UpdateAsync(good, "v1", new[] { "wr50" });

        var failing = new FakeCodex { Override = p => p.Contains("encounter-stats") ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null };
        using CodexClient bad = Client(failing);
        await Assert.ThrowsAsync<HttpRequestException>(() => cache.UpdateAsync(bad, "v2", new[] { "wr50" }));

        Assert.Equal("v1", cache.ReadMeta()!.GameVersion);
        Assert.True(File.Exists(Path.Combine(_root, "export", "cards.json")));
        Assert.False(Directory.Exists(_root + ".staging"));
    }
}

/// <summary>Runs against the real cache built by 'advisor codex update'; does nothing if it hasn't been built.</summary>
public class RealCodexDataTests
{
    private static CodexCache? RealCache()
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null ? null : cache;
    }

    [Fact]
    public void RealCardsMonstersAndEncountersDeserialize()
    {
        CodexCache? cache = RealCache();
        if (cache == null) return;

        var cards = cache.LoadCards();
        Assert.Equal(6, cards["STRIKE_IRONCLAD"].DamageAmount);
        Assert.Equal(1, cards["SHRUG_IT_OFF"].CardsDrawAmount);
        Assert.Equal("Vulnerable", cards["BASH"].PowersApplied![0].Power);

        var monsters = cache.LoadMonsters();
        Assert.Equal(3, monsters["CORPSE_SLUG"].Moves.Count);
        Assert.Equal("cycle", monsters["CORPSE_SLUG"].AttackPattern!.Type);
        Assert.Equal(12, monsters["NIBBIT"].Moves.First(m => m.Id == "BUTT").Damage!.Normal);

        Assert.Contains(cache.LoadEncounters(), e => e.Id == "CORPSE_SLUGS_NORMAL");
    }
}
