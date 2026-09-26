namespace SpireMonteCarlo.Sim;

/// <summary>
/// Explains the recommendation in plain language by what the table can't show: where the gap between the best option and the
/// runner-up comes from (surviving this act, the deck and HP carried into the rest of the run, the cards' worth later), and what
/// drives the biggest part (which kind of fight the deaths move in, how much stronger the end-of-act deck tests).
/// </summary>
public static class Explainer
{
    public static IReadOnlyList<string> Explain(AdviceReport report)
    {
        var lines = new List<string>();
        if (report.OnlyOption || report.Options.Count < 2) return lines;
        OptionReport best = report.Options[0], next = report.Options[1];
        var ties = report.Options.Skip(1).Where(o => o.AboutEqualToBest).ToList();

        var parts = Split(best, next);
        string split = string.Join(", ", parts.Where(p => Math.Abs(p.Points) >= 0.3).Select(p => $"{p.Points:+0.0;-0.0} {p.What}"));
        double gap = -next.PointsVsBest;
        if (ties.Count > 0)
            lines.Add($"{best.Label} and {string.Join(" / ", ties.Select(t => t.Label))} are too close to call ({gap:F1} points apart{(split.Length > 0 ? $": {split}" : "")}).");
        else
            lines.Add($"{best.Label} beats {next.Label} by {gap:F1} points{(split.Length > 0 ? $": {split}" : "")}.");

        // What drives the biggest part, from numbers the table doesn't show.
        var main = parts.OrderByDescending(p => Math.Abs(p.Points)).First();
        if (Math.Abs(main.Points) >= 0.3)
        {
            string? driver = main.Kind switch
            {
                Part.ThisAct => DeathDriver(best, next),
                Part.RestOfRun => DeckDriver(best, next),
                _ => null,
            };
            if (driver != null) lines.Add(driver);
        }
        return lines;
    }

    private enum Part { ThisAct, RestOfRun, Later }

    /// <summary>
    /// The points gap split three ways. An option's value is (chance to survive the act) x (average rest-of-run value when it does, plus
    /// the cards' later worth), so the gap is: the survival difference at the runner-up's rest-of-run value, the rest-of-run difference
    /// at the best option's survival, and the later-worth difference at the best option's survival.
    /// </summary>
    private static List<(Part Kind, double Points, string What)> Split(OptionReport best, OptionReport next)
    {
        double thisAct = 100 * (best.SurvivalRate - next.SurvivalRate) * (next.MeanFutureIfSurvived + next.LongTermPoints / 100);
        double restOfRun = 100 * best.SurvivalRate * (best.MeanFutureIfSurvived - next.MeanFutureIfSurvived);
        double later = best.SurvivalRate * (best.LongTermPoints - next.LongTermPoints);
        return new()
        {
            (Part.ThisAct, thisAct, $"from surviving this act ({Pct(best.SurvivalRate)} vs {Pct(next.SurvivalRate)})"),
            (Part.RestOfRun, restOfRun, "from the deck and HP it leaves for the rest of the run"),
            (Part.Later, later, "from worth later in the run"),
        };
    }

    /// <summary>Which kind of fight the deaths move in, and by how much.</summary>
    private static string? DeathDriver(OptionReport best, OptionReport next)
    {
        var rooms = new[] { ("boss fights", best.BossDeathRate, next.BossDeathRate, best.BossHpLost, next.BossHpLost),
                            ("elites", best.EliteDeathRate, next.EliteDeathRate, best.EliteHpLost, next.EliteHpLost),
                            ("normal fights", best.NormalDeathRate, next.NormalDeathRate, best.NormalHpLost, next.NormalHpLost) };
        var (room, b, n, bHp, nHp) = rooms.OrderByDescending(r => Math.Abs(r.Item2 - r.Item3)).First();
        if (Math.Abs(b - n) < 0.01) return null;
        string hp = Math.Abs(bHp - nHp) >= 1 ? $", and those fights cost about {Math.Abs(bHp - nHp):F0} HP {(bHp < nHp ? "less" : "more")} each" : "";
        return $"Mostly {(b < n ? "fewer" : "more")} deaths to {room} ({Pct(b)} vs {Pct(n)} of futures){hp}.";
    }

    /// <summary>How the end-of-act deck and HP compare.</summary>
    private static string? DeckDriver(OptionReport best, OptionReport next)
    {
        var bits = new List<string>();
        if (!double.IsNaN(best.ProbeHpLost) && !double.IsNaN(next.ProbeHpLost) && Math.Abs(best.ProbeHpLost - next.ProbeHpLost) >= 0.5)
            bits.Add($"its end-of-act deck loses {Math.Abs(best.ProbeHpLost - next.ProbeHpLost):F1} HP {(best.ProbeHpLost < next.ProbeHpLost ? "less" : "more")} per test fight against Act 2 elites and a boss");
        double hp = best.MeanHpEndIfSurvived - next.MeanHpEndIfSurvived;
        if (Math.Abs(hp) >= 2) bits.Add($"it ends the act with about {Math.Abs(hp):F0} {(hp > 0 ? "more" : "less")} HP");
        return bits.Count == 0 ? null : $"Mostly because {string.Join(", and ", bits)}.";
    }

    private static string Pct(double rate) => $"{100 * rate:F0}%";
}
