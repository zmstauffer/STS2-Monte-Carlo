using SpireMonteCarlo.Advisor;
using SpireMonteCarlo.Contracts;

// Usage: advisor <snapshot.json>   or   advisor --latest
// Loads a snapshot written by the mod and describes it. Runs without the game.
string? path = args.Length == 1 && args[0] == "--latest" ? FindLatestSnapshot() : args.FirstOrDefault();
if (path == null || !File.Exists(path))
{
    Console.Error.WriteLine("Usage: advisor <snapshot.json> | advisor --latest");
    return 1;
}

Console.WriteLine(path);
Console.Write(SnapshotSummary.Render(SnapshotSerializer.Deserialize(File.ReadAllText(path))));
return 0;

static string? FindLatestSnapshot()
{
    string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpireMonteCarlo", "snapshots");
    return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").OrderBy(f => f).LastOrDefault() : null;
}
