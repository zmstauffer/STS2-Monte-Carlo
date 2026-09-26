using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;

namespace SpireMonteCarlo.Advisor;

/// <summary>Turns an advice report into what a person reads (console text) and what a program reads (<see cref="AdviceResult"/>).</summary>
public static class AdviceFormatter
{
    /// <summary>Options the simulations can't tell apart from the best one (the best excluded).</summary>
    private static List<OptionReport> Ties(AdviceReport report) => report.Options.Skip(1).Where(o => o.AboutEqualToBest).ToList();

    private static string Points(double points) => $"{points:F1}";

    /// <summary>
    /// A label as a person reads it: game ids as names ("GHOST_SEED" -> "Ghost Seed") and a shop bundle as its items and total price
    /// ("relic GHOST_SEED (177g) + potion FIRE_POTION (49g)  [226g]" -> "Ghost Seed + Fire Potion (226g)"). The JSON keeps the raw labels.
    /// </summary>
    public static string Readable(string label)
    {
        string text = label;
        Match total = Regex.Match(text, @"\[(\d+g)\]\s*$");
        if (total.Success) text = Regex.Replace(text[..total.Index], @"\s*\(\d+g\)", "").Trim() + $" ({total.Groups[1].Value})";
        text = Regex.Replace(text, @"\b(relic|potion|card) (?=[A-Z])", "");
        return Regex.Replace(text, @"\b[A-Z][A-Z0-9]*(_[A-Z0-9]+)+\b|\b[A-Z]{3,}\b", m => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(m.Value.ToLowerInvariant().Replace('_', ' ')));
    }

    /// <summary>The one-line recommendation: the best option and how sure the simulations are about it.</summary>
    public static string Suggestion(AdviceReport report)
    {
        OptionReport best = report.Options[0];
        List<OptionReport> ties = Ties(report);
        if (report.Options.Count == 1) return best.Label;
        if (ties.Count == report.Options.Count - 1)
            return $"No real difference: every option is within the noise of the others (best by a hair: {Readable(best.Label)}). Pick whichever you prefer.";
        if (ties.Count > 0)
            return $"{Readable(best.Label)}, or {string.Join(" / ", ties.Select(t => Readable(t.Label)))}: about equal (too close to call); the rest are clearly worse.";
        OptionReport runnerUp = report.Options[1];
        return $"{Readable(best.Label)}: clearly best, {Points(-runnerUp.PointsVsBest)} points ahead of {Readable(runnerUp.Label)}.";
    }

    public static string RenderText(string source, RunSnapshot snapshot, AdviceReport report, int rollouts, double seconds)
    {
        var sb = new StringBuilder();
        if (!string.Equals(snapshot.Run.Character, "ironclad", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"Note: the simulator has only been built and checked for Ironclad; results for {snapshot.Run.Character} are rough.");
        sb.AppendLine(source);
        sb.AppendLine($"{snapshot.Run.Character} A{snapshot.Run.Ascension}, act {snapshot.Run.Act} floor {snapshot.Run.TotalFloor}, HP {snapshot.Run.CurrentHp}/{snapshot.Run.MaxHp}, deck {snapshot.Deck.Count} cards, {snapshot.Run.Gold} gold");
        bool test = report.Options.Any(o => !double.IsNaN(o.ProbeHpLost));
        sb.AppendLine($"{rollouts} simulated futures per option ({seconds:F1}s), to the end of act {snapshot.Run.Act}{(test ? ", then a deck test (3 Act 2 elites and a boss, each from full HP)" : "")}.");
        sb.AppendLine(report.ExactPlan ? "Using this run's actual upcoming encounters." : "This snapshot has no encounter plan, so upcoming fights are sampled (less precise).");
        sb.AppendLine();
        sb.AppendLine($"Suggestion: {Suggestion(report)}");
        sb.AppendLine();

        int width = Math.Clamp(report.Options.Max(o => Readable(o.Label).Length), 12, 90);
        sb.AppendLine($"  {"option".PadRight(width)}  {"vs best",-14} {"survive act",11} {"HP left*",9} {(test ? "deck test**" : ""),13}");
        foreach (OptionReport o in report.Options)
        {
            string verdict = o == report.Options[0] ? "best"
                : o.AboutEqualToBest ? $"~ equal ({Points(o.PointsVsBest)})"
                : $"{Points(o.PointsVsBest)} +/-{Points(2 * o.PointsVsBestSe)}";
            string hp = o.SurvivalRate > 0 ? $"{o.MeanHpEndIfSurvived:F0}" : "-";
            string deckTest = double.IsNaN(o.ProbeHpLost) ? "" : $"{o.ProbeHpLost:F0} HP/fight";
            sb.AppendLine($"  {Readable(o.Label).PadRight(width)}  {verdict,-14} {100 * o.SurvivalRate,10:F1}% {hp,9} {deckTest,13}");
        }
        sb.AppendLine();
        sb.AppendLine("  vs best: points behind the best option (10 points is worth about 10% more chance of surviving the act; HP left and the");
        sb.AppendLine("  deck test count too); +/- is the noise; \"~ equal\" means within the noise or under 1 point, too close to call.");
        sb.AppendLine("  * HP at the end of the act, in the futures that survive it.");
        if (test) sb.AppendLine("  ** HP the end-of-act deck loses per test fight; lower means a stronger deck for what comes next.");
        sb.AppendLine();
        sb.AppendLine("Why:");
        foreach (string line in Explainer.Explain(report)) sb.AppendLine($"  - {Readable(line)}");
        OptionReport baseline = report.Options.First(o => o.IsBaseline);
        if (baseline.Killers.Count > 0)
            sb.AppendLine($"  - Where runs die with \"{report.BaselineLabel}\": {string.Join(", ", baseline.Killers.Select(k => $"{Readable(k.Encounter)} x{k.Count}"))}");
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
        BestIsClear = report.Options.Count > 1 && Ties(report).Count == 0,
        Options = report.Options.Select(o => new AdviceOption
        {
            Label = o.Label,
            IsBaseline = o.IsBaseline,
            SurvivalPct = Math.Round(100 * o.SurvivalRate, 1),
            HpLeft = Math.Round(o.MeanHpEnd, 1),
            NextActEliteWinPct = double.IsNaN(o.ProbeWinRate) ? null : Math.Round(100 * o.ProbeWinRate, 1),
            DeckTestHpLost = double.IsNaN(o.ProbeHpLost) ? null : Math.Round(o.ProbeHpLost, 1),
            PointsVsBest = Math.Round(o.PointsVsBest, 1),
            PointsVsBestUncertainty = Math.Round(2 * o.PointsVsBestSe, 1),
            AboutEqualToBest = o.AboutEqualToBest,
            DeltaScore = Math.Round(o.DeltaValue, 3),
            DeltaScoreUncertainty = Math.Round(2 * o.DeltaValueSe, 3),
            Clear = !o.IsBaseline && o.ClearlyDifferentFromSkip,
        }).ToList(),
        Why = Explainer.Explain(report).ToList(),
        Notes = Notes(report).ToList(),
    };
}
