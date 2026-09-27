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
    public static string Suggestion(AdviceReport report, Func<string, string>? name = null)
    {
        Func<string, string> Readable = name ?? AdviceFormatter.Readable;
        OptionReport best = report.Options[0];
        List<OptionReport> ties = Ties(report);
        if (report.Options.Count == 1) return $"Only one option: {Readable(best.Label)}.";
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
        if (report.OnlyOption)
        {
            sb.AppendLine($"Suggestion: {Suggestion(report)}");
            return sb.ToString();
        }
        bool test = report.Options.Any(o => !double.IsNaN(o.ProbeHpLost));
        sb.AppendLine($"{rollouts} simulated futures per option ({seconds:F1}s), to the end of act {snapshot.Run.Act}{(test ? ", then a deck test (3 Act 2 elites and a boss, each from full HP)" : "")}.");
        sb.AppendLine(report.ExactPlan ? "Using this run's actual upcoming encounters." : "This snapshot has no encounter plan, so upcoming fights are sampled (less precise).");
        sb.AppendLine();
        sb.AppendLine($"Suggestion: {Suggestion(report)}");
        sb.AppendLine();

        int width = Math.Clamp(report.Options.Max(o => Readable(o.Label).Length), 12, 90);
        bool later = report.Options.Any(o => Math.Abs(o.LongTermPoints) >= 0.05);
        sb.AppendLine($"  {"option".PadRight(width)}  {"vs best",-14} {"survive act",11} {"HP left*",9} {(test ? "deck test**" : ""),13} {(later ? "later***" : "")}");
        foreach (OptionReport o in report.Options)
        {
            string verdict = o == report.Options[0] ? "best"
                : o.AboutEqualToBest ? $"~ equal ({Points(o.PointsVsBest)})"
                : $"{Points(o.PointsVsBest)} +/-{Points(2 * o.PointsVsBestSe)}";
            string hp = o.SurvivalRate > 0 ? $"{o.MeanHpEndIfSurvived:F0}" : "-";
            string deckTest = double.IsNaN(o.ProbeHpLost) ? "" : $"{o.ProbeHpLost:F0} HP/fight";
            string laterText = later ? $"{o.LongTermPoints,+7:+0.0;-0.0;0.0}" : "";
            sb.AppendLine($"  {Readable(o.Label).PadRight(width)}  {verdict,-14} {100 * o.SurvivalRate,10:F1}% {hp,9} {deckTest,13} {laterText}");
        }
        sb.AppendLine();
        sb.AppendLine("  vs best: percentage points of the chance to win the run, behind the best option. The rest of this act is simulated; what");
        sb.AppendLine("  follows is predicted from the end-of-act deck test and HP; +/- is the noise; \"~ equal\" means too close to call.");
        sb.AppendLine("  * HP at the end of the act, in the futures that survive it.");
        if (test) sb.AppendLine("  ** HP the end-of-act deck loses per test fight; lower means a stronger deck for what comes next.");
        if (later) sb.AppendLine("  *** points from worth after this act (new cards: real players' ratings; upgrades: HP saved in test fights), included in \"vs best\".");
        sb.AppendLine();
        sb.AppendLine("Why:");
        foreach (string line in Explainer.Explain(report)) sb.AppendLine($"  - {Readable(line)}");
        OptionReport top = report.Options[0];
        if (top.Killers.Count > 0 && top.SurvivalRate < 0.97)
            sb.AppendLine($"  - Where runs die with {Readable(top.Label)}: {string.Join(", ", top.Killers.Select(k => $"{Readable(k.Encounter)} x{k.Count}"))}");
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

    /// <summary>
    /// How a player would name each option on screen: readable card names, and map nodes by room and position among the choices
    /// ("Elite (left)") rather than map columns.
    /// </summary>
    public static Dictionary<string, string> DisplayLabels(RunSnapshot snapshot, AdviceReport report)
    {
        var names = new Dictionary<string, string>();
        var nodes = report.Options.Select(o => (o.Label, Match: Regex.Match(o.Label, @"^(\w+) \(column (\d+), row \d+\)$"))).ToList();
        var columns = nodes.Where(n => n.Match.Success).Select(n => int.Parse(n.Match.Groups[2].Value)).Distinct().OrderBy(c => c).ToList();
        foreach ((string label, Match m) in nodes)
            names[label] = snapshot.Decision == DecisionType.Map && m.Success
                ? RoomName(m.Groups[1].Value) + Position(columns.IndexOf(int.Parse(m.Groups[2].Value)), columns.Count)
                : Readable(label);
        return names;
    }

    private static string RoomName(string type) => type switch
    {
        "Unknown" => "? room",
        "RestSite" => "Rest site",
        _ => type,
    };

    private static string Position(int index, int count) => count switch
    {
        <= 1 => "",
        2 => index == 0 ? " (left)" : " (right)",
        3 => new[] { " (left)", " (middle)", " (right)" }[index],
        _ => $" ({index + 1}{(index == 0 ? "st" : index == 1 ? "nd" : index == 2 ? "rd" : "th")} from left)",
    };

    public static AdviceResult ToResult(string snapshotFile, RunSnapshot snapshot, AdviceReport report, int rollouts, double seconds)
    {
        Dictionary<string, string> names = DisplayLabels(snapshot, report);
        return new()
    {
        SnapshotFile = Path.GetFileName(snapshotFile),
        Decision = snapshot.Decision,
        EventId = snapshot.EventId,
        CreatedAt = DateTimeOffset.Now,
        Rollouts = rollouts,
        Seconds = Math.Round(seconds, 1),
        Baseline = report.BaselineLabel,
        Best = report.Options[0].Label,
        Suggestion = Suggestion(report, label => names[label]),
        BestIsClear = report.Options.Count > 1 && Ties(report).Count == 0,
        Options = report.Options.Select(o => new AdviceOption
        {
            Label = o.Label,
            Display = names[o.Label],
            IsBaseline = o.IsBaseline,
            SurvivalPct = Math.Round(100 * o.SurvivalRate, 1),
            HpLeft = Math.Round(o.MeanHpEnd, 1),
            NextActEliteWinPct = double.IsNaN(o.ProbeWinRate) ? null : Math.Round(100 * o.ProbeWinRate, 1),
            DeckTestHpLost = double.IsNaN(o.ProbeHpLost) ? null : Math.Round(o.ProbeHpLost, 1),
            PointsVsBest = Math.Round(o.PointsVsBest, 1),
            PointsVsBestUncertainty = Math.Round(2 * o.PointsVsBestSe, 1),
            AboutEqualToBest = o.AboutEqualToBest,
            LongTermPoints = Math.Round(o.LongTermPoints, 1),
            DeltaScore = Math.Round(o.DeltaValue, 3),
            DeltaScoreUncertainty = Math.Round(2 * o.DeltaValueSe, 3),
            Clear = !o.IsBaseline && o.ClearlyDifferentFromSkip,
        }).ToList(),
        Why = Explainer.Explain(report).ToList(),
        Notes = Notes(report).ToList(),
    };
    }
}
