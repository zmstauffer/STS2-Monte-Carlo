using System.IO.Compression;
using System.Text.Json;

namespace SpireMonteCarlo.Codex;

/// <summary>
/// Local copy of Spire Codex data under %APPDATA%\SpireMonteCarlo\codex\. Decisions read only from here;
/// the network is touched only by <see cref="UpdateAsync"/>.
/// Layout: export\*.json (entity data from /exports/eng), metrics\{type}_{bracket}.json (Elo, win rate,
/// pick rate for cards/relics/potions), encounter_stats.json (for calibrating the simulator), meta.json.
/// </summary>
public sealed class CodexCache
{
    public static readonly string[] DefaultBrackets = { "all", "wr50", "a10" };
    public static readonly string[] MetricTypes = { "cards", "relics", "potions" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public string Root { get; }

    public CodexCache(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpireMonteCarlo", "codex");
    }

    private string MetaPath => Path.Combine(Root, "meta.json");

    public CodexMeta? ReadMeta() =>
        File.Exists(MetaPath) ? JsonSerializer.Deserialize<CodexMeta>(File.ReadAllText(MetaPath), Json) : null;

    /// <summary>True when there is no cache, or the game has been patched since it was built.</summary>
    public bool NeedsUpdate(string gameVersion)
    {
        CodexMeta? meta = ReadMeta();
        return meta == null || (gameVersion != "" && meta.GameVersion != gameVersion);
    }

    /// <summary>
    /// Downloads everything into a staging folder and swaps it in only if every request succeeded,
    /// so a failed refresh (network, rate limit) leaves the existing cache untouched.
    /// </summary>
    public async Task<CodexMeta> UpdateAsync(CodexClient client, string gameVersion, IEnumerable<string>? brackets = null,
        Action<string>? log = null, CancellationToken ct = default)
    {
        string[] bracketList = (brackets ?? DefaultBrackets).ToArray();
        string staging = Root.TrimEnd('\\', '/') + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        var meta = new CodexMeta { FetchedAt = DateTimeOffset.Now, GameVersion = gameVersion, Brackets = bracketList.ToList() };
        try
        {
            log?.Invoke("Downloading entity export...");
            byte[] zip = await client.GetBytesAsync("exports/eng", ct);
            ExtractExport(zip, Path.Combine(staging, "export"), meta);

            foreach (string type in MetricTypes)
            {
                foreach (string bracket in bracketList)
                {
                    log?.Invoke($"Downloading {type} metrics ({bracket})...");
                    byte[] data = await client.GetBytesAsync($"runs/metrics/{type}?bracket={Uri.EscapeDataString(bracket)}", ct);
                    Save(staging, Path.Combine("metrics", $"{type}_{bracket}.json"), data, meta);
                }
            }

            log?.Invoke("Downloading encounter stats...");
            Save(staging, "encounter_stats.json", await DownloadEncounterStatsAsync(client, ct), meta);

            meta.Files.Sort(StringComparer.Ordinal);
            File.WriteAllText(Path.Combine(staging, "meta.json"), JsonSerializer.Serialize(meta, Json));
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }

        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        Directory.Move(staging, Root);
        return meta;
    }

    /// <summary>/runs/encounter-stats is paginated; merges every page into one {"encounters":[...]} document.</summary>
    private static async Task<byte[]> DownloadEncounterStatsAsync(CodexClient client, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        for (int page = 1; ; page++)
        {
            byte[] bytes = await client.GetBytesAsync($"runs/encounter-stats?page={page}&limit=100", ct);
            using JsonDocument doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("encounters", out JsonElement rows))
                return bytes;   // not the paginated shape; keep it as received
            all.AddRange(rows.EnumerateArray().Select(r => r.Clone()));
            bool hasNext = doc.RootElement.TryGetProperty("has_next", out JsonElement next) && next.ValueKind == JsonValueKind.True;
            if (!hasNext || rows.GetArrayLength() == 0) break;
        }
        return JsonSerializer.SerializeToUtf8Bytes(new { encounters = all });
    }

    /// <summary>Refreshes only encounter_stats.json in the existing cache (no need to redownload the entity export).</summary>
    public async Task UpdateEncounterStatsAsync(CodexClient client, CancellationToken ct = default)
    {
        byte[] data = await DownloadEncounterStatsAsync(client, ct);
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(Path.Combine(Root, "encounter_stats.json"), data);
    }

    private static void Save(string dir, string relative, byte[] data, CodexMeta meta)
    {
        string path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        meta.Files.Add(relative.Replace('\\', '/'));
    }

