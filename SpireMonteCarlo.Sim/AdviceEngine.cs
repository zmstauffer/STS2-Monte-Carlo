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
    /// <summary>Average rest-of-run value (<see cref="AdviceEngine.ValueOf"/>, without the cards' later worth) over the futures that survive the act.</summary>
    public double MeanFutureIfSurvived { get; init; }
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
    /// <summary>
    /// The gap to the best option in points: percentage points of the chance to win the run as the advisor models it (see
    /// <see cref="AdviceEngine.ValueOf"/>).
    /// </summary>
    public double PointsVsBest => 100 * DeltaVsBest;
    public double PointsVsBestSe => 100 * DeltaVsBestSe;
    /// <summary>The part of this option's value that comes from its cards' worth later in the run (real players' ratings), in points.</summary>
    public double LongTermPoints { get; init; }
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
    /// <summary>The futures it would have run; fewer were run (<see cref="Rollouts"/>) when the answer was settled early.</summary>
    public int MaxRollouts { get; init; }
    public bool StoppedEarly => Rollouts < MaxRollouts;
    /// <summary>There was only one option, so nothing was simulated.</summary>
    public bool OnlyOption { get; init; }
    public bool ExactPlan { get; init; }
    public int ApproximateCards { get; init; }
    public int UnmodelledFights { get; init; }
    public IReadOnlyList<string> UnknownCards { get; init; } = Array.Empty<string>();
}

/// <summary>One choice the player could make: a label, the state of the run after making it, and any worth later in the run the rollouts can't see (an upgrade's, from <see cref="AdviceEngine.UpgradeLaterPoints"/>).</summary>
public sealed record DecisionOption(string Label, string? CardId, RolloutStart Start, double LaterPoints = 0);

public static class AdviceEngine
{
    /// <summary>Above this many options, all are screened on a fifth of the futures and only the best <see cref="ScreenKeep"/> (and the baseline) get the rest.</summary>
    public const int ScreenAbove = 8, ScreenKeep = 6;

    /// <summary>The smallest gap (in points) reported as a real difference between two options.</summary>
    public const double MeaningfulPoints = 1.0;

    /// <summary>Futures per batch between checks of whether the answer is settled.</summary>
    public const int EarlyStopBatch = 400;

    /// <summary>
    /// Standard errors an option must trail the best by to stop being simulated before the full count. Stricter than the 2 used in
    /// reports because the gap is looked at after every batch, and each look is another chance for noise to cross the line.
    /// </summary>
    public const double EarlyStopZ = 2.5;

    public enum Verdict { Open, Behind, Tied }

    /// <summary>
    /// Whether more futures could change what the report says about an option trailing the best by <paramref name="points"/> (negative)
    /// with standard error <paramref name="se"/>: Behind when it is clearly worse by a meaningful gap, Tied when the whole two-standard-error
    /// range is within <see cref="MeaningfulPoints"/> of the best, otherwise Open.
    /// </summary>
    public static Verdict Settled(double points, double se)
    {
        if (-points > Math.Max(EarlyStopZ * se, MeaningfulPoints)) return Verdict.Behind;
        if (Math.Abs(points) + 2 * se <= MeaningfulPoints) return Verdict.Tied;
        return Verdict.Open;
    }

    // How the end-of-Act-1 deck predicts surviving Act 2, fit by logistic regression on chained runs (sim calibrate-run --maps 400 --act2
    // at the calibrated HP scales, ~3900 Act 1 survivors): logit P = -6.99 + 5.93 x deck strength + 4.99 x share of HP carried in (after
    // the Ancient's heal). Survival by deck-strength fifth was 31/53/60/72/84%. The strength slope is stable between fits (5.5-5.9); the
    // HP slope is not (2.6-5.5), since HP carried in varies little.
    // Re-fit after the deck test was made to last as long as real Act 2 fights (ActRollout.CalibratedProbeHpScale) and Act 1 got its
    // per-room scales (6000 runs, 3888 Act 1 survivors): logit P = -4.39 + 4.31 x strength + 3.20 x HP share; survival by strength
    // fifth 45/60/69/75/82%. The old test's long fights spread decks by how well they race, which is why its slope was steeper.
    private const double NextActIntercept = -4.39, StrengthSlope = 4.31, HpSlope = 3.20;

