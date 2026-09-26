using System.Text;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;

namespace SpireMonteCarlo.Advisor;

/// <summary>Turns an advice report into what a person reads (console text) and what a program reads (<see cref="AdviceResult"/>).</summary>
public static class AdviceFormatter
{
    /// <summary>The one-line recommendation.</summary>
    public static string Suggestion(AdviceReport report)
    {
        OptionReport baseline = report.Options.First(o => o.IsBaseline);
        OptionReport best = report.Options[0];
        if (best.IsBaseline)
            return $"{report.BaselineLabel}. None of the other options beat it ({100 * baseline.SurvivalRate:F1}% survival, about {baseline.MeanHpEnd:F0} HP left).";
        if (!best.ClearlyDifferentFromSkip)
            return $"{best.Label} looks best, but it is not clearly better than \"{report.BaselineLabel}\" with this many simulations.";
        return $"{best.Label}. It survives the act {100 * best.SurvivalRate:F1}% of the time versus {100 * baseline.SurvivalRate:F1}% for \"{report.BaselineLabel}\", ending with about {best.MeanHpEnd:F0} HP versus {baseline.MeanHpEnd:F0}.";
    }

    public static string RenderText(string source, RunSnapshot snapshot, AdviceReport report, int rollouts, double seconds)
    {
        var sb = new StringBuilder();
        if (!string.Equals(snapshot.Run.Character, "ironclad", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"Note: the simulator has only been built and checked for Ironclad; results for {snapshot.Run.Character} are rough.");
        sb.AppendLine(source);
        sb.AppendLine($"{snapshot.Run.Character} A{snapshot.Run.Ascension}, act {snapshot.Run.Act} floor {snapshot.Run.TotalFloor}, HP {snapshot.Run.CurrentHp}/{snapshot.Run.MaxHp}, deck {snapshot.Deck.Count} cards, {snapshot.Run.Gold} gold");
        sb.AppendLine($"{rollouts} simulated futures per option ({seconds:F1}s), to the end of the act's boss fight.");
        sb.AppendLine(report.ExactPlan ? "Using this run's actual upcoming encounters." : "This snapshot has no encounter plan, so upcoming fights are sampled (less precise).");
        sb.AppendLine();

        OptionReport baseline = report.Options.First(o => o.IsBaseline);
        int width = Math.Clamp(report.Options.Max(o => o.Label.Length), 22, 90);
        sb.AppendLine($"{"option".PadRight(width)} {"survive",8} {"HP left",8} {"next-act elites",16} {"vs " + report.BaselineLabel + " (survive)",26} {"vs " + report.BaselineLabel + " (score)",24}");
        foreach (OptionReport o in report.Options)
        {
            string versus = o.IsBaseline ? "" : $"{100 * o.DeltaSurvival,+6:F1} pts +/-{200 * o.DeltaSurvivalSe:F1}";
            string score = o.IsBaseline ? "" : $"{o.DeltaValue,+6:F3} +/-{2 * o.DeltaValueSe:F3}{(o.ClearlyDifferentFromSkip ? "" : "  (unclear)")}";
            string probe = double.IsNaN(o.ProbeWinRate) ? "" : $"{100 * o.ProbeWinRate:F0}% won";
            sb.AppendLine($"{o.Label.PadRight(width)} {100 * o.SurvivalRate,7:F1}% {o.MeanHpEnd,8:F1} {probe,16} {versus,26} {score,24}");
        }

        sb.AppendLine();
        sb.AppendLine($"Suggestion: {Suggestion(report)}");
        sb.AppendLine();
        sb.AppendLine("Why:");
        foreach (string line in Explainer.Explain(report)) sb.AppendLine($"  - {line}");
        sb.AppendLine();
        if (baseline.Killers.Count > 0)
            sb.AppendLine($"Where runs die with \"{report.BaselineLabel}\": {string.Join(", ", baseline.Killers.Select(k => $"{k.Encounter} x{k.Count}"))}");
        foreach (string note in Notes(report)) sb.AppendLine($"Note: {note}");
        return sb.ToString();
    }

    /// <summary>Everything the reader should be warned about, in one list.</summary>
    public static IReadOnlyList<string> Notes(AdviceReport report)
    {
        var notes = new List<string>(report.Notes);
        if (report.ApproximateCards > 0) notes.Add($"{report.ApproximateCards} card(s) involved have text the simulator only partly models.");
        if (report.UnmodelledFights > 0) notes.Add($"about {report.UnmodelledFights} fight(s) per run used encounters the simulator doesn't know, counted as free.");
        if (report.UnknownCards.Count > 0) notes.Add($"cards unknown to Codex, treated as blanks: {string.Join(", ", report.UnknownCards)}");
        return notes;
    }

    public static AdviceResult ToResult(string snapshotFile, RunSnapshot snapshot, AdviceReport report, int rollouts, double seconds) => new()
    {
        SnapshotFile = Path.GetFileName(snapshotFile),
        Decision = snapshot.Decision,
        EventId = snapshot.EventId,
        CreatedAt = DateTimeOffset.Now,
        Rollouts = rollouts,
        Seconds = Math.Round(seconds, 1),
        Baseline = report.BaselineLabel,
        Best = report.Options[0].Label,
        Suggestion = Suggestion(report),
        BestIsClear = !report.Options[0].IsBaseline && report.Options[0].ClearlyDifferentFromSkip,
        Options = report.Options.Select(o => new AdviceOption
        {
            Label = o.Label,
            IsBaseline = o.IsBaseline,
            SurvivalPct = Math.Round(100 * o.SurvivalRate, 1),
            HpLeft = Math.Round(o.MeanHpEnd, 1),
            NextActEliteWinPct = double.IsNaN(o.ProbeWinRate) ? null : Math.Round(100 * o.ProbeWinRate, 1),
            DeltaScore = Math.Round(o.DeltaValue, 3),
            DeltaScoreUncertainty = Math.Round(2 * o.DeltaValueSe, 3),
            Clear = !o.IsBaseline && o.ClearlyDifferentFromSkip,
        }).ToList(),
        Why = Explainer.Explain(report).ToList(),
        Notes = Notes(report).ToList(),
    };
}
