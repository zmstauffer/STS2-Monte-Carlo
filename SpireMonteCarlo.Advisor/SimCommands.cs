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
            case "calibrate": return Calibrate(args.Skip(1).ToArray());
            case "calibrate-run": return CalibrateRun(args.Skip(1).ToArray());
            case "rewards": return Rewards(args.Skip(1).ToArray());
            case "audit": return AuditCommand.Run(args.Skip(1).ToArray());
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
        foreach ((string id, ExtractedMachine machine) in machines.OrderBy(kv => kv.Key))
            if (machine.Initial == null && machine.StarterSwitch.Count == 0)
                Console.WriteLine($"  {id}: starting state not resolved");

        // A move state with no follow-up in a multi-state machine usually means the parser missed a link.
        foreach ((string id, ExtractedMachine machine) in machines.OrderBy(kv => kv.Key))
        {
            var dangling = machine.States.Where(s => s.Kind == "move" && s.Next == null).Select(s => s.Id).ToList();
            if (dangling.Count > 0 && machine.States.Count > 1)
                Console.WriteLine($"  {id}: no follow-up for {string.Join(", ", dangling)}");
        }
        Dictionary<string, ExtractedMonster> monsterClasses = MonsterClassExtractor.ExtractAll(monstersDir);
        cache.SaveMonsterClasses(monsterClasses);
        Console.WriteLine($"Monster classes: {monsterClasses.Count} read (hit points, damage, block, powers, cards and monsters added).");

        string cardsDir = Path.Combine(root, "MegaCrit.Sts2.Core.Models.Cards");
        if (Directory.Exists(cardsDir))
        {
            Dictionary<string, ExtractedCard> cards = CardSourceExtractor.ExtractAll(cardsDir);
            cache.SaveCardDefs(cards);
            var codexCards = cache.LoadCards();
            Console.WriteLine($"Cards: {cards.Count} extracted from the game ({codexCards.Count} in Codex; {cards.Keys.Count(id => !codexCards.ContainsKey(id))} newer than Codex).");
            foreach (ExtractedCard c in cards.Values.Where(c => c.UnparsedUpgrade.Count > 0).OrderBy(c => c.Id))
                Console.WriteLine($"  {c.Id}: upgrade statements not understood: {string.Join(" | ", c.UnparsedUpgrade)}");
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

    /// <summary>Sim versus real-player Ironclad results for every Act 1 encounter, to spot engine or data bugs.</summary>
    private static int Calibrate(string[] args)
    {
        var cache = new CodexCache();
        SimData? data = Load();
        if (data == null) return 1;
        int n = int.Parse(Option(args, "--n") ?? "1000");
        int ascension = int.Parse(Option(args, "--ascension") ?? "10");
        var stats = cache.LoadEncounterStats();
        CodexCharacter ironclad = data.Characters["IRONCLAD"];
        List<CardDef> deck = data.ParseDeck(string.Join(",", ironclad.StartingDeck.Select(DecompiledExtractor.ToSnakeCase)));

        var rows = new List<(EncounterDef e, double simDamage, double simWin, double simTurns, double realDamage, double realFatal, double realTurns, int realFights)>();
        foreach (EncounterDef e in data.Encounters.All.Where(e => (e.Act ?? "").StartsWith("Act 1") && stats.ContainsKey(e.Id)).OrderBy(e => e.RoomType).ThenBy(e => e.Id))
        {
            CodexCharacterStat? real = stats[e.Id].Characters.FirstOrDefault(c => c.Character == "IRONCLAD");
            if (real == null || real.Total == 0) continue;
            var results = new FightResult[n];
            Parallel.For(0, n, i =>
            {
                ulong seed = SimRng.Mix(7, (ulong)i);
                string[] lineup = e.Generate(new SimRng(SimRng.Mix(seed, 1)));
                results[i] = FightSimulator.Run(deck, ironclad.StartingHp, ironclad.StartingHp, lineup.Select(data.Monsters.Get), ascension, seed, altStarts: e.AltStarts, services: data.Services);
            });
            rows.Add((e, results.Average(r => r.HpLost), results.Count(r => r.Won) / (double)n, results.Average(r => r.Turns),
                real.AvgDamage, real.Fatal / (double)real.Total, real.AvgTurns, real.Total));
        }

        Console.WriteLine($"Ironclad starter deck at A{ascension}, {n} fights each, vs real Ironclad players (mixed decks and ascensions)");
        Console.WriteLine($"{"encounter",-32} {"room",-8} {"sim dmg",8} {"real dmg",9} {"sim turns",10} {"real turns",11} {"sim win%",9} {"real fatal%",12}");
        foreach (var r in rows)
            Console.WriteLine($"{r.e.Id,-32} {r.e.RoomType,-8} {r.simDamage,8:F1} {r.realDamage,9:F1} {r.simTurns,10:F1} {r.realTurns,11:F1} {100 * r.simWin,9:F1} {100 * r.realFatal,12:F2}{(r.e.Approximate ? "  [lineup guessed]" : "")}");

        Console.WriteLine();
        Console.WriteLine($"Rank correlation (Spearman) of average damage, all Act 1: {Spearman(rows.Select(r => r.simDamage).ToArray(), rows.Select(r => r.realDamage).ToArray()):F2}");
        foreach (string room in new[] { "Monster", "Elite", "Boss" })
        {
            var subset = rows.Where(r => r.e.RoomType == room).ToList();
            if (subset.Count >= 3)
                Console.WriteLine($"  {room,-8} n={subset.Count,2}  damage {Spearman(subset.Select(r => r.simDamage).ToArray(), subset.Select(r => r.realDamage).ToArray()):F2}   turns {Spearman(subset.Select(r => r.simTurns).ToArray(), subset.Select(r => r.realTurns).ToArray()):F2}");
        }
        return 0;
    }

    /// <summary>
    /// Whole-act check: Ironclad rollouts from the start of Act 1 (starter deck, default policies), comparing how often
    /// each encounter kills the run with how often it kills real Ironclad players. Needs a snapshot for its map.
    /// </summary>
    private static int CalibrateRun(string[] args)
    {
        string? mapPath = Option(args, "--map");
        var cache = new CodexCache();
        SimData? data = Load();
        if (data == null || mapPath == null || !File.Exists(mapPath))
        {
            Console.Error.WriteLine("Usage: advisor sim calibrate-run --map <snapshot.json with an Act 1 map> [--n N] [--ascension A]");
            return 1;
        }
        int n = int.Parse(Option(args, "--n") ?? "6000");
        int ascension = int.Parse(Option(args, "--ascension") ?? "10");
        var stats = cache.LoadEncounterStats();
        CodexCharacter ironclad = data.Characters["IRONCLAD"];
        List<CardDef> deck = data.ParseDeck(string.Join(",", ironclad.StartingDeck.Select(DecompiledExtractor.ToSnakeCase)));

        var rollouts = new List<ActRollout>();
        foreach (string variant in new[] { "OVERGROWTH", "UNDERDOCKS" })
        {
            Contracts.RunSnapshot snap = Contracts.SnapshotSerializer.Deserialize(File.ReadAllText(mapPath));
            snap.Run.Character = "ironclad"; snap.Run.Ascension = ascension; snap.Run.Act = 1;
            snap.Run.MaxHp = ironclad.StartingHp;
            snap.Run.Gold = ironclad.StartingGold;
            snap.Run.CurrentHp = ascension >= 2 ? (int)Math.Round(0.8 * ironclad.StartingHp) : ironclad.StartingHp;   // the first Ancient heals 80% of max HP from A2
            snap.Relics = new List<string> { "BURNING_BLOOD" };
            snap.Map!.Current = null; snap.Map.Visited = new();
            snap.Odds = null;
            snap.Plan = new Contracts.ActPlan { ActId = variant };
            rollouts.Add(new ActRollout(data, snap) { ProbeHpScale = double.Parse(Option(args, "--probe-scale") ?? ActRollout.CalibratedProbeHpScale.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture), PlayerHpScale = double.Parse(Option(args, "--hp-scale") ?? ActRollout.CalibratedPlayerHpScale.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture) });
        }

        var results = new RolloutResult[n];
        Parallel.For(0, n, i => results[i] = rollouts[i % rollouts.Count].Run(deck, SimRng.Mix(3, (ulong)i)));

        int survived = results.Count(r => r.Survived);
        Console.WriteLine($"Ironclad, Act 1 from the start, A{ascension}, {n} rollouts: survive {100.0 * survived / n:F1}%, mean HP left when surviving {results.Where(r => r.Survived).DefaultIfEmpty().Average(r => r?.HpEnd ?? 0):F0}");

        var fights = results.SelectMany(r => r.Encounters).GroupBy(e => e).ToDictionary(g => g.Key, g => g.Count());
        double PerRun(string room) => fights.Where(kv => data.Encounters.Contains(kv.Key) && data.Encounters.Get(kv.Key).RoomType == room).Sum(kv => kv.Value) / (double)n;
        var realFights = stats.Values.Where(s => s.Act == 1).GroupBy(s => s.RoomType.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Sum(s => s.Characters.Where(c => c.Character == "IRONCLAD").Sum(c => c.Total)));
        double realBoss = Math.Max(1, realFights.GetValueOrDefault("boss"));
        var probed = results.Where(r => r.Survived && r.ProbeFights > 0).ToList();
        if (probed.Count > 0) Console.WriteLine($"Act 2 elite probe (3 fights from the post-boss HP), runs that reached it: {100.0 * probed.Sum(r => r.ProbeWins) / probed.Sum(r => r.ProbeFights):F1}% won");
        Console.WriteLine($"Fights per run: normal {PerRun("Monster"):F1} (real {realFights.GetValueOrDefault("monster") / realBoss:F1}), elite {PerRun("Elite"):F1} (real {realFights.GetValueOrDefault("elite") / realBoss:F1})");
        var deaths = results.Where(r => r.DiedTo != null).GroupBy(r => r.DiedTo!).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"{"encounter",-32} {"room",-8} {"fights",7} {"sim fatal%",11} {"real fatal%",12}");
        foreach (var (id, count) in fights.OrderByDescending(kv => kv.Value))
        {
            if (count < n / 40 || !data.Encounters.Contains(id)) continue;
            CodexCharacterStat? real = stats.TryGetValue(id, out CodexEncounterStat? s) ? s.Characters.FirstOrDefault(c => c.Character == "IRONCLAD") : null;
            double simFatal = 100.0 * deaths.GetValueOrDefault(id) / count;
            Console.WriteLine($"{id,-32} {data.Encounters.Get(id).RoomType,-8} {count,7} {simFatal,11:F1} {(real == null ? "" : (100.0 * real.Fatal / real.Total).ToString("F1")),12}");
        }
        Console.WriteLine();
        Console.WriteLine("By fight number (all rollouts that reached it):   HP before   HP lost   deck size   alive");
        for (int k = 0; k < 14; k++)
        {
            var reached = results.Where(r => r.Log.Count > k).Select(r => r.Log[k]).ToList();
            if (reached.Count < n / 50) break;
            Console.WriteLine($"  fight {k + 1,2}: {reached.Count,5} runs    {reached.Average(e => e.HpBefore),6:F1}   {reached.Average(e => e.HpBefore - e.HpAfter),8:F1}   {reached.Average(e => e.DeckSize),9:F1}   {100.0 * reached.Count(e => e.HpAfter > 0) / reached.Count,5:F1}%");
        }
        Console.WriteLine();
        Console.WriteLine($"Died on: normal {100.0 * results.Count(r => r.DiedTo != null && data.Encounters.Get(r.DiedTo).RoomType == "Monster") / n:F1}%   elite {100.0 * results.Count(r => r.DiedTo != null && data.Encounters.Get(r.DiedTo).RoomType == "Elite") / n:F1}%   boss {100.0 * results.Count(r => r.DiedTo != null && data.Encounters.Get(r.DiedTo).RoomType == "Boss") / n:F1}%");
        return 0;
    }

    /// <summary>What the default reward policy picks, and how much of it the engine models properly.</summary>
    private static int Rewards(string[] args)
    {
        SimData? data = Load();
        if (data == null) return 1;
        int n = int.Parse(Option(args, "--n") ?? "20000");
        RewardPool pool = data.PoolFor("ironclad");
        var picks = new Dictionary<string, int>();
        int skipped = 0;
        var rng = new SimRng(11);
        var odds = new RarityOdds(10);
        for (int i = 0; i < n; i++)
        {
            string[] offer = pool.GenerateOffer(RewardKind.Normal, odds, rng);
            int pick = PickPolicy.Choose(offer, pool, deckSize: 10 + i % 8, rng);
            if (pick < 0) { skipped++; continue; }
            picks[offer[pick]] = picks.GetValueOrDefault(offer[pick]) + 1;
        }

        int taken = picks.Values.Sum();
        int approx = picks.Where(kv => data.Cards.Get(kv.Key, false).Approximate).Sum(kv => kv.Value);
        Console.WriteLine($"{n} rewards: {100.0 * skipped / n:F1}% skipped; of the cards taken, {100.0 * approx / taken:F1}% are only approximately modelled");
        Console.WriteLine($"{"card",-24} {"share",6}  approx  effects");
        foreach (var (id, count) in picks.OrderByDescending(kv => kv.Value).Take(25))
        {
            CardDef def = data.Cards.Get(id, false);
            Console.WriteLine($"{id,-24} {100.0 * count / taken,5:F1}%  {(def.Approximate ? "  yes " : "      ")}  {string.Join("; ", def.Effects.Select(e => $"{e.Op} {e.Amount}{(e.Hits != 1 ? $"x{e.Hits}" : "")}{(e.Power != PowerKind.Unsupported ? $" {e.Power}" : "")}"))}{(def.Effects.Length == 0 ? "(nothing)" : "")}");
        }
        return 0;
    }

    private static double Spearman(double[] a, double[] b)
    {
        double[] ra = Ranks(a), rb = Ranks(b);
        double ma = ra.Average(), mb = rb.Average();
        double cov = 0, va = 0, vb = 0;
        for (int i = 0; i < ra.Length; i++)
        {
            cov += (ra[i] - ma) * (rb[i] - mb);
            va += (ra[i] - ma) * (ra[i] - ma);
            vb += (rb[i] - mb) * (rb[i] - mb);
        }
        return va == 0 || vb == 0 ? 0 : cov / Math.Sqrt(va * vb);
    }

    private static double[] Ranks(double[] values)
    {
        int[] order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        var ranks = new double[values.Length];
        for (int i = 0; i < order.Length;)
        {
            int j = i;
            while (j + 1 < order.Length && values[order[j + 1]] == values[order[i]]) j++;
            double rank = (i + j) / 2.0;
            for (int k = i; k <= j; k++) ranks[order[k]] = rank;
            i = j + 1;
        }
        return ranks;
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
        var potions = (Option(args, "--potions") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => PotionLibrary.Find(id) ?? throw new ArgumentException($"Unknown or unmodelled potion {id}")).ToList();
        int stakes = encounter.RoomType switch { "Boss" => 2, "Elite" => 1, _ => 0 };
        var relics = (Option(args, "--relics") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => RelicRules.Parse(id) is var k && k != RelicKind.Unknown ? k : throw new ArgumentException($"Unknown or unmodelled relic {id}")).ToList();

        if (args.Contains("--trace"))
        {
            ulong traceSeed = SimRng.Mix(seed, 0);
            string[] traceLineup = encounter.Generate(new SimRng(SimRng.Mix(traceSeed, 1)));
            Console.WriteLine($"{encounter.Id} (A{ascension}): {string.Join(" + ", traceLineup)}");
            FightSimulator.Run(deck, hp, hp, traceLineup.Select(data.Monsters.Get), ascension, traceSeed, trace: Console.WriteLine, altStarts: encounter.AltStarts, services: data.Services, potions: potions, stakes: stakes, relics: relics);
            return 0;
        }

        var sw = Stopwatch.StartNew();
        var results = new FightResult[n];
        var monsterIds = new string[n][];
        Parallel.For(0, n, i =>
        {
            ulong fightSeed = SimRng.Mix(seed, (ulong)i);
            string[] lineup = encounter.Generate(new SimRng(SimRng.Mix(fightSeed, 1)));
            monsterIds[i] = lineup;
            results[i] = FightSimulator.Run(deck, hp, hp, lineup.Select(data.Monsters.Get), ascension, fightSeed, altStarts: encounter.AltStarts, services: data.Services, potions: potions, stakes: stakes, relics: relics);
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
