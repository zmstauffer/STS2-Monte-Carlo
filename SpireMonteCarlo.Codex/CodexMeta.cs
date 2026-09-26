namespace SpireMonteCarlo.Codex;

/// <summary>What the cache was built from; written as meta.json next to the data.</summary>
public sealed class CodexMeta
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>The game version the cache was refreshed for (from the game's release_info.json), or "" if unknown.</summary>
    public string GameVersion { get; set; } = "";

    /// <summary>Metrics brackets fetched (all, wr50, a10, ...).</summary>
    public List<string> Brackets { get; set; } = new();

    /// <summary>Last-modified time of each file inside the export ZIP; shows how stale Codex's game data is.</summary>
    public Dictionary<string, DateTimeOffset> ExportFileDates { get; set; } = new();

    /// <summary>Files written, relative to the cache root.</summary>
    public List<string> Files { get; set; } = new();
}