    // Act 3 isn't modelled well enough to simulate, so the chance of winning it uses the same sensitivity to deck strength, centred on
    // the real rate: A10 Ironclads win 33.4% of runs and survive Acts 1 and 2 about 65% and 61% of the time, so ~84% of those who reach
    // Act 3 win it. Centred on the average end-of-Act-1 deck (0.51) for Act 1 decisions and on the average end-of-Act-2 deck (0.72,
    // tested against the same Act 2 fights) for Act 2 decisions.
    private const double Act3WinRate = 0.84, Act1DeckStrength = 0.51, Act2DeckStrength = 0.72, Act2HpShare = 0.9;

    // For Act 1 decisions the Act 3 chance is judged on the deck after the next act's card picks (RolloutResult.DevelopedStrength),
    // centred on its average (sim calibrate-run: DEVELOPED_MEAN below), so a card that the deck builds around counts for what it grows into.
    private const double DevelopedDeckStrength = 0.60;

    // A card's worth later in the run, from real players' ratings: across 82 Ironclad cards in the A10 bracket each +100 Codex Elo goes
    // with +3.1 points of run win rate (r = 0.61). Part of that is stronger players picking better cards, so half of it is counted, measured
    // from the average Ironclad card (the regression's centre), and only for the share of the run after the current act. (It was first
    // measured from the reward policy's Elo of skipping, which rises with deck size to ~1700 at 20 cards, so nearly every card looked worse
    // than Skip later in the run and Skip won most late-act rewards.)
    // Now the full slope (fourth playtest): half was counted on the view that the rollouts' deck test already sees part of a card's later
    // worth, but the simulator's card values don't follow real players' at all (sim cardvalue: rank correlation with Elo about 0 even
    // after the attack-bias fixes), and its futures never adapt picks to what the deck lacks, so it kept favouring attacks. On 41 logged
    // card rewards the best card was an attack in 67% of card picks before the fixes and 61% after them, against 41% of real Act 1 picks
    // (Codex pick rates by act); with the full slope 44%, the best is the highest-Elo card offered 46% of the time (24% before), and the
    // ranking agrees with Elo per screen at +0.38 (-0.04 before).
    public const double LongTermPointsPerElo = 0.031;

    private static double Logistic(double x) => 1 / (1 + Math.Exp(-x));
    private static double Logit(double p) => Math.Log(p / (1 - p));

    /// <summary>
    /// Value of one rollout: the chance to win the rest of the run from where it ends. A future that dies scores 0. One that survives
    /// the act scores the predicted chance of getting through what follows: for an Act 1 decision, Act 2 from the deck test and the HP
    /// carried in, times Act 3 from the deck test; for an Act 2 decision, Act 3; in Act 3, surviving is winning. Points are 100 times
    /// value differences: percentage points of that chance.
    /// </summary>
    public static double ValueOf(RolloutResult r, int act = 1)
    {
        if (!r.Survived) return 0.0;
        double hpShare = r.End != null && r.End.MaxHp > 0 ? r.End.Hp / r.End.MaxHp : (double)r.HpEnd / Math.Max(1, r.MaxHp);
        if (act >= 3 || double.IsNaN(r.DeckStrength)) return 0.98 + 0.02 * hpShare;   // no test: surviving is what counts, HP breaks ties
        double s = r.DeckStrength;
        if (act == 1)
        {
            double act2 = Logistic(NextActIntercept + StrengthSlope * s + HpSlope * hpShare);
            double act3 = double.IsNaN(r.DevelopedStrength)
                ? Logistic(Logit(Act3WinRate) + StrengthSlope * (s - Act1DeckStrength))
                : Logistic(Logit(Act3WinRate) + StrengthSlope * (r.DevelopedStrength - DevelopedDeckStrength));
            return act2 * act3;
        }
        return Logistic(Logit(Act3WinRate) + StrengthSlope * (s - Act2DeckStrength) + HpSlope * (hpShare - Act2HpShare));
    }

    // How much the run's value moves per unit of deck strength (1 - share of HP lost in the deck test), from ValueOf at average decks:
    // V x ((1 - P(Act 2)) + (1 - P(Act 3))) x slope with V ~0.5, P(Act 2) ~0.66, P(Act 3) ~0.84: 1.66 with the old slope 5.93, now
    // 0.5 x 0.50 x 4.31.
    private const double RunValuePerStrength = 1.08;

