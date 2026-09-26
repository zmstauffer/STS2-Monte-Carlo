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
        if (snapshot.Decision != DecisionType.CardReward)
        {
            Console.Error.WriteLine($"Only card reward decisions are supported so far (this snapshot is '{snapshot.Decision}').");
            return 1;
        }
        if (!string.Equals(snapshot.Run.Character, "ironclad", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"Note: the simulator has only been built and checked for Ironclad; results for {snapshot.Run.Character} are rough.");

        int rollouts = int.Parse(Option(args, "--n") ?? "2000");
        ulong seed = ulong.Parse(Option(args, "--seed") ?? "1");
        var data = new SimData(cache);

        var sw = Stopwatch.StartNew();
        AdviceReport report = AdviceEngine.EvaluateCardReward(data, snapshot, rollouts, seed);
        sw.Stop();

        Console.WriteLine(path);
        Console.WriteLine($"{snapshot.Run.Character} A{snapshot.Run.Ascension}, act {snapshot.Run.Act} floor {snapshot.Run.TotalFloor}, HP {snapshot.Run.CurrentHp}/{snapshot.Run.MaxHp}, deck {snapshot.Deck.Count} cards");
        Console.WriteLine($"{rollouts} simulated futures per option ({sw.Elapsed.TotalSeconds:F1}s), to the end of the act's boss fight.");
        Console.WriteLine(report.ExactPlan ? "Using this run's actual upcoming encounters." : "This snapshot has no encounter plan, so upcoming fights are sampled (less precise).");
        Console.WriteLine();

        OptionReport skip = report.Options.First(o => o.CardId == null);
        Console.WriteLine($"{"option",-22} {"survive",8} {"HP left",8} {"next-act elites",16} {"vs skip (survive)",20} {"vs skip (score)",18}");
        foreach (OptionReport o in report.Options)
        {
            string versus = o.CardId == null ? "" : $"{100 * o.DeltaSurvival,+6:F1} pts +/-{200 * o.DeltaSurvivalSe:F1}";
            string score = o.CardId == null ? "" : $"{o.DeltaValue,+6:F3} +/-{2 * o.DeltaValueSe:F3}{(o.ClearlyDifferentFromSkip ? "" : "  (unclear)")}";
            Console.WriteLine($"{o.Label,-22} {100 * o.SurvivalRate,7:F1}% {o.MeanHpEnd,8:F1} {(double.IsNaN(o.ProbeWinRate) ? "" : $"{100 * o.ProbeWinRate:F0}% won"),16} {versus,20} {score,18}");
        }

        Console.WriteLine();
        OptionReport best = report.Options[0];
        if (best.CardId == null)
            Console.WriteLine($"Suggestion: skip. None of the offered cards beat skipping ({100 * skip.SurvivalRate:F1}% survival, about {skip.MeanHpEnd:F0} HP left).");
        else if (!best.ClearlyDifferentFromSkip)
            Console.WriteLine($"Suggestion: {best.Label} looks best, but it is not clearly better than skipping with this many simulations.");
        else
            Console.WriteLine($"Suggestion: take {best.Label}. It survives the act {100 * best.SurvivalRate:F1}% of the time versus {100 * skip.SurvivalRate:F1}% if you skip, ending with about {best.MeanHpEnd:F0} HP versus {skip.MeanHpEnd:F0}.");

        if (skip.Killers.Count > 0)
            Console.WriteLine($"Where runs die without the card: {string.Join(", ", skip.Killers.Select(k => $"{k.Encounter} x{k.Count}"))}");
        if (report.ApproximateCards > 0)
            Console.WriteLine($"Caution: {report.ApproximateCards} card(s) involved have text the simulator only partly models.");
        if (report.UnmodelledFights > 0)
            Console.WriteLine($"Caution: about {report.UnmodelledFights} fight(s) per run used encounters the simulator doesn't know, counted as free.");
        if (report.UnknownCards.Count > 0)
            Console.WriteLine($"Caution: cards unknown to Codex, treated as blanks: {string.Join(", ", report.UnknownCards)}");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
