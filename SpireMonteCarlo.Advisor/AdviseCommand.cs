using System.Diagnostics;
using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;

namespace SpireMonteCarlo.Advisor;

public static class AdviseCommand
{
    /// <summary>advisor advise &lt;snapshot.json&gt;|--latest [--n ROLLOUTS] [--seed S]</summary>
    public static int Run(string[] args)
    {
        string? path = args.Contains("--latest") ? SnapshotFiles.FindLatest() : args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
        if (path == null)
        {
            Console.Error.WriteLine("Usage: advisor advise <snapshot.json>|--latest [--n ROLLOUTS] [--seed S]");
            return 1;
        }
        var cache = new CodexCache();
        if (cache.ReadMeta() == null)
        {
            Console.Error.WriteLine("No Codex cache. Run 'advisor codex update' first.");
            return 1;
        }

        RunSnapshot snapshot = SnapshotSerializer.Deserialize(File.ReadAllText(path));
        if (!DecisionAdvisor.Supports(snapshot))
        {
            Console.Error.WriteLine($"Decisions of type '{snapshot.Decision}' are not supported yet (card reward, rest site, card upgrade, map, shop, and Ancient relic choices are).");
            return 1;
        }
        int rollouts = int.Parse(Option(args, "--n") ?? DecisionAdvisor.DefaultRollouts(snapshot).ToString());
        ulong seed = ulong.Parse(Option(args, "--seed") ?? "1");
        var data = new SimData(cache);

        var sw = Stopwatch.StartNew();
        AdviceReport report = DecisionAdvisor.Evaluate(data, snapshot, rollouts, seed);
        sw.Stop();

        Console.Write(AdviceFormatter.RenderText(path, snapshot, report, rollouts, sw.Elapsed.TotalSeconds));
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