    /// <summary>
    /// An upgrade's worth after the current act, in points: the HP per test fight it saves the current deck (<see cref="ActRollout.TestDeck"/>,
    /// same fights for every deck) as deck strength, at <see cref="RunValuePerStrength"/>, for the share of the run after this act. Half is
    /// counted because the rollouts' end-of-act deck test sees part of it too (the same halving as the cards' Elo worth). Codex has no
    /// ratings for upgraded cards, and without this the upgrade screen went flat late in an act, where the rollouts barely tell upgrades apart.
    /// </summary>
    public static double UpgradeLaterPoints(RunSnapshot snapshot, double hpSavedPerFight, double maxHp) =>
        double.IsNaN(hpSavedPerFight) || maxHp <= 0 ? 0 : 0.5 * 100 * RunValuePerStrength * hpSavedPerFight / maxHp * ShareAfterThisAct(snapshot.Run);

    /// <summary>Share of the rest of the run that comes after the current act (acts are 17 floors, the run 3 acts).</summary>
    public static double ShareAfterThisAct(RunInfo run)
    {
        int floorInAct = run.TotalFloor - 17 * (run.Act - 1);
        double leftInAct = Math.Max(0, 17 - floorInAct), after = 17.0 * Math.Max(0, 3 - run.Act);
        return after + leftInAct <= 0 ? 0 : after / (after + leftInAct);
    }

