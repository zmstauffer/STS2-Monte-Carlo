using System.Diagnostics;
using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;

namespace SpireMonteCarlo.Advisor;

/// <summary>
/// advisor watch [--dir SNAPSHOTS] [--out ADVICE] [--n ROLLOUTS] [--seed S] [--all] [--once] [--timeout SECONDS]
///
/// Runs next to the game: every time the mod writes a snapshot of a decision screen, work out the advice for it, print it, and
/// write it as advice\latest.json / latest.txt (plus one file per snapshot). Snapshots that already exist when it starts are
/// ignored unless --all is given. If several arrive while it is busy, only the newest is advised on: older decisions are over.
/// </summary>
public static class WatchCommand
{
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string DefaultSnapshotFolder => Path.Combine(AppData, "SpireMonteCarlo", "snapshots");
    public static string DefaultAdviceFolder => Path.Combine(AppData, "SpireMonteCarlo", "advice");

    public static int Run(string[] args)
    {
        string snapshots = Option(args, "--dir") ?? DefaultSnapshotFolder;
        string advice = Option(args, "--out") ?? DefaultAdviceFolder;
        int? fixedRollouts = Option(args, "--n") is { } n ? int.Parse(n) : null;
        ulong seed = ulong.Parse(Option(args, "--seed") ?? "1");
        bool once = args.Contains("--once");
        double timeout = double.Parse(Option(args, "--timeout") ?? "0", System.Globalization.CultureInfo.InvariantCulture);

        var cache = new CodexCache();
        if (cache.ReadMeta() == null)
        {
            Console.Error.WriteLine("No Codex cache. Run 'advisor codex update' first.");
            return 1;
        }
        Directory.CreateDirectory(snapshots);
        Directory.CreateDirectory(advice);

        Console.WriteLine("Loading game data...");
        var loading = Stopwatch.StartNew();
        var data = new SimData(cache);
        Console.WriteLine($"Ready in {loading.Elapsed.TotalSeconds:F1}s. Watching {snapshots} (Ctrl+C to stop). Advice goes to {advice}.");

        // Everything the mod has written goes into the permanent decision log (the mod keeps only the newest 500 snapshots).
        bool archive = !args.Contains("--no-log");
        if (archive)
        {
            int archived = DecisionLog.Sweep(snapshots, advice);
            if (archived > 0) Console.WriteLine($"Archived {archived} snapshot/advice file(s) into {DecisionLog.DefaultFolder} ('advisor log' shows the record).");
        }
        var seen = new HashSet<string>(args.Contains("--all") ? Array.Empty<string>() : Directory.GetFiles(snapshots, "*.json"), StringComparer.OrdinalIgnoreCase);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var clock = Stopwatch.StartNew();
        WriteStatus(advice, "", WatcherStatus.Idle, "");
        var heartbeat = Stopwatch.StartNew();

        while (!cancel.IsCancellationRequested)
        {
            // The mod's panel reads the heartbeat to tell a running advisor from one that isn't.
            if (heartbeat.Elapsed.TotalSeconds >= 3) { WriteStatus(advice, _status.SnapshotFile, _status.State, _status.Message); heartbeat.Restart(); }
            string[] fresh = Directory.GetFiles(snapshots, "*.json").Where(f => !seen.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            if (fresh.Length > 0)
            {
                foreach (string skipped in fresh.SkipLast(1)) seen.Add(skipped);   // superseded before we got to them
                string latest = fresh[^1];
                seen.Add(latest);
                Advise(data, latest, advice, fixedRollouts, seed);
                if (archive) DecisionLog.Sweep(snapshots, advice);
                if (once) return 0;
            }
            if (timeout > 0 && clock.Elapsed.TotalSeconds > timeout) return once ? 2 : 0;
            cancel.Token.WaitHandle.WaitOne(400);
        }
        try { File.Delete(Path.Combine(advice, "status.json")); } catch (IOException) { }   // stopped: the panel says so at once
        return 0;
    }

    private static WatcherStatus _status = new();

    /// <summary>Writes advice\status.json for the in-game panel: which snapshot, what the advisor is doing with it, and a fresh heartbeat.</summary>
    public static void WriteStatus(string adviceFolder, string snapshotFile, string state, string message)
    {
        _status = new WatcherStatus { SnapshotFile = snapshotFile, State = state, Message = message, UpdatedAt = DateTimeOffset.Now };
        try { WriteAtomic(Path.Combine(adviceFolder, "status.json"), AdviceSerializer.Serialize(_status)); }
        catch (IOException) { }   // the game may be reading it; the next heartbeat tries again
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Writes a whole file under a temporary name and swaps it in, so a reader never sees half of it.</summary>
    private static void WriteAtomic(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Advises on one snapshot file and writes the results; problems are reported and never stop the watch.</summary>
    public static bool Advise(SimData data, string path, string adviceFolder, int? fixedRollouts, ulong seed)
    {
        try
        {
            RunSnapshot snapshot = ReadWhenReady(path);
            string file = Path.GetFileName(path);
            if (!DecisionAdvisor.Supports(snapshot))
            {
                WriteStatus(adviceFolder, file, WatcherStatus.Unsupported, "Nothing to advise on this screen.");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {Path.GetFileName(path)}: {snapshot.Decision}{(snapshot.EventId != null ? " " + snapshot.EventId : "")} - nothing to advise on.");
                return false;
            }

            int rollouts = fixedRollouts ?? DecisionAdvisor.DefaultRollouts(snapshot);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {Path.GetFileName(path)}: {snapshot.Decision}{(snapshot.EventId != null ? " " + snapshot.EventId : "")} - thinking...");
            WriteStatus(adviceFolder, file, WatcherStatus.Thinking, "");
            var sw = Stopwatch.StartNew();
            AdviceReport report = DecisionAdvisor.Evaluate(data, snapshot, rollouts, seed);
            sw.Stop();

            string text = AdviceFormatter.RenderText(Path.GetFileName(path), snapshot, report, rollouts, sw.Elapsed.TotalSeconds);
            AdviceResult result = AdviceFormatter.ToResult(path, snapshot, report, rollouts, sw.Elapsed.TotalSeconds);
            string json = AdviceSerializer.Serialize(result);
            string name = Path.GetFileNameWithoutExtension(path);
            File.WriteAllText(Path.Combine(adviceFolder, name + ".json"), json);
            WriteAtomic(Path.Combine(adviceFolder, "latest.json"), json);
            WriteAtomic(Path.Combine(adviceFolder, "latest.txt"), text);
            WriteStatus(adviceFolder, file, WatcherStatus.Done, "");

            Console.WriteLine();
            Console.Write(text);
            Console.WriteLine(new string('-', 80));
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {Path.GetFileName(path)}: could not advise ({e.GetType().Name}: {e.Message})");
            WriteStatus(adviceFolder, Path.GetFileName(path), WatcherStatus.Error, e.Message);
            return false;
        }
    }

    /// <summary>The mod may still be writing the file when it first appears; try again for a couple of seconds.</summary>
    private static RunSnapshot ReadWhenReady(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return SnapshotSerializer.Deserialize(File.ReadAllText(path)); }
            catch (Exception e) when (attempt < 20 && (e is IOException || e is Newtonsoft.Json.JsonException))
            {
                Thread.Sleep(100);
            }
        }
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
