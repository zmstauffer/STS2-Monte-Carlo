using System.Globalization;
using System.Text.RegularExpressions;
using SpireMonteCarlo.Contracts;

namespace SpireMonteCarlo.Advisor;

/// <summary>
/// A permanent record of every run played with the mod, and what it says about the advice. The mod keeps only the newest 500
/// snapshots, so <see cref="Sweep"/> copies each snapshot (and the advice written for it) into
/// <c>%APPDATA%\SpireMonteCarlo\log\runs\&lt;seed&gt;\</c>, never deleting anything. <c>advisor log</c> reads that archive: what was
/// chosen at each decision (worked out from the next snapshot), whether it was the advice (or tied with it), and how the advisor's
/// predicted chance of surviving the act compares with what happened (act reached, and deaths from the mod's run-end files).
/// </summary>
public static class DecisionLog
{
    private static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpireMonteCarlo");
    public static string DefaultFolder => Path.Combine(AppData, "log");
    public static string RunEndFolder => Path.Combine(AppData, "runs");

    private static readonly Dictionary<string, string> SeedOf = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies snapshots and advice not yet archived. Returns how many files were copied.</summary>
    public static int Sweep(string snapshotFolder, string adviceFolder, string? logFolder = null)
    {
        string runs = Path.Combine(logFolder ?? DefaultFolder, "runs");
        int copied = 0;
        if (!Directory.Exists(snapshotFolder)) return 0;
        foreach (string snapshot in Directory.GetFiles(snapshotFolder, "*.json"))
        {
            string name = Path.GetFileName(snapshot);
            try
            {
                if (!SeedOf.TryGetValue(name, out string? seed))
                {
                    seed = SnapshotSerializer.Deserialize(File.ReadAllText(snapshot)).Run.Seed;
                    if (string.IsNullOrWhiteSpace(seed)) seed = "unknown";
                    SeedOf[name] = seed;
                }
                string runFolder = Path.Combine(runs, seed);
                string target = Path.Combine(runFolder, name);
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(runFolder);
                    File.Copy(snapshot, target);
                    copied++;
                }
                string advice = Path.Combine(adviceFolder, name);
                string adviceTarget = Path.Combine(runFolder, "advice", name);
                if (File.Exists(advice) && !File.Exists(adviceTarget))
                {
                    Directory.CreateDirectory(Path.Combine(runFolder, "advice"));
                    File.Copy(advice, adviceTarget);
                    copied++;
                }
            }
            catch (Exception e) when (e is IOException || e is Newtonsoft.Json.JsonException || e is UnauthorizedAccessException)
            {
                // Still being written, or unreadable: the next sweep tries again.
            }
        }
        return copied;
    }

    // ---- reading the archive ----

    /// <summary>One decision of one run: the snapshot, the advice for it (if any), and what was chosen.</summary>
    public sealed record Decision(string File, RunSnapshot Snapshot, AdviceResult? Advice, string? Chosen);

    public sealed record Run(string Seed, List<Decision> Decisions, RunEnd? End)
    {
        public int MaxAct => Decisions.Max(d => d.Snapshot.Run.Act);
        public DateTimeOffset Started => Decisions[0].Snapshot.CapturedAt;
        public DateTimeOffset Last => Decisions[^1].Snapshot.CapturedAt;
    }

    public static List<Run> Load(string? logFolder = null, string? runEndFolder = null)
    {
        string runs = Path.Combine(logFolder ?? DefaultFolder, "runs");
        var ends = new List<RunEnd>();
        if (Directory.Exists(runEndFolder ?? RunEndFolder))
            foreach (string f in Directory.GetFiles(runEndFolder ?? RunEndFolder, "*.json"))
                try { ends.Add(RunEndSerializer.Deserialize(File.ReadAllText(f))); } catch (Newtonsoft.Json.JsonException) { }
        var result = new List<Run>();
        if (!Directory.Exists(runs)) return result;
        foreach (string dir in Directory.GetDirectories(runs))
        {
            var snaps = new List<(string File, RunSnapshot Snap, AdviceResult? Advice)>();
            foreach (string f in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    RunSnapshot s = SnapshotSerializer.Deserialize(File.ReadAllText(f));
                    string advicePath = Path.Combine(dir, "advice", Path.GetFileName(f));
                    AdviceResult? a = File.Exists(advicePath) ? AdviceSerializer.Deserialize(File.ReadAllText(advicePath)) : null;
                    snaps.Add((Path.GetFileName(f), s, a));
                }
                catch (Newtonsoft.Json.JsonException) { }
            }
            if (snaps.Count == 0) continue;
            var decisions = new List<Decision>();
            for (int i = 0; i < snaps.Count; i++)
                decisions.Add(new Decision(snaps[i].File, snaps[i].Snap, snaps[i].Advice, Chosen(snaps, i)));
            string seed = Path.GetFileName(dir);
            result.Add(new Run(seed, decisions, ends.Where(e => e.Seed == seed).OrderBy(e => e.EndedAt).LastOrDefault()));
        }
        return result.OrderBy(r => r.Started).ToList();
    }

    /// <summary>What was chosen at snapshot <paramref name="i"/>, as an advice label (or a label-like description); null when it can't be told.</summary>
    public static string? Chosen(IReadOnlyList<(string File, RunSnapshot Snap, AdviceResult? Advice)> snaps, int i)
    {
        RunSnapshot s = snaps[i].Snap;
        // A shop can take several snapshots (one per purchase screen); what it sold shows by the first snapshot after the shop.
        int j = i + 1;
        if (s.Decision == DecisionType.Shop)
            while (j < snaps.Count && snaps[j].Snap.Decision == DecisionType.Shop && snaps[j].Snap.Run.TotalFloor == s.Run.TotalFloor) j++;
        if (j >= snaps.Count) return null;
        RunSnapshot next = snaps[j].Snap;
        var added = Diff(next.Deck, s.Deck);
        var removed = Diff(s.Deck, next.Deck);
        switch (s.Decision)
        {
            case DecisionType.CardReward:
            {
                string? taken = s.Offer.Cards.Select(c => c.Id).FirstOrDefault(id => added.Contains(id));
                return taken ?? "Skip";
            }
            case DecisionType.Map:
                // At an act's start the next snapshot is the Ancient, still on the start node: the choice isn't made yet.
                return next.Map?.Current is { } at && at.Row > 0 && !(s.Map?.Current is { } was && was.Col == at.Col && was.Row == at.Row)
                    ? $"column {at.Col}, row {at.Row}" : null;
            case DecisionType.RestSite:
                if (next.Decision == DecisionType.CardUpgrade && next.Run.TotalFloor == s.Run.TotalFloor) return "Upgrade";
                return next.Run.CurrentHp > s.Run.CurrentHp ? "Rest" : null;
            case DecisionType.CardUpgrade:
            {
                var before = s.Deck.Where(c => !c.Upgraded).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Count());
                var after = next.Deck.Where(c => !c.Upgraded).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Count());
                string? up = before.Keys.FirstOrDefault(id => after.GetValueOrDefault(id) < before[id] && next.Deck.Any(c => c.Id == id && c.Upgraded));
                return up != null ? $"Upgrade {up}" : null;
            }
            case DecisionType.Shop:
            {
                var bought = new List<string>();
                bought.AddRange(added.Select(id => $"card {id}"));
                bought.AddRange(next.Relics.Where(r => !s.Relics.Contains(r)).Select(r => $"relic {r}"));
                bought.AddRange(next.Potions.Select(p => p.Id).Except(s.Potions.Select(p => p.Id)).Select(p => $"potion {p}"));
                bought.AddRange(removed.Select(id => $"remove {id}"));
                return bought.Count == 0 ? "Buy nothing" : string.Join(" + ", bought.OrderBy(b => b, StringComparer.Ordinal));
            }
            case DecisionType.Event:
            {
                string? relic = next.Relics.FirstOrDefault(r => !s.Relics.Contains(r));
                return relic != null && s.EventOptions.Any(o => o.Relic == relic) ? relic : null;
            }
        }
        return null;
    }

    private static List<string> Diff(List<CardSnapshot> a, List<CardSnapshot> b)
    {
        var left = b.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<string>();
        foreach (CardSnapshot c in a)
        {
            if (left.TryGetValue(c.Id, out int n) && n > 0) left[c.Id] = n - 1;
            else result.Add(c.Id);
        }
        return result;
    }

    /// <summary>Which advice option a choice was, or null. Map labels end with the node; shop labels are item lists with prices.</summary>
    public static AdviceOption? MatchOption(AdviceResult advice, string decision, string chosen)
    {
        foreach (AdviceOption o in advice.Options)
        {
            bool match = decision switch
            {
                DecisionType.Map => o.Label.Contains(chosen, StringComparison.Ordinal),
                DecisionType.RestSite => chosen == "Rest" ? o.Label.StartsWith("Rest", StringComparison.Ordinal) : o.Label.StartsWith("Upgrade", StringComparison.Ordinal),
                DecisionType.Shop => ShopItems(o.Label) == chosen,
                DecisionType.Event => o.Label.StartsWith(chosen, StringComparison.Ordinal),
                _ => o.Label == chosen,
            };
            if (match) return o;
        }
        return null;
    }

    /// <summary>A shop label's items without prices, sorted: "card X (78g) + remove Y (100g)  [178g]" -> "card X + remove Y".</summary>
    public static string ShopItems(string label)
    {
        if (label == "Buy nothing") return label;
        string items = Regex.Replace(label, @"\s*\[\d+g\]\s*$", "");
        return string.Join(" + ", items.Split(" + ").Select(p => Regex.Replace(p, @"\s*\(\d+g\)", "").Trim()).OrderBy(p => p, StringComparer.Ordinal));
    }

    // ---- the report ----

    /// <summary>One run's decisions: the advice, the choice made, and how far behind the best option it was (<c>advisor log --run SEED</c>).</summary>
    private static int ReportRun(List<Run> runs, string seed)
    {
        Run? run = runs.FirstOrDefault(r => r.Seed.Equals(seed, StringComparison.OrdinalIgnoreCase));
        if (run == null) { Console.WriteLine($"No run with seed {seed} in {DefaultFolder}."); return 1; }
        string ended = run.End is { } e ? (e.Won ? "won" : $"died, act {e.Act} floor {e.Floor}") : "no end recorded";
        Console.WriteLine($"{run.Seed}: {ended}");
        Console.WriteLine($"{"floor",5} {"hp",7} {"decision",-12} {"advised",-34} {"chosen",-34} {"behind",7}");
        for (int k = 0; k < run.Decisions.Count; k++)
        {
            Decision d = run.Decisions[k];
            if (d.Advice == null || d.Advice.Options.Count < 2) continue;
            string kind = d.Snapshot.Decision;
            if (kind == DecisionType.Shop && k > 0 && run.Decisions[k - 1].Snapshot.Decision == DecisionType.Shop
                && run.Decisions[k - 1].Snapshot.Run.TotalFloor == d.Snapshot.Run.TotalFloor) continue;
            AdviceOption? best = d.Advice.Options.FirstOrDefault(o => o.Label == d.Advice.Best);
            AdviceOption? pick = d.Chosen == null ? null : MatchOption(d.Advice, kind, d.Chosen);
            string chosen = d.Chosen == null ? "?" : pick?.Display ?? d.Chosen;
            string behind = pick == null ? "" : pick.Label == d.Advice.Best ? "best" : pick.AboutEqualToBest ? "tied" : $"{pick.PointsVsBest:F1}";
            RunInfo r = d.Snapshot.Run;
            Console.WriteLine($"{r.TotalFloor,5} {r.CurrentHp + "/" + r.MaxHp,7} {kind,-12} {Cut(best?.Display ?? d.Advice.Best),-34} {Cut(chosen),-34} {behind,7}");
        }
        return 0;

        static string Cut(string s) => s.Length <= 34 ? s : s[..33] + "~";
    }

    public static int Report(string[] args)
    {
        Sweep(WatchCommand.DefaultSnapshotFolder, WatchCommand.DefaultAdviceFolder);
        var runs = Load();
        if (runs.Count == 0)
        {
            Console.WriteLine($"No runs in {DefaultFolder} yet. The archive fills while 'advisor watch' runs.");
            return 0;
        }
        int runArg = Array.IndexOf(args, "--run");
        if (runArg >= 0 && runArg + 1 < args.Length) return ReportRun(runs, args[runArg + 1]);
        var followed = new Dictionary<string, (int Best, int Tie, int Other, int Unknown)>();
        var predictions = new List<(double Predicted, bool Survived)>();
        Console.WriteLine($"{runs.Count} runs archived in {DefaultFolder}");
        Console.WriteLine();
        Console.WriteLine($"{"started",-17} {"seed",-13} {"char",-9} {"reached",-8} {"ended",-24} {"advised",7} {"followed",9}");
        Run latest = runs.OrderBy(r => r.Last).Last();
        foreach (Run run in runs)
        {
            int advised = 0, same = 0;
            for (int k = 0; k < run.Decisions.Count; k++)
            {
                Decision d = run.Decisions[k];
                if (d.Advice == null || d.Advice.Options.Count < 2) continue;
                string kind = d.Snapshot.Decision;
                // A shop visit can take several snapshots; it is counted once, at its first.
                if (kind == DecisionType.Shop && k > 0 && run.Decisions[k - 1].Snapshot.Decision == DecisionType.Shop
                    && run.Decisions[k - 1].Snapshot.Run.TotalFloor == d.Snapshot.Run.TotalFloor) continue;
                var counts = followed.GetValueOrDefault(kind);
                if (d.Chosen == null) { followed[kind] = counts with { Unknown = counts.Unknown + 1 }; continue; }
                AdviceOption? pick = MatchOption(d.Advice, kind, d.Chosen);
                advised++;
                if (pick == null) { followed[kind] = counts with { Other = counts.Other + 1 }; }
                else if (pick.Label == d.Advice.Best) { followed[kind] = counts with { Best = counts.Best + 1 }; same++; }
                else if (pick.AboutEqualToBest) { followed[kind] = counts with { Tie = counts.Tie + 1 }; same++; }
                else followed[kind] = counts with { Other = counts.Other + 1 };

                // The advisor's predicted chance of surviving this act for the option taken, against what happened.
                bool? survived = ActSurvived(run, d.Snapshot.Run.Act, run == latest);
                if (pick != null && survived is bool s) predictions.Add((pick.SurvivalPct / 100, s));
            }
            string ended = run.End is { } e ? (e.Won ? "won" : $"died, act {e.Act} floor {e.Floor}") : run == latest ? "in progress" : "stopped (no death recorded)";
            var first = run.Decisions[0].Snapshot.Run;
            Console.WriteLine($"{run.Started.LocalDateTime:yyyy-MM-dd HH:mm} {run.Seed,-13} {first.Character,-9} {"act " + run.MaxAct,-8} {ended,-24} {advised,7} {(advised == 0 ? "-" : $"{100.0 * same / advised:F0}%"),9}");
        }

        Console.WriteLine();
        Console.WriteLine("Choices against the advice (\"tied\" = an option the advice called too close to call with the best):");
        foreach (var (kind, c) in followed.OrderBy(k => k.Key))
        {
            int known = c.Best + c.Tie + c.Other;
            Console.WriteLine($"  {kind,-12} best {c.Best,3}  tied {c.Tie,3}  other {c.Other,3}  (followed {(known == 0 ? "-" : $"{100.0 * (c.Best + c.Tie) / known:F0}%")}{(c.Unknown > 0 ? $"; {c.Unknown} choice(s) couldn't be worked out" : "")})");
        }

        Console.WriteLine();
        if (predictions.Count == 0)
            Console.WriteLine("No act outcomes to check predictions against yet (an act counts once the run reaches the next act or its death is recorded).");
        else
        {
            Console.WriteLine($"Predicted chance of surviving the act (for the option taken) against what happened, {predictions.Count} decisions:");
            if (!runs.Any(r => r.End is { Won: false }))
                Console.WriteLine("  (No deaths recorded yet: the mod writes them from its update of 2026-09-26 on, so until a run dies only survived acts count and this is one-sided.)");
            foreach (var (lo, hi) in new[] { (0.0, 0.7), (0.7, 0.85), (0.85, 0.95), (0.95, 0.99), (0.99, 1.01) })
            {
                var bin = predictions.Where(p => p.Predicted >= lo && p.Predicted < hi).ToList();
                if (bin.Count == 0) continue;
                Console.WriteLine($"  predicted {100 * lo,3:F0}-{Math.Min(100, 100 * hi),3:F0}%: {bin.Count,4} decisions, predicted {100 * bin.Average(p => p.Predicted),5:F1}%, survived {100.0 * bin.Count(p => p.Survived) / bin.Count,5:F1}%");
            }
            double brier = predictions.Average(p => Math.Pow(p.Predicted - (p.Survived ? 1 : 0), 2));
            Console.WriteLine($"  Brier score {brier:F3} (0 is perfect; always guessing the average would score {predictions.Average(p => p.Survived ? 1.0 : 0.0) * (1 - predictions.Average(p => p.Survived ? 1.0 : 0.0)):F3}). Decisions in one act share its outcome, so this needs many runs to mean much.");
        }
        return 0;
    }

    /// <summary>Whether the run got through the act: true once it reached a later act, false if it died in it, null if unknown (stopped, or still playing).</summary>
    public static bool? ActSurvived(Run run, int act, bool isLatest)
    {
        if (run.MaxAct > act) return true;
        if (run.End is { } e && !e.Won && e.Act == act) return false;
        if (run.End is { Won: true }) return true;
        return null;
    }
}
