using SpireMonteCarlo.Advisor;
using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Contracts;

// advisor <snapshot.json> | --latest        describe a snapshot written by the mod (runs without the game)
// advisor codex update [--force] [--game-dir <path>]   refresh the local Spire Codex cache
// advisor codex status                       what the cache holds and how current it is
// advisor codex check <snapshot.json>|--latest   ids in a snapshot that the cache doesn't know
// advisor advise <snapshot.json>|--latest    simulate the options of a decision and recommend one
// advisor watch                              advise on every snapshot the mod writes, while you play
// advisor log                                the permanent record of runs: choices against the advice, predictions against outcomes
if (args.Length > 0 && args[0] == "codex")
    return await CodexCommands.RunAsync(args.Skip(1).ToArray());
if (args.Length > 0 && args[0] == "advise")
    return AdviseCommand.Run(args.Skip(1).ToArray());
if (args.Length > 0 && args[0] == "watch")
    return WatchCommand.Run(args.Skip(1).ToArray());
if (args.Length > 0 && args[0] == "log")
    return DecisionLog.Report(args.Skip(1).ToArray());
if (args.Length > 0 && args[0] == "sim")
    return SimCommands.Run(args.Skip(1).ToArray());

string? path = args.Length == 1 && args[0] == "--latest" ? SnapshotFiles.FindLatest() : args.FirstOrDefault();
if (path == null || !File.Exists(path))
{
    Console.Error.WriteLine("Usage: advisor <snapshot.json> | --latest | codex <update|status|check>");
    return 1;
}

Console.WriteLine(path);
Console.Write(SnapshotSummary.Render(SnapshotSerializer.Deserialize(File.ReadAllText(path))));
return 0;