    /// <summary>What the cards an option adds to the deck are worth later in the run, in points (see <see cref="LongTermPointsPerElo"/>).</summary>
    public static double LongTermPoints(SimData data, RunSnapshot snapshot, IEnumerable<CardDef> optionDeck)
    {
        RewardPool pool = data.PoolFor(snapshot.Run.Character);
        var had = snapshot.Deck.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Count());
        var current = snapshot.Deck.Select(c => data.Cards.Get(c.Id, c.Upgraded)).ToList();
        double average = RewardPool.DefaultElo, total = 0;
        foreach (CardDef c in optionDeck)
        {
            if (had.TryGetValue(c.Id, out int left) && left > 0) { had[c.Id] = left - 1; continue; }
            // How well it fits the deck counts too, as it does for the simulated player's picks (Synergy.Bonus).
            if (pool.HasElo(c.Id)) total += LongTermPointsPerElo * (pool.Elo(c.Id) + Synergy.Bonus(c, current) - average);
        }
        return total * ShareAfterThisAct(snapshot.Run);
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
    public static AdviceReport Evaluate(SimData data, RunSnapshot snapshot, ActRollout rollout, IReadOnlyList<DecisionOption> options, int rollouts, ulong seed, IReadOnlyList<string>? notes = null, bool reuse = false)
    {
        // Nothing to compare (a map node with one way on): say so instead of simulating.
        if (options.Count == 1)
            return new AdviceReport
            {
                Decision = snapshot.Decision, BaselineLabel = options[0].Label, Rollouts = 0, OnlyOption = true, Notes = notes ?? Array.Empty<string>(),
                Options = new[] { new OptionReport { Label = options[0].Label, CardId = options[0].CardId, IsBaseline = true } },
            };
        var results = new RolloutResult[options.Count][];
        for (int j = 0; j < options.Count; j++) results[j] = new RolloutResult[rollouts];
        int act0 = snapshot.Run.Act;
        double[] laterPoints = options.Select(o => LongTermPoints(data, snapshot, o.Start.Deck) + o.LaterPoints).ToArray();
        double FutureValue(int j, RolloutResult r) => ValueOf(r, act0) + (r.Survived ? laterPoints[j] / 100 : 0);

        void RunFutures(int from, int to, IReadOnlyList<int> which) => Parallel.For(from, to, i =>
        {
            ulong rolloutSeed = SimRng.Mix(seed, (ulong)i);
            foreach (int j in which) results[j][i] = rollout.Run(options[j].Start, rolloutSeed);
        });

        // Options this situation already simulated (the rest site's upgrades, when the card-upgrade screen follows) are reused as they are.
        string situation = $"{seed}|{rollouts}|{SituationKey(snapshot)}";
        string[] keys = options.Select(o => StartKey(o.Start)).ToArray();
        int[] count = new int[options.Count];   // futures each option has so far
        var live = Enumerable.Range(0, options.Count).ToList();   // options still being simulated
        for (int j = 0; j < options.Count; j++)
            if (reuse && CachedFutures(situation, keys[j]) is { } cached)
            {
                count[j] = Math.Min(cached.Length, rollouts);
                Array.Copy(cached, results[j], count[j]);
            }

        // Futures run in batches, and simulating stops once the answer is settled (see Settled): a clear winner or a clear tie shows
        // after a few hundred futures, a close call gets the full count. Options clearly behind the best stop getting futures.
        // With many options (a shop's bundles), the first batch is a screen: only the best ScreenKeep (and the baseline) go on.
        // Batch ends fall on a fixed grid, so futures reused from the rest site line up with the card-upgrade screen's batches.
        bool screening = count.Count(c => c == 0) > ScreenAbove;
        int first = screening ? Math.Min(rollouts, Math.Max(100, rollouts / 5)) : Math.Min(rollouts, EarlyStopBatch);
        int n = 0;
        while (n < rollouts)
        {
            int next = n == 0 ? first : Math.Min(rollouts, n + EarlyStopBatch);
            // (A reused option can be behind the others if the rest site stopped simulating it early; it catches up here.)
            foreach (var group in live.Where(j => count[j] < next).GroupBy(j => count[j]).ToList())
            {
                RunFutures(group.Key, next, group.ToList());
                foreach (int j in group) count[j] = next;
            }
            n = next;
            if (n >= rollouts) break;

            // Compare the options that have every future so far.
            var current = Enumerable.Range(0, options.Count).Where(j => count[j] >= n).ToList();
            double[] Values(int j) => results[j].Take(n).Select(r => FutureValue(j, r)).ToArray();
            var values = current.ToDictionary(j => j, Values);
            int best = current.MaxBy(j => values[j].Average());
            if (screening && n == first)
            {
                var kept = live.OrderByDescending(j => values[j].Average()).Take(ScreenKeep).ToList();
                if (live.Contains(0) && !kept.Contains(0)) kept.Add(0);
                live = kept;
                continue;
            }
            bool settled = true;
            foreach (int j in current)
            {
                if (j == best) continue;
                (double d, double se) = PairedDifference(values[j], values[best]);
                switch (Settled(100 * d, 100 * se))
                {
                    case Verdict.Behind: live.Remove(j); break;
                    case Verdict.Open: settled = false; break;
                }
            }
            if (settled || live.Count == 0) break;
        }
        for (int j = 0; j < options.Count; j++)
            if (count[j] < rollouts) results[j] = results[j].Take(count[j]).ToArray();
        if (reuse) RememberFutures(situation, keys, results);

        double DeathRate(RolloutResult[] rs, string room) => rs.Count(r => r.DiedTo != null && RoomOf(data, r.DiedTo) == room) / (double)rs.Length;
        double HpLostPerFight(RolloutResult[] rs, string room)
        {
            var fights = rs.SelectMany(r => r.Log).Where(l => RoomOf(data, l.Encounter) == room).ToList();
            return fights.Count == 0 ? 0 : fights.Average(l => (double)l.HpLost);
        }

        double[] skipSurvived = results[0].Select(r => r.Survived ? 1.0 : 0.0).ToArray();
        double[] skipHp = results[0].Select(r => (double)r.HpEnd).ToArray();

        // Each option's value per future: the chance to win from where the future ends, plus (if it survived the act) what the option's
        // cards are worth later in the run.
        int act = act0;
        double[] longTerm = laterPoints;
        double[][] allValues = results.Select((rs, j) => rs.Select(r => ValueOf(r, act) + (r.Survived ? longTerm[j] / 100 : 0)).ToArray()).ToArray();
        // The best is one of the options that ran every future (one dropped early as clearly behind can't take it on fewer futures).
        int most = results.Max(r => r.Length);
        int bestIndex = Enumerable.Range(0, options.Count).Where(j => results[j].Length == most).MaxBy(j => allValues[j].Average());
        double[] skipValues = allValues[0];

        var reports = new List<OptionReport>();
        for (int j = 0; j < options.Count; j++)
        {
            RolloutResult[] rs = results[j];
            double[] values = allValues[j];
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
                Rollouts = rs.Length,
                SurvivalRate = survivors / (double)rs.Length,
                MeanHpEnd = hp.Average(),
                MeanHpEndIfSurvived = survivors == 0 ? 0 : rs.Where(r => r.Survived).Average(r => r.HpEnd),
                MeanFutureIfSurvived = survivors == 0 ? 0 : rs.Where(r => r.Survived).Average(r => ValueOf(r, act)),
                MeanFightsWon = rs.Average(r => r.FightsWon),
                ProbeWinRate = rs.Where(r => r.Survived && r.ProbeFights > 0).Select(r => (double)r.ProbeWins / r.ProbeFights).DefaultIfEmpty(double.NaN).Average(),
                ProbeHpLost = rs.Where(r => r.Survived && r.ProbeFights > 0).Select(r => (double)r.ProbeHpLost / r.ProbeFights).DefaultIfEmpty(double.NaN).Average(),
                DeltaVsBest = dBest, DeltaVsBestSe = dBestSe, LongTermPoints = longTerm[j],
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
            Rollouts = results.Max(r => r.Length),
            MaxRollouts = rollouts,
            ExactPlan = rollout.HasExactPlan,
            BaselineLabel = options[0].Label,
            Notes = (notes ?? Array.Empty<string>()).Concat(ActNotes(snapshot.Run.Act)).ToList(),
            ApproximateCards = options.SelectMany(o => o.Start.Deck).Where(c => c.Approximate).Distinct().Count(),
            UnmodelledFights = results.SelectMany(r => r).Sum(r => r.UnmodelledFights) / options.Count,
            UnknownCards = data.Cards.UnknownIds.ToList(),
        };
    }

