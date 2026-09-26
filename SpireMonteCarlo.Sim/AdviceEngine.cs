using SpireMonteCarlo.Contracts;

namespace SpireMonteCarlo.Sim;

public sealed class OptionReport
{
    public required string Label { get; init; }
    /// <summary>The card this option takes; null for "skip" and for options that aren't about a card.</summary>
    public string? CardId { get; init; }

    /// <summary>The option the others are compared against (skip, rest, buy nothing, ...).</summary>
    public bool IsBaseline { get; init; }
    public int Rollouts { get; init; }
    public double SurvivalRate { get; init; }
    /// <summary>Average HP at the end of the act, counting a death as 0.</summary>
    public double MeanHpEnd { get; init; }
    public double MeanHpEndIfSurvived { get; init; }
    public double MeanFightsWon { get; init; }

    /// <summary>Share of the deck-test fights the end-of-act deck wins, over the runs that reach the end of the act; NaN when there is no test (Act 3).</summary>
    public double ProbeWinRate { get; init; } = double.NaN;
    /// <summary>Average real HP the end-of-act deck loses per deck-test fight (a lost fight counts as all of it); NaN without a test.</summary>
    public double ProbeHpLost { get; init; } = double.NaN;
    /// <summary>The single number options are ranked by (see <see cref="AdviceEngine.ValueOf"/>).</summary>
    public double Value { get; init; }

    /// <summary>Value compared with the best option on the same futures (0 for the best itself, negative for the rest) and its standard error.</summary>
    public double DeltaVsBest { get; init; }
    public double DeltaVsBestSe { get; init; }
    /// <summary>What a surviving future is worth on average in this decision, so that value differences read as survival points.</summary>
    public double PointScale { get; init; } = 1;
    /// <summary>
    /// The gap to the best option in points: 100 times the value gap over what a surviving future is worth, so an option that only
    /// changes the chance of surviving the act by 10% is about 10 points; HP left and the deck test move it too.
    /// </summary>
    public double PointsVsBest => 100 * DeltaVsBest / PointScale;
    public double PointsVsBestSe => 100 * DeltaVsBestSe / PointScale;
    /// <summary>
    /// The best option, or one whose gap to it is within about two standard errors (the simulations can't tell them apart) or under
    /// <see cref="AdviceEngine.MeaningfulPoints"/> (a real difference, but too small to matter next to how far the model is from the game).
    /// </summary>
    public bool AboutEqualToBest => DeltaVsBest == 0 || -PointsVsBest <= Math.Max(2 * PointsVsBestSe, AdviceEngine.MeaningfulPoints);

    public double DeltaSurvival { get; init; }
    public double DeltaSurvivalSe { get; init; }
    public double DeltaHp { get; init; }
    public double DeltaHpSe { get; init; }
    public double DeltaValue { get; init; }
    public double DeltaValueSe { get; init; }

    /// <summary>Share of futures that ended in a death in a normal fight, an elite fight, and a boss fight.</summary>
    public double NormalDeathRate { get; init; }
    public double EliteDeathRate { get; init; }
    public double BossDeathRate { get; init; }

    /// <summary>Average HP lost per fight of each kind (real points, before healing), over the fights that were reached.</summary>
    public double NormalHpLost { get; init; }
    public double EliteHpLost { get; init; }
    public double BossHpLost { get; init; }

    /// <summary>Which encounters ended the runs that died, most common first.</summary>
    public IReadOnlyList<(string Encounter, int Count)> Killers { get; init; } = Array.Empty<(string, int)>();

    /// <summary>The difference from skipping is bigger than about two standard errors.</summary>
    public bool ClearlyDifferentFromSkip => DeltaValueSe > 0 && Math.Abs(DeltaValue) > 2 * DeltaValueSe;
}

public sealed class AdviceReport
{
    public required string Decision { get; init; }

    /// <summary>What the first option means, e.g. "Skip" or "Rest": every other option is compared with it.</summary>
    public string BaselineLabel { get; init; } = "";

