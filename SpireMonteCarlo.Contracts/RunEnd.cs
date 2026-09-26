using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SpireMonteCarlo.Contracts;

/// <summary>
/// How a run ended, written by the mod when the game ends a run (a death or a win; abandoning a run counts as a death in the game).
/// Snapshots never show a death, so the advisor's decision log reads these to tell a run that died from one that was just stopped.
/// One file per ended run in <c>%APPDATA%\SpireMonteCarlo\runs\</c>.
/// </summary>
public sealed class RunEnd
{
    public int SchemaVersion { get; set; } = 1;
    /// <summary>The run's seed, as in <see cref="RunInfo.Seed"/> (joins with the snapshots).</summary>
    public string Seed { get; set; } = "";
    public string Character { get; set; } = "";
    public int Ascension { get; set; }
    public bool Won { get; set; }
    public int Act { get; set; }
    public int Floor { get; set; }
    public DateTimeOffset EndedAt { get; set; }
}

public static class RunEndSerializer
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
        Formatting = Formatting.Indented,
    };

    public static string Serialize(RunEnd end) => JsonConvert.SerializeObject(end, Settings);

    public static RunEnd Deserialize(string json) => JsonConvert.DeserializeObject<RunEnd>(json, Settings) ?? throw new JsonException("Empty run-end file.");
}
