using System.Diagnostics;
using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Sim;

namespace SpireMonteCarlo.Advisor;

public static class SimCommands
{
    private const string DefaultDecompiledDir = @"C:\Users\zmsta\source\repos\STS2 Monte Carlo\sts2-decompiled";

    public static int Run(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "extract": return Extract(args.Skip(1).ToArray());
            case "fight": return Fight(args.Skip(1).ToArray());
            case "encounters": return ListEncounters(args.Skip(1).ToArray());
            default:
                Console.Error.WriteLine("Usage: advisor sim <extract [--decompiled <dir>] | encounters [act] | fight --encounter <ID> [--n N] [--ascension A] [--hp N] [--deck SPEC] [--seed S]>");
                return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Extract(string[] args)
    {
        string root = Option(args, "--decompiled") ?? DefaultDecompiledDir;
        string encountersDir = Path.Combine(root, "MegaCrit.Sts2.Core.Models.Encounters");
        string monstersDir = Path.Combine(root, "MegaCrit.Sts2.Core.Models.Monsters");
        if (!Directory.Exists(encountersDir) || !Directory.Exists(monstersDir))
        {
            Console.Error.WriteLine($"Not found under {root} (pass --decompiled <dir> pointing at the ilspycmd output).");
            return 1;
        }
        var cache = new CodexCache();
        if (cache.ReadMeta() == null)
        {
            Console.Error.WriteLine("No Codex cache. Run 'advisor codex update' first.");
            return 1;
        }

        DecompiledExtractor.EncounterExtraction encounters = DecompiledExtractor.ExtractEncounters(encountersDir);
        cache.SaveEncounterLineups(encounters);
        Console.WriteLine($"Encounters: {encounters.Fixed.Count} with a fixed lineup, {encounters.Random.Count} random (written by hand in EncounterLibrary where needed).");

        Dictionary<string, ExtractedMachine> machines = MonsterAiExtractor.ExtractAll(monstersDir);
        cache.SaveMonsterAi(machines);
        var codexMonsters = cache.LoadMonsters();
        Console.WriteLine($"Monster AI: {machines.Count} state machines extracted for {codexMonsters.Count} Codex monsters.");

        var noMachine = codexMonsters.Keys.Where(id => !machines.ContainsKey(id)).OrderBy(id => id).ToList();
        if (noMachine.Count > 0) Console.WriteLine($"  no machine in their own class (inherited or missing), using Codex data: {string.Join(", ", noMachine)}");
        foreach ((string id, ExtractedMachine machine) in machines.OrderBy(kv => kv.Key))
        {
            if (!codexMonsters.TryGetValue(id, out CodexMonster? cm)) continue;
            var missing = machine.States.Where(s => s.Kind == "move" && (s.MoveId == null || !cm.Moves.Any(mv => mv.Id == s.MoveId))).Select(s => s.Id).ToList();
            if (missing.Count > 0) Console.WriteLine($"  {id}: moves not in Codex: {string.Join(", ", missing)}");
        }
        // A move state with no follow-up in a multi-state machine usually means the parser missed a link.
        foreach ((string id, ExtractedMachine machine) in machines.OrderBy(kv => kv.Key))
        {
            var dangling = machine.States.Where(s => s.Kind == "move" && s.Next == null).Select(s => s.Id).ToList();
            if (dangling.Count > 0 && machine.States.Count > 1)
                Console.WriteLine($"  {id}: no follow-up for {string.Join(", ", dangling)}");
        }
        Console.WriteLine($"Saved to {Path.Combine(cache.Root, "game")}");
        return 0;
    }

    private static SimData? Load()
    {
        var cache = new CodexCache();
        if (cache.ReadMeta() == null)
        {
            Console.Error.WriteLine("No Codex cache. Run 'advisor codex update' first.");
            return null;
        }
        return new SimData(cache);
    }

    private static int ListEncounters(string[] args)
    {
        SimData? data = Load();
        if (data == null) return 1;
        string? actFilter = args.FirstOrDefault();
        foreach (EncounterDef e in data.Encounters.All.Where(e => actFilter == null || (e.Act ?? "").Contains(actFilter, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Act).ThenBy(e => e.RoomType).ThenBy(e => e.Id))
            Console.WriteLine($"{e.Act ?? "(event)",-20} {e.RoomType,-8} {e.Id,-34}{(e.Approximate ? " [lineup guessed]" : "")}");
        return 0;
    }

    private static int Fight(string[] args)
    {
        SimData? data = Load();
        if (data == null) return 1;

        string? encounterId = Option(args, "--encounter");
        if (encounterId == null || !data.Encounters.Contains(encounterId))
        {
            Console.Error.WriteLine("Pass --encounter <ID> (see 'advisor sim encounters').");
            return 1;
        }
        int n = int.Parse(Option(args, "--n") ?? "2000");
        int ascension = int.Parse(Option(args, "--ascension") ?? "10");
        ulong seed = ulong.Parse(Option(args, "--seed") ?? "1");
        CodexCharacter ironclad = data.Characters["IRONCLAD"];
        int hp = int.Parse(Option(args, "--hp") ?? ironclad.StartingHp.ToString());
        string deckSpec = Option(args, "--deck") ?? string.Join(",", ironclad.StartingDeck.Select(DecompiledExtractor.ToSnakeCase));
        List<CardDef> deck = data.ParseDeck(deckSpec);
        EncounterDef encounter = data.Encounters.Get(encounterId);

        var sw = Stopwatch.StartNew();
        var results = new FightResult[n];
        var monsterIds = new string[n][];
        Parallel.For(0, n, i =>
        {
            ulong fightSeed = SimRng.Mix(seed, (ulong)i);
            string[] lineup = encounter.Generate(new SimRng(SimRng.Mix(fightSeed, 1)));
            monsterIds[i] = lineup;
            results[i] = FightSimulator.Run(deck, hp, hp, lineup.Select(data.Monsters.Get), ascension, fightSeed);
        });
        sw.Stop();

        int wins = results.Count(r => r.Won);
        double meanLostWhenWon = wins == 0 ? 0 : results.Where(r => r.Won).Average(r => r.HpLost);
        Console.WriteLine($"{encounter.Id} (A{ascension}), {n} fights, deck {deck.Count} cards, {hp} HP");
        Console.WriteLine($"  win rate {100.0 * wins / n:F1}%   mean HP lost (all) {results.Average(r => r.HpLost):F1}   mean HP lost (wins) {meanLostWhenWon:F1}   mean turns {results.Average(r => r.Turns):F1}");
        Console.WriteLine($"  {n / sw.Elapsed.TotalSeconds:F0} fights/sec ({sw.ElapsedMilliseconds} ms)");

        var lineups = monsterIds.Select(l => string.Join("+", l)).GroupBy(s => s).OrderByDescending(g => g.Count()).Take(3).ToList();
        Console.WriteLine($"  lineups: {string.Join(" | ", lineups.Select(g => $"{g.Key} x{g.Count()}"))}");

        var approxMonsters = monsterIds.SelectMany(l => l).Distinct().Select(data.Monsters.Get).Where(m => m.Approximate).ToList();
        if (approxMonsters.Count > 0)
            Console.WriteLine($"  approximated monsters: {string.Join(", ", approxMonsters.Select(m => m.Id + (m.IgnoredPowers.Length > 0 ? $" (ignores {string.Join("/", m.IgnoredPowers)})" : "")))}");
        var approxCards = deck.Where(c => c.Approximate).Select(c => c.ToString()).Distinct().ToList();
        if (approxCards.Count > 0) Console.WriteLine($"  approximated cards: {string.Join(", ", approxCards)}");
        if (encounter.Approximate) Console.WriteLine("  encounter lineup was guessed (run 'advisor sim extract')");
        return 0;
    }
}
