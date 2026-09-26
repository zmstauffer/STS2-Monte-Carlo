using System.Text.Json;
using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Contracts;

namespace SpireMonteCarlo.Advisor;

public static class CodexCommands
{
    private const string DefaultGameDir = @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2";

    public static async Task<int> RunAsync(string[] args)
    {
        var cache = new CodexCache();
        switch (args.FirstOrDefault())
        {
            case "update": return await UpdateAsync(cache, args.Skip(1).ToArray());
            case "status": return Status(cache);
            case "check": return Check(cache, args.Skip(1).FirstOrDefault());
            default:
                Console.Error.WriteLine("Usage: advisor codex <update [--force] [--game-dir <path>] | status | check <snapshot.json>|--latest>");
                return 1;
        }
    }

    private static async Task<int> UpdateAsync(CodexCache cache, string[] args)
    {
        bool force = args.Contains("--force");
        int dirIndex = Array.IndexOf(args, "--game-dir");
        string gameDir = dirIndex >= 0 && dirIndex + 1 < args.Length ? args[dirIndex + 1] : DefaultGameDir;
        string gameVersion = ReadGameVersion(gameDir);

        if (!force && !cache.NeedsUpdate(gameVersion))
        {
            Console.WriteLine($"Cache is already built for game {(gameVersion == "" ? "(unknown version)" : gameVersion)}. Use --force to refresh anyway.");
            return 0;
        }

        // A free API key raises the per-endpoint limit from 15/min to 60/min; a full refresh stays well under either.
        using var client = new CodexClient(Environment.GetEnvironmentVariable("SPIRE_CODEX_API_KEY"));
        try
        {
            CodexMeta meta = await cache.UpdateAsync(client, gameVersion, log: Console.WriteLine);
            Console.WriteLine($"Done: {meta.Files.Count} files in {cache.Root} (game {(gameVersion == "" ? "unknown" : gameVersion)}).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Update failed, existing cache left untouched: {ex.Message}");
            return 2;
        }
    }

    private static int Status(CodexCache cache)
    {
        CodexMeta? meta = cache.ReadMeta();
        if (meta == null)
        {
            Console.WriteLine($"No cache at {cache.Root}. Run 'advisor codex update'.");
            return 1;
        }

        Console.WriteLine($"Cache: {cache.Root}");
        Console.WriteLine($"Fetched {meta.FetchedAt:yyyy-MM-dd HH:mm} for game {(meta.GameVersion == "" ? "(unknown)" : meta.GameVersion)}; brackets: {string.Join(", ", meta.Brackets)}");
        if (meta.ExportFileDates.Count > 0)
            Console.WriteLine($"Codex export data last changed {meta.ExportFileDates.Values.Min():yyyy-MM-dd} to {meta.ExportFileDates.Values.Max():yyyy-MM-dd}");

        IReadOnlyDictionary<string, CodexCard> cards = cache.LoadCards();
        Console.WriteLine($"Cards: {cards.Count} ({string.Join(", ", cards.Values.GroupBy(c => c.Color).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))})");
        foreach (string bracket in meta.Brackets)
        {
            CodexMetrics m = cache.LoadMetricsFile("cards", bracket);
            Console.WriteLine($"  cards/{bracket}: {m.Rows.Count} rows, {m.TotalRuns} runs, baseline win rate {m.BaselineWinRate}%");
        }
        return 0;
    }

    private static int Check(CodexCache cache, string? snapshotArg)
    {
        string? path = snapshotArg == "--latest" ? SnapshotFiles.FindLatest() : snapshotArg;
        if (path == null || !File.Exists(path))
        {
            Console.Error.WriteLine("Usage: advisor codex check <snapshot.json>|--latest");
            return 1;
        }
        if (cache.ReadMeta() == null)
        {
            Console.Error.WriteLine("No Codex cache. Run 'advisor codex update'.");
            return 1;
        }

        RunSnapshot s = SnapshotSerializer.Deserialize(File.ReadAllText(path));
        IReadOnlyDictionary<string, CodexCard> cards = cache.LoadCards();
        IReadOnlyDictionary<string, CodexMetricRow> cardElo = cache.LoadMetrics("cards", "all");
        IReadOnlyDictionary<string, CodexMetricRow> relics = cache.LoadMetrics("relics", "all");
        IReadOnlyDictionary<string, CodexMetricRow> potions = cache.LoadMetrics("potions", "all");

        var missingCards = s.Deck.Concat(s.Offer.Cards).Select(c => c.Id).Distinct().Where(id => !cards.ContainsKey(id)).ToList();
        var noElo = s.Offer.Cards.Select(c => c.Id).Distinct().Where(id => !cardElo.ContainsKey(id)).ToList();
        var missingRelics = s.Relics.Concat(s.Offer.Relics.Select(r => r.Id)).Distinct().Where(id => !relics.ContainsKey(id)).ToList();
        var missingPotions = s.Potions.Concat(s.Offer.Potions).Select(p => p.Id).Distinct().Where(id => !potions.ContainsKey(id)).ToList();

        Console.WriteLine(path);
        Report("Cards unknown to Codex", missingCards);
        Report("Offered cards without Elo", noElo);
        Report("Relics without metrics", missingRelics);
        Report("Potions without metrics", missingPotions);
        return 0;

        static void Report(string label, List<string> ids) =>
            Console.WriteLine(ids.Count == 0 ? $"{label}: none" : $"{label}: {string.Join(", ", ids)}");
    }

    private static string ReadGameVersion(string gameDir)
    {
        try
        {
            string file = Path.Combine(gameDir, "release_info.json");
            if (!File.Exists(file)) return "";
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("version", out JsonElement v) ? v.GetString() ?? "" : "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