    private static void ExtractExport(byte[] zip, string exportDir, CodexMeta meta)
    {
        Directory.CreateDirectory(exportDir);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            // Flat archive of JSON files; take only the file name so a hostile entry path can't escape the folder.
            if (entry.Name == "" || !entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(exportDir, Path.GetFileName(entry.Name));
            using (Stream input = entry.Open())
            using (FileStream output = File.Create(target))
                input.CopyTo(output);
            meta.Files.Add("export/" + Path.GetFileName(entry.Name));
            meta.ExportFileDates[entry.Name] = entry.LastWriteTime;
        }
    }

    /// <summary>All cards by id (UPPER_SNAKE_CASE, matching the mod's snapshots).</summary>
    public IReadOnlyDictionary<string, CodexCard> LoadCards() =>
        Read<List<CodexCard>>(Path.Combine("export", "cards.json")).ToDictionary(c => c.Id);

    public IReadOnlyDictionary<string, CodexMonster> LoadMonsters() =>
        Read<List<CodexMonster>>(Path.Combine("export", "monsters.json")).ToDictionary(m => m.Id);

    public IReadOnlyList<CodexEncounter> LoadEncounters() =>
        Read<List<CodexEncounter>>(Path.Combine("export", "encounters.json"));

    /// <summary>Encounter lineups extracted from the decompiled game (see <see cref="DecompiledExtractor"/>), or null if not extracted yet.</summary>
    public DecompiledExtractor.EncounterExtraction? LoadEncounterLineups()
    {
        string path = Path.Combine(Root, "game", "encounter_lineups.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<DecompiledExtractor.EncounterExtraction>(File.ReadAllText(path), Json) : null;
    }

    public void SaveEncounterLineups(DecompiledExtractor.EncounterExtraction extraction)
    {
        string dir = Path.Combine(Root, "game");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "encounter_lineups.json"), JsonSerializer.Serialize(extraction, Json));
    }

    public IReadOnlyDictionary<string, CodexCharacter> LoadCharacters() =>
        Read<List<CodexCharacter>>(Path.Combine("export", "characters.json")).ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Monster move state machines extracted from the decompiled game (see <see cref="MonsterAiExtractor"/>), or null if not extracted yet.</summary>
    public Dictionary<string, ExtractedMachine>? LoadMonsterAi()
    {
        string path = Path.Combine(Root, "game", "monster_ai.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, ExtractedMachine>>(File.ReadAllText(path), Json) : null;
    }

    public void SaveMonsterAi(Dictionary<string, ExtractedMachine> machines)
    {
        string dir = Path.Combine(Root, "game");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "monster_ai.json"), JsonSerializer.Serialize(machines, Json));
    }

    /// <summary>Card definitions extracted from the decompiled game (see <see cref="CardSourceExtractor"/>), or null if not extracted yet.</summary>
    public Dictionary<string, ExtractedCard>? LoadCardDefs()
    {
        string path = Path.Combine(Root, "game", "card_defs.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, ExtractedCard>>(File.ReadAllText(path), Json) : null;
    }

    public void SaveCardDefs(Dictionary<string, ExtractedCard> cards)
    {
        string dir = Path.Combine(Root, "game");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "card_defs.json"), JsonSerializer.Serialize(cards, Json));
    }

    /// <summary>Monster numbers and move effects extracted from the decompiled game (see <see cref="MonsterClassExtractor"/>), or null if not extracted yet.</summary>
    public Dictionary<string, ExtractedMonster>? LoadMonsterClasses()
    {
        string path = Path.Combine(Root, "game", "monster_classes.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, ExtractedMonster>>(File.ReadAllText(path), Json) : null;
    }

    public void SaveMonsterClasses(Dictionary<string, ExtractedMonster> monsters)
    {
        string dir = Path.Combine(Root, "game");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "monster_classes.json"), JsonSerializer.Serialize(monsters, Json));
    }

    public IReadOnlyDictionary<string, CodexEncounterStat> LoadEncounterStats() =>
        // Some encounters (event fights) are listed once per act; keep the biggest sample.
        Read<CodexEncounterStats>("encounter_stats.json").Encounters
            .GroupBy(e => e.EncounterId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Total).First());

    /// <summary>Base-card (non-upgraded) metric rows by id for one bracket, e.g. "wr50".</summary>
    public IReadOnlyDictionary<string, CodexMetricRow> LoadMetrics(string type, string bracket) =>
        Read<CodexMetrics>(Path.Combine("metrics", $"{type}_{bracket}.json")).Rows
            .Where(r => !r.Upgraded).ToDictionary(r => r.Id);

    public CodexMetrics LoadMetricsFile(string type, string bracket) =>
        Read<CodexMetrics>(Path.Combine("metrics", $"{type}_{bracket}.json"));

    private T Read<T>(string relative)
    {
        string path = Path.Combine(Root, relative);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Codex cache is missing {relative}. Run 'advisor codex update'.", path);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
               ?? throw new JsonException($"{relative} was empty.");
    }
}
