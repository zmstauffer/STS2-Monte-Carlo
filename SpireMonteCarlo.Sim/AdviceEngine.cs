using SpireMonteCarlo.Contracts;

namespace SpireMonteCarlo.Sim;

public sealed class OptionReport
{
    public required string Label { get; init; }
    /// <summary>Null for "skip".</summary>
    public string? CardId { get; init; }
    public int Rollouts { get; init; }
    public double SurvivalRate { get; init; }
    /// <summary>Average HP at the end of the act, counting a death as 0.</summary>
    public double MeanHpEnd { get; init; }
    public double MeanHpEndIfSurvived { get; init; }
    public double MeanFightsWon { get; init; }

    /// <summary>Share of next-act elites the end-of-act deck beats, over the runs that reach the end of the act; NaN when there is no probe (Act 3).</summary>
    public double ProbeWinRate { get; init; } = double.NaN;
    /// <summary>The single number options are ranked by: survival first, with HP left as a tiebreaker.</summary>
    public double Value { get; init; }

    public double DeltaSurvival { get; init; }
    public double DeltaSurvivalSe { get; init; }
    public double DeltaHp { get; init; }
    public double DeltaHpSe { get; init; }
    public double DeltaValue { get; init; }
    public double DeltaValueSe { get; init; }

    /// <summary>Which encounters ended the runs that died, most common first.</summary>
    public IReadOnlyList<(string Encounter, int Count)> Killers { get; init; } = Array.Empty<(string, int)>();

    /// <summary>The difference from skipping is bigger than about two standard errors.</summary>
    public bool ClearlyDifferentFromSkip => DeltaValueSe > 0 && Math.Abs(DeltaValue) > 2 * DeltaValueSe;
}

public sealed class AdviceReport
{
    public required string Decision { get; init; }
    public required IReadOnlyList<OptionReport> Options { get; init; }
    public int Rollouts { get; init; }
    public bool ExactPlan { get; init; }
    public int ApproximateCards { get; init; }
    public int UnmodelledFights { get; init; }
    public IReadOnlyList<string> UnknownCards { get; init; } = Array.Empty<string>();
}

public static class AdviceEngine
{
    /// <summary>
    /// Value of one rollout: surviving the act is what counts, then HP left, then how the end-of-act deck does against a few
    /// next-act elites (which is where scaling cards pay off, and nearly everything survives Act 1).
    /// </summary>
    public static double ValueOf(RolloutResult r)
    {
        if (!r.Survived) return 0.0;
        double value = 1.0 + 0.5 * r.HpEnd / r.MaxHp;
        return r.ProbeFights > 0 ? value + 0.5 * r.ProbeWins / r.ProbeFights : value;
    }

    /// <summary>Compares taking each offered card against skipping, over many simulated futures of the act.</summary>
    public static AdviceReport EvaluateCardReward(SimData data, RunSnapshot snapshot, int rollouts, ulong seed)
    {
        var rollout = new ActRollout(data, snapshot);
        List<CardDef> baseDeck = snapshot.Deck.Select(c => data.Cards.Get(c.Id, c.Upgraded)).ToList();

        var options = new List<(string Label, string? Card, List<CardDef> Deck)> { ("Skip", null, baseDeck) };
        foreach (CardSnapshot offered in snapshot.Offer.Cards)
            options.Add((offered.Id, offered.Id, baseDeck.Append(data.Cards.Get(offered.Id, offered.Upgraded)).ToList()));

        var results = new RolloutResult[options.Count][];
        for (int j = 0; j < options.Count; j++) results[j] = new RolloutResult[rollouts];

        Parallel.For(0, rollouts, i =>
        {
            ulong rolloutSeed = SimRng.Mix(seed, (ulong)i);
            for (int j = 0; j < options.Count; j++)
                results[j][i] = rollout.Run(options[j].Deck, rolloutSeed);
        });

        double[] skipValues = results[0].Select(ValueOf).ToArray();
        double[] skipSurvived = results[0].Select(r => r.Survived ? 1.0 : 0.0).ToArray();
        double[] skipHp = results[0].Select(r => (double)r.HpEnd).ToArray();

        var reports = new List<OptionReport>();
        for (int j = 0; j < options.Count; j++)
        {
            RolloutResult[] rs = results[j];
            double[] values = rs.Select(ValueOf).ToArray();
            double[] survived = rs.Select(r => r.Survived ? 1.0 : 0.0).ToArray();
            double[] hp = rs.Select(r => (double)r.HpEnd).ToArray();
            (double dSurv, double dSurvSe) = PairedDifference(survived, skipSurvived);
            (double dHp, double dHpSe) = PairedDifference(hp, skipHp);
            (double dValue, double dValueSe) = PairedDifference(values, skipValues);
            int survivors = rs.Count(r => r.Survived);

            reports.Add(new OptionReport
            {
                Label = options[j].Label,
                CardId = options[j].Card,
                Rollouts = rollouts,
                SurvivalRate = survivors / (double)rollouts,
                MeanHpEnd = hp.Average(),
                MeanHpEndIfSurvived = survivors == 0 ? 0 : rs.Where(r => r.Survived).Average(r => r.HpEnd),
                MeanFightsWon = rs.Average(r => r.FightsWon),
                ProbeWinRate = rs.Where(r => r.Survived && r.ProbeFights > 0).Select(r => (double)r.ProbeWins / r.ProbeFights).DefaultIfEmpty(double.NaN).Average(),
                Value = values.Average(),
                DeltaSurvival = dSurv, DeltaSurvivalSe = dSurvSe,
                DeltaHp = dHp, DeltaHpSe = dHpSe,
                DeltaValue = dValue, DeltaValueSe = dValueSe,
                Killers = rs.Where(r => r.DiedTo != null).GroupBy(r => r.DiedTo!).OrderByDescending(g => g.Count()).Take(3).Select(g => (g.Key, g.Count())).ToList(),
            });
        }

        return new AdviceReport
        {
            Decision = snapshot.Decision,
            Options = reports.OrderByDescending(r => r.Value).ToList(),
            Rollouts = rollouts,
            ExactPlan = rollout.HasExactPlan,
            ApproximateCards = baseDeck.Concat(options.Skip(1).Select(o => o.Deck[^1])).Count(c => c.Approximate),
            UnmodelledFights = results.SelectMany(r => r).Sum(r => r.UnmodelledFights) / options.Count,
            UnknownCards = data.Cards.UnknownIds.ToList(),
        };
    }

    /// <summary>Mean of (a - b) over paired samples and its standard error.</summary>
    private static (double Mean, double StandardError) PairedDifference(double[] a, double[] b)
    {
        int n = a.Length;
        double mean = 0;
        for (int i = 0; i < n; i++) mean += a[i] - b[i];
        mean /= n;
        double variance = 0;
        for (int i = 0; i < n; i++) variance += (a[i] - b[i] - mean) * (a[i] - b[i] - mean);
        variance /= Math.Max(1, n - 1);
        return (mean, Math.Sqrt(variance / n));
    }
}
