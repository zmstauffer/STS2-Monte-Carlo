namespace SpireMonteCarlo.Sim;

/// <summary>
/// Puts what the simulations found into plain language: where the recommended option wins (or loses) against the baseline,
/// in terms a player recognises: how often the act is survived, which kind of fight is deadlier, how much HP fights cost,
/// and how the end-of-act deck does in its test fights against the next act.
/// </summary>
public static class Explainer
{
    private const double RateStep = 0.02, HpStep = 2.0;

    public static IReadOnlyList<string> Explain(AdviceReport report)
    {
        var lines = new List<string>();
        if (report.OnlyOption) return lines;
        OptionReport baseline = report.Options.First(o => o.IsBaseline);
        OptionReport best = report.Options[0];

        if (best.IsBaseline)
        {
            lines.Add($"None of the other options beat \"{baseline.Label}\": {Pct(baseline.SurvivalRate)} of futures survive the act with it.");
            OptionReport? runnerUp = report.Options.Skip(1).FirstOrDefault();
            if (runnerUp != null)
                lines.Add($"The closest is {runnerUp.Label}: {Difference(runnerUp, baseline)}.");
            return lines;
        }

        string difference = Difference(best, baseline);
        lines.Add(best.ClearlyDifferentFromSkip
            ? $"{best.Label} beats \"{baseline.Label}\": {difference}."
            : $"{best.Label} looks best, but the difference from \"{baseline.Label}\" is within the noise of this many simulations ({difference}).");

        // Trade-offs: an option can be worse on one measure and better on another.
        var costs = Costs(best, baseline).ToList();
        if (costs.Count > 0) lines.Add($"The trade-off: it {string.Join(" and ", costs)}.");

        // Options that are clearly worse than the baseline are the traps worth naming.
        foreach (OptionReport o in report.Options.Skip(1).Where(o => o.DeltaValue < 0 && o.ClearlyDifferentFromSkip).OrderBy(o => o.DeltaValue).Take(2))
            lines.Add($"Avoid {o.Label}: compared with \"{baseline.Label}\", {Difference(o, baseline)}.");
        return lines;
    }

    /// <summary>Everything that differs noticeably between an option and the baseline, best news first.</summary>
    private static string Difference(OptionReport o, OptionReport baseline)
    {
        var parts = new List<string>();
        double survival = o.SurvivalRate - baseline.SurvivalRate;
        if (Math.Abs(survival) >= RateStep)
            parts.Add($"the act is survived {Pct(o.SurvivalRate)} of the time versus {Pct(baseline.SurvivalRate)}");

        AddDeaths(parts, "boss", o.BossDeathRate, baseline.BossDeathRate);
        AddDeaths(parts, "elite", o.EliteDeathRate, baseline.EliteDeathRate);
        AddDeaths(parts, "normal", o.NormalDeathRate, baseline.NormalDeathRate);
        AddHp(parts, "elite fights", o.EliteHpLost, baseline.EliteHpLost);
        AddHp(parts, "boss fights", o.BossHpLost, baseline.BossHpLost);

        double hpEnd = o.MeanHpEndIfSurvived - baseline.MeanHpEndIfSurvived;
        if (Math.Abs(hpEnd) >= 3 && o.SurvivalRate > 0 && baseline.SurvivalRate > 0)
            parts.Add($"the act ends with about {Math.Abs(hpEnd):F0} {(hpEnd > 0 ? "more" : "less")} HP when it is survived");

        if (!double.IsNaN(o.ProbeHpLost) && !double.IsNaN(baseline.ProbeHpLost) && Math.Abs(o.ProbeHpLost - baseline.ProbeHpLost) >= 1)
            parts.Add($"the end-of-act deck loses about {Math.Abs(o.ProbeHpLost - baseline.ProbeHpLost):F0} HP {(o.ProbeHpLost < baseline.ProbeHpLost ? "less" : "more")} per next-act test fight ({o.ProbeHpLost:F0} versus {baseline.ProbeHpLost:F0})");

        return parts.Count == 0 ? "the differences are small" : string.Join("; ", parts);
    }

    /// <summary>The ways the option is worse than the baseline, for the trade-off sentence (only when it is better overall).</summary>
    private static IEnumerable<string> Costs(OptionReport o, OptionReport baseline)
    {
        if (baseline.SurvivalRate - o.SurvivalRate >= RateStep)
            yield return $"survives the act slightly less often ({Pct(o.SurvivalRate)} versus {Pct(baseline.SurvivalRate)})";
        if (o.BossDeathRate - baseline.BossDeathRate >= RateStep) yield return $"loses more often to bosses ({Pct(o.BossDeathRate)} versus {Pct(baseline.BossDeathRate)})";
        if (o.EliteDeathRate - baseline.EliteDeathRate >= RateStep) yield return $"loses more often to elites ({Pct(o.EliteDeathRate)} versus {Pct(baseline.EliteDeathRate)})";
        if (o.EliteHpLost - baseline.EliteHpLost >= HpStep) yield return $"costs about {o.EliteHpLost - baseline.EliteHpLost:F0} more HP in each elite fight";
        if (!double.IsNaN(o.ProbeHpLost) && o.ProbeHpLost - baseline.ProbeHpLost >= 1)
            yield return $"leaves a weaker deck for the next act (it loses {o.ProbeHpLost:F0} HP per test fight versus {baseline.ProbeHpLost:F0})";
    }

    private static void AddDeaths(List<string> parts, string kind, double option, double baseline)
    {
        if (Math.Abs(option - baseline) < RateStep) return;
        parts.Add(option < baseline
            ? $"deaths to {kind} fights fall from {Pct(baseline)} to {Pct(option)}"
            : $"deaths to {kind} fights rise from {Pct(baseline)} to {Pct(option)}");
    }

    private static void AddHp(List<string> parts, string what, double option, double baseline)
    {
        double d = option - baseline;
        if (Math.Abs(d) < HpStep) return;
        parts.Add($"{what} cost about {Math.Abs(d):F0} HP {(d < 0 ? "less" : "more")} on average ({option:F0} versus {baseline:F0})");
    }

    private static string Pct(double rate) => $"{100 * rate:F0}%";
}
