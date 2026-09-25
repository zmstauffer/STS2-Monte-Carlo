using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SpireMonteCarlo.Contracts;

/// <summary>The single place that decides how snapshots look on disk / on the wire (snake_case JSON).</summary>
public static class SnapshotSerializer
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
    };

    public static string Serialize(RunSnapshot snapshot) => JsonConvert.SerializeObject(snapshot, Settings);

    public static RunSnapshot Deserialize(string json) =>
        JsonConvert.DeserializeObject<RunSnapshot>(json, Settings)
        ?? throw new JsonException("Snapshot JSON was empty.");
}
