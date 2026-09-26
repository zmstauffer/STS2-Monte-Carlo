namespace SpireMonteCarlo.Advisor;

public static class SnapshotFiles
{
    public static string? FindLatest()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpireMonteCarlo", "snapshots");
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").OrderBy(f => f).LastOrDefault() : null;
    }
}