    /// <summary>Things worth telling the reader: options whose effect the simulator can't model, and the like.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public required IReadOnlyList<OptionReport> Options { get; init; }
    public int Rollouts { get; init; }
    public bool ExactPlan { get; init; }
    public int ApproximateCards { get; init; }
    public int UnmodelledFights { get; init; }
    public IReadOnlyList<string> UnknownCards { get; init; } = Array.Empty<string>();
}

/// <summary>One choice the player could make: a label and the state of the run after making it.</summary>
public sealed record DecisionOption(string Label, string? CardId, RolloutStart Start);

public static class AdviceEngine
{
    /// <summary>How much the deck test counts next to surviving the act (which counts 1).</summary>
    public const double DeckTestWeight = 1.0;

    /// <summary>The smallest gap (in points) reported as a real difference between two options.</summary>
    public const double MeaningfulPoints = 1.0;

    /// <summary>
    /// Value of one rollout: surviving the act counts 1; then HP left at its end (up to 0.5 for full HP); then the deck test (up to
    /// <see cref="DeckTestWeight"/> for a deck that loses no HP in its test fights). The deck test is where scaling cards pay off and what
    /// still tells options apart late in an act, when nearly every future survives it. Reports show value differences times 100 as
    /// "points": one point is worth about one percent more chance of getting through the act.
    /// </summary>
    public static double ValueOf(RolloutResult r)
    {
        if (!r.Survived) return 0.0;
        double value = 1.0 + 0.5 * r.HpEnd / r.MaxHp;
        return r.ProbeFights > 0 ? value + DeckTestWeight * r.DeckStrength : value;
    }

    /// <summary>Compares taking each offered card against skipping, over many simulated futures of the act.</summary>
    public static AdviceReport EvaluateCardReward(SimData data, RunSnapshot snapshot, int rollouts, ulong seed)
    {
        var rollout = new ActRollout(data, snapshot);
        List<CardDef> baseDeck = snapshot.Deck.Select(c => data.Cards.Get(c.Id, c.Upgraded)).ToList();

        var options = new List<DecisionOption> { new("Skip", null, rollout.InitialStart(baseDeck)) };
        foreach (CardSnapshot offered in snapshot.Offer.Cards)
            options.Add(new DecisionOption(offered.Id, offered.Id, rollout.InitialStart(baseDeck.Append(data.Cards.Get(offered.Id, offered.Upgraded)).ToList())));
        return Evaluate(data, snapshot, rollout, options, rollouts, seed);
    }

