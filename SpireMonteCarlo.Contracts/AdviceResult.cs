using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SpireMonteCarlo.Contracts;

/// <summary>
/// What the advisor app says about one decision: plain data the mod (or anything else) can read to show a recommendation.
/// The app writes one of these next to each snapshot it advises on (advice\latest.json is always the newest).
/// </summary>
public sealed class AdviceResult
{
    /// <summary>Bumped on any breaking change to this format.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>File name of the snapshot this advice is for (so a display can tell whether it is still current).</summary>
    public string SnapshotFile { get; set; } = "";
    public string Decision { get; set; } = "";
    public string? EventId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>How many simulated futures each option got, and how long the whole evaluation took.</summary>
    public int Rollouts { get; set; }
    public double Seconds { get; set; }

    /// <summary>The label of the option the others are compared with (skip, rest, buy nothing, ...).</summary>
    public string Baseline { get; set; } = "";

    /// <summary>The recommended option's label, the one-line suggestion, and whether it is clearly better than every other option.</summary>
    public string Best { get; set; } = "";
    public string Suggestion { get; set; } = "";
    public bool BestIsClear { get; set; }

    /// <summary>Best first.</summary>
    public List<AdviceOption> Options { get; set; } = new();

    /// <summary>The recommendation in plain language, one point per line.</summary>
    public List<string> Why { get; set; } = new();

    /// <summary>Things the reader should know: options that couldn't be evaluated, parts of the run the simulator doesn't model.</summary>
    public List<string> Notes { get; set; } = new();
}

public sealed class AdviceOption
{
    public string Label { get; set; } = "";

    /// <summary>The label as a player would say it (card names instead of ids, "Elite (left)" instead of map columns), for displays.</summary>
    public string Display { get; set; } = "";
    public bool IsBaseline { get; set; }

    /// <summary>Chance to survive the rest of the act, and average HP left at its end (a death counts as 0), in percent and points.</summary>
    public double SurvivalPct { get; set; }
    public double HpLeft { get; set; }

    /// <summary>Share of deck-test fights the end-of-act deck wins; null in Act 3.</summary>
    public double? NextActEliteWinPct { get; set; }

    /// <summary>Average HP the end-of-act deck loses per deck-test fight (3 next-act elites and a boss, each from full HP); null in Act 3.</summary>
    public double? DeckTestHpLost { get; set; }

    /// <summary>
    /// Points behind the best option (0 for the best; percentage points of the chance to win the run as the advisor models it), its
    /// uncertainty (about two standard errors), and whether the gap is too small to call.
    /// </summary>
    public double PointsVsBest { get; set; }
    public double PointsVsBestUncertainty { get; set; }
    public bool AboutEqualToBest { get; set; }

    /// <summary>Points from the option's cards' worth after the current act (real players' ratings), already included in <see cref="PointsVsBest"/>.</summary>
    public double LongTermPoints { get; set; }

    /// <summary>Overall score difference from the baseline, its uncertainty (about two standard errors), and whether it is clearly not noise.</summary>
    public double DeltaScore { get; set; }
    public double DeltaScoreUncertainty { get; set; }
    public bool Clear { get; set; }
}

/// <summary>
/// What the advisor app is doing, for a display in the game: written as advice\status.json when it starts and finishes a snapshot, and
/// refreshed every few seconds while it waits (<see cref="UpdatedAt"/> is its heartbeat; a stale one means the app isn't running).
/// </summary>
public sealed class WatcherStatus
{
    public const string Thinking = "thinking", Done = "done", Unsupported = "unsupported", Error = "error", Idle = "idle";

    /// <summary>The snapshot the state refers to (empty before the first one).</summary>
    public string SnapshotFile { get; set; } = "";
    public string State { get; set; } = Idle;
    public string Message { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class AdviceSerializer
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
    };

    public static string Serialize(AdviceResult result) => JsonConvert.SerializeObject(result, Settings);

    public static string Serialize(WatcherStatus status) => JsonConvert.SerializeObject(status, Settings);

    public static WatcherStatus DeserializeStatus(string json) =>
        JsonConvert.DeserializeObject<WatcherStatus>(json, Settings) ?? throw new JsonException("Status JSON was empty.");

    public static AdviceResult Deserialize(string json) =>
        JsonConvert.DeserializeObject<AdviceResult>(json, Settings) ?? throw new JsonException("Advice JSON was empty.");
}