    // ---- reusing futures between the two screens of a rest site ----

    private static readonly object CacheLock = new();
    private static string? _cachedSituation;
    private static Dictionary<string, RolloutResult[]> _cachedFutures = new();

    /// <summary>Everything about the run a decision starts from, apart from the decision itself.</summary>
    private static string SituationKey(RunSnapshot s) =>
        $"{s.Run.Seed}|{s.Run.Act}|{s.Run.TotalFloor}|{s.Run.CurrentHp}/{s.Run.MaxHp}|{s.Run.Gold}|{s.Map?.Current?.Col},{s.Map?.Current?.Row}|" +
        $"{string.Join(",", s.Deck.Select(c => c.Id + (c.Upgraded ? "+" : "")))}|{string.Join(",", s.Relics)}|{string.Join(",", s.Potions.Select(p => p.Id))}";

    /// <summary>An option's resulting state; options with the same state have the same futures. Options with extras (events, forced paths) are never shared.</summary>
    private static string StartKey(RolloutStart s) =>
        s.EventEffect != null || s.ForcedNext != null || s.PendingFights.Count > 0 || s.AcquireOnStart.Count > 0 ? Guid.NewGuid().ToString()
            : $"{string.Join(",", s.Deck.Select(c => c.Id + (c.Upgraded ? "+" : "")))}|{s.Hp:F2}/{s.MaxHp:F2}|{s.Gold}|{string.Join(",", s.Relics)}|{string.Join(",", s.Potions)}|{s.RemovalsUsed}";

    private static RolloutResult[]? CachedFutures(string situation, string key)
    {
        lock (CacheLock) return _cachedSituation == situation && _cachedFutures.TryGetValue(key, out var r) ? r : null;
    }

    private static void RememberFutures(string situation, string[] keys, RolloutResult[][] results)
    {
        lock (CacheLock)
        {
            if (_cachedSituation != situation) { _cachedSituation = situation; _cachedFutures = new(); }
            for (int j = 0; j < keys.Length; j++) _cachedFutures[keys[j]] = results[j];
        }
    }

    /// <summary>What the reader should know about how far to trust the simulator in this act.</summary>
    private static IEnumerable<string> ActNotes(int act)
    {
        if (act == 2)
            yield return "Act 2 survival is calibrated on real decks, but some boss mechanics (racing the Insatiable's sandpit, the Knowledge Demon's curses) are played badly, so bosses come out harder and normal fights easier than in the real game.";
        else if (act >= 3)
            yield return "Act 3's bosses are modelled, but its survival is calibrated on only two real runs and the Knights come out harder than in the real game; treat this advice as a rough guide.";
    }

    private static string RoomOf(SimData data, string encounter) => data.Encounters.Contains(encounter) ? data.Encounters.Get(encounter).RoomType : "";

    /// <summary>Mean of (a - b) over paired samples (the futures both were run on) and its standard error.</summary>
    private static (double Mean, double StandardError) PairedDifference(double[] a, double[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double mean = 0;
        for (int i = 0; i < n; i++) mean += a[i] - b[i];
        mean /= n;
        double variance = 0;
        for (int i = 0; i < n; i++) variance += (a[i] - b[i] - mean) * (a[i] - b[i] - mean);
        variance /= Math.Max(1, n - 1);
        return (mean, Math.Sqrt(variance / n));
    }
}