    /// <summary>
    /// Runs every option through the same simulated futures (same seeds, so the same luck) and reports how each does and how
    /// it differs from the first option, the baseline.
    /// </summary>
    public static AdviceReport Evaluate(SimData data, RunSnapshot snapshot, ActRollout rollout, IReadOnlyList<DecisionOption> options, int rollouts, ulong seed, IReadOnlyList<string>? notes = null)
    {
        var results = new RolloutResult[options.Count][];
        for (int j = 0; j < options.Count; j++) results[j] = new RolloutResult[rollouts];

        Parallel.For(0, rollouts, i =>
        {
            ulong rolloutSeed = SimRng.Mix(seed, (ulong)i);
            for (int j = 0; j < options.Count; j++)
                results[j][i] = rollout.Run(options[j].Start, rolloutSeed);
        });

        double DeathRate(RolloutResult[] rs, string room) => rs.Count(r => r.DiedTo != null && RoomOf(data, r.DiedTo) == room) / (double)rs.Length;
        double HpLostPerFight(RolloutResult[] rs, string room)
        {
            var fights = rs.SelectMany(r => r.Log).Where(l => RoomOf(data, l.Encounter) == room).ToList();
            return fights.Count == 0 ? 0 : fights.Average(l => (double)l.HpLost);
        }

        double[] skipValues = results[0].Select(ValueOf).ToArray();
        double[] skipSurvived = results[0].Select(r => r.Survived ? 1.0 : 0.0).ToArray();
        double[] skipHp = results[0].Select(r => (double)r.HpEnd).ToArray();

        double[][] allValues = results.Select(rs => rs.Select(ValueOf).ToArray()).ToArray();
        int bestIndex = Enumerable.Range(0, options.Count).MaxBy(j => allValues[j].Average());
        double pointScale = allValues.SelectMany(v => v).Where(v => v > 0).DefaultIfEmpty(1.0).Average();

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
            (double dBest, double dBestSe) = j == bestIndex ? (0.0, 0.0) : PairedDifference(values, allValues[bestIndex]);
            int survivors = rs.Count(r => r.Survived);

            reports.Add(new OptionReport
            {
                Label = options[j].Label,
                CardId = options[j].CardId,
                IsBaseline = j == 0,
                Rollouts = rollouts,
                SurvivalRate = survivors / (double)rollouts,
                MeanHpEnd = hp.Average(),
                MeanHpEndIfSurvived = survivors == 0 ? 0 : rs.Where(r => r.Survived).Average(r => r.HpEnd),
                MeanFightsWon = rs.Average(r => r.FightsWon),
                ProbeWinRate = rs.Where(r => r.Survived && r.ProbeFights > 0).Select(r => (double)r.ProbeWins / r.ProbeFights).DefaultIfEmpty(double.NaN).Average(),
                ProbeHpLost = rs.Where(r => r.Survived && r.ProbeFights > 0).Select(r => (double)r.ProbeHpLost / r.ProbeFights).DefaultIfEmpty(double.NaN).Average(),
                DeltaVsBest = dBest, DeltaVsBestSe = dBestSe, PointScale = pointScale,
                Value = values.Average(),
                DeltaSurvival = dSurv, DeltaSurvivalSe = dSurvSe,
                DeltaHp = dHp, DeltaHpSe = dHpSe,
                DeltaValue = dValue, DeltaValueSe = dValueSe,
                NormalDeathRate = DeathRate(rs, "Monster"), EliteDeathRate = DeathRate(rs, "Elite"), BossDeathRate = DeathRate(rs, "Boss"),
                NormalHpLost = HpLostPerFight(rs, "Monster"), EliteHpLost = HpLostPerFight(rs, "Elite"), BossHpLost = HpLostPerFight(rs, "Boss"),
                Killers = rs.Where(r => r.DiedTo != null).GroupBy(r => r.DiedTo!).OrderByDescending(g => g.Count()).Take(3).Select(g => (g.Key, g.Count())).ToList(),
            });
        }

        return new AdviceReport
        {
            Decision = snapshot.Decision,
            Options = reports.OrderByDescending(r => r.Value).ToList(),
            Rollouts = rollouts,
            ExactPlan = rollout.HasExactPlan,
            BaselineLabel = options[0].Label,
            Notes = (notes ?? Array.Empty<string>()).Concat(ActNotes(snapshot.Run.Act)).ToList(),
            ApproximateCards = options.SelectMany(o => o.Start.Deck).Where(c => c.Approximate).Distinct().Count(),
            UnmodelledFights = results.SelectMany(r => r).Sum(r => r.UnmodelledFights) / options.Count,
            UnknownCards = data.Cards.UnknownIds.ToList(),
        };
    }

    /// <summary>What the reader should know about how far to trust the simulator in this act.</summary>
    private static IEnumerable<string> ActNotes(int act)
    {
        if (act == 2)
            yield return "Act 2 monsters come from the game's classes, but the simulated decks are weaker than real Act 2 decks and some boss mechanics (racing the Insatiable's sandpit, the Decimillipede's segments) are played badly, so read the comparison between options rather than the absolute survival numbers.";
        else if (act >= 3)
            yield return "Act 3 is only roughly modelled (several monster mechanics are missing and the fights come out much harder than in the real game); treat this advice as a rough guide.";
    }

    private static string RoomOf(SimData data, string encounter) => data.Encounters.Contains(encounter) ? data.Encounters.Get(encounter).RoomType : "";

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
