using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using SpireMonteCarlo.Sim;

namespace SpireMonteCarlo.Advisor;

/// <summary>
/// <c>sim fitleaf</c>: fits the bot's leaf weights to what plans actually lead to. It plays the bench's Ironclad fights with the current
/// bot; at each turn it takes the plans the bot's search considered, records each plan's leaf terms (<see cref="LeafFeatures"/>), and
/// finishes the fight from each plan's end state many times on shuffled draws (the same random futures for every plan of a turn) to
/// measure the HP it really costs. A least-squares fit of that cost on the terms, comparing only plans of the same turn (the bot only
/// ever ranks those against each other), gives new weights. Half the fights are held out to report how much HP the old and new
/// weights' choices lose against the best plan of each turn.
/// </summary>
public static class FitLeafCommand
{
    private sealed record Row(int Root, bool Test, double[] X, double Target, double OldValue, double EvenHalf = 0, double OddHalf = 0);

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static int Run(string[] args, SimData data)
    {
        int seeds = int.Parse(Option(args, "--n") ?? "4");
        int rollouts = int.Parse(Option(args, "--rollouts") ?? "16");
        int plans = int.Parse(Option(args, "--plans") ?? "8");
        int hp = int.Parse(Option(args, "--hp") ?? "80");
        string? room = Option(args, "--room");
        string? csv = Option(args, "--out");
        string[] fitNames = (Option(args, "--features") ?? string.Join(",", LeafFeatures.Names)).Split(',');
        var tuning = BotTuning.Default.Clone();
        if (Option(args, "--tune") is { } spec) tuning.Apply(spec);
        var bot = new BasicBot { Tuning = tuning };

        var fights = new List<(string Encounter, int Stakes, List<CardDef> Deck, int Seed)>();
        foreach (var (_, deckSpec) in TuneCommand.Decks)
        {
            List<CardDef> deck = data.ParseDeck(deckSpec);
            void Add(IEnumerable<string> ids, int stakes)
            {
                foreach (string id in ids.Where(data.Encounters.Contains))
                    for (int i = 0; i < seeds; i++) fights.Add((id, stakes, deck, i));
            }
            if (room is null or "normal") Add(TuneCommand.Normals, 0);
            if (room is null or "elite") Add(TuneCommand.Elites, 1);
            if (room is null or "boss") Add(TuneCommand.Bosses, 2);
        }

        var sw = Stopwatch.StartNew();
        var rows = new ConcurrentBag<Row>();
        var turnInfo = new ConcurrentDictionary<int, string>();
        if (Option(args, "--in") is { } input)
        {
            // Refit from rows saved by an earlier run's --out.
            foreach (string line in File.ReadLines(input).Skip(1))
            {
                double[] v = line.Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
                rows.Add(new Row((int)v[0], v[1] == 1, v.Skip(6).ToArray(), v[2], v[3], v[4], v[5]));
            }
            fights.Clear();
        }
        int roots = 0, done = 0;
        Parallel.For(0, fights.Count, f =>
        {
            var (id, stakes, deck, seedIndex) = fights[f];
            EncounterDef encounter = data.Encounters.Get(id);
            ulong seed = SimRng.Mix(9001, (ulong)(f * 131 + seedIndex));
            string[] lineup = encounter.Generate(new SimRng(SimRng.Mix(seed, 1)));
            var combat = new Combat(deck, hp, hp, lineup.Select(data.Monsters.Get), 10, seed, altStarts: encounter.AltStarts, services: data.Services, stakes: stakes);
            var rng = new SimRng(SimRng.Mix(seed, 2));
            bool test = f % 2 == 1;
            Span<double> x = stackalloc double[LeafFeatures.Count];
            while (combat.Result == CombatResult.Ongoing && combat.Turn <= 40)
            {
                var candidates = new List<(Combat State, double[] X, double Old)>();
                foreach (var (path, state) in bot.CandidatePlans(combat))
                {
                    double old = bot.LeafValue(combat, state, deep: true, plays: path.Count, x, out bool terminal);
                    if (!terminal) candidates.Add((state, x.ToArray(), old));
                }
                if (candidates.Count >= 2)
                {
                    // The plans the bot would weigh most, plus a few others so the fit also sees what makes a plan bad.
                    var ranked = candidates.OrderByDescending(c => c.Old).ToList();
                    var chosen = ranked.Take((plans + 1) / 2).ToList();
                    var rest = ranked.Skip(chosen.Count).ToList();
                    while (chosen.Count < plans && rest.Count > 0) { int k = rng.Next(rest.Count); chosen.Add(rest[k]); rest.RemoveAt(k); }
                    int root = Interlocked.Increment(ref roots);
                    turnInfo[root] = $"{id},{Array.FindIndex(TuneCommand.Decks, d => data.ParseDeck(d.Deck).Count == deck.Count)},{combat.Turn}";
                    ulong futures = SimRng.Mix(seed, (ulong)combat.Turn * 1000 + 7);
                    foreach (var (state, features, old) in chosen)
                    {
                        double even = 0, odd = 0;
                        for (int r = 0; r < rollouts; r++)
                        {
                            double loss = Finish(state, bot, SimRng.Mix(futures, (ulong)r)) - combat.HpLost;
                            if (r % 2 == 0) even += loss; else odd += loss;
                        }
                        int half = Math.Max(1, rollouts / 2);
                        rows.Add(new Row(root, test, features, -(even + odd) / rollouts, old, -even / half, -odd / half));
                    }
                }
                FightSimulator.PlayTurn(combat, bot);
                if (combat.Result != CombatResult.Ongoing) break;
                combat.EndPlayerTurn();
            }
            int n = Interlocked.Increment(ref done);
            if (n % 50 == 0) Console.Error.Write($"\r  {n}/{fights.Count} fights, {roots} turns ({sw.Elapsed.TotalSeconds:F0}s)   ");
        });
        Console.Error.WriteLine();
        var all = rows.OrderBy(r => r.Root).ToList();
        Console.WriteLine($"{fights.Count} fights, {all.Select(r => r.Root).Distinct().Count()} turns, {all.Count} plans ({sw.Elapsed.TotalSeconds:F0}s)");

        if (csv != null && fights.Count > 0)
        {
            using var w = new StreamWriter(csv);
            w.WriteLine("root,test,target,old,even,odd," + string.Join(",", LeafFeatures.Names));
            using var info = new StreamWriter(Path.ChangeExtension(csv, ".turns.csv"));
            info.WriteLine("root,encounter,deck,turn");
            foreach (var (root, text) in turnInfo.OrderBy(kv => kv.Key)) info.WriteLine($"{root},{text}");
            foreach (Row r in all)
                w.WriteLine(string.Join(",", new[] { r.Root.ToString(), r.Test ? "1" : "0", F(r.Target), F(r.OldValue), F(r.EvenHalf), F(r.OddHalf) }.Concat(r.X.Select(F))));
        }

        int[] use = fitNames.Select(n => Array.IndexOf(LeafFeatures.Names, n)).Where(i => i >= 0).ToArray();
        double[] start = use.Select(i => CurrentWeight(tuning, i)).ToArray();
        double temperature = double.Parse(Option(args, "--temp") ?? "1", CultureInfo.InvariantCulture);
        double[] beta = (Option(args, "--loss") ?? "choice") == "ls"
            ? Fit(all.Where(r => !r.Test).ToList(), use)
            : FitChoice(all.Where(r => !r.Test).ToList(), use, start, temperature, double.Parse(Option(args, "--ridge") ?? "0.001", CultureInfo.InvariantCulture));
        Console.WriteLine();
        Console.WriteLine("fitted weight per term (value in HP; the bot maximises the sum):");
        for (int j = 0; j < use.Length; j++) Console.WriteLine($"  {LeafFeatures.Names[use[j]],-16} {beta[j],9:0.000}   (now {CurrentWeight(tuning, use[j]),7:0.###})");
        Console.WriteLine();
        foreach (var (label, test) in new[] { ("train", false), ("held out", true) })
        {
            var part = all.Where(r => r.Test == test).ToList();
            double oldRegret = Regret(part, r => r.OldValue), newRegret = Regret(part, r => use.Select((i, j) => beta[j] * r.X[i]).Sum());
            double randomRegret = part.GroupBy(r => r.Root).Average(g => g.Max(r => r.Target) - g.Average(r => r.Target));
            Console.WriteLine($"{label,-9} HP lost vs the best plan of each turn: old weights {oldRegret:F2}, fitted {newRegret:F2}, random plan {randomRegret:F2}   (within-turn R2 fitted {R2(part, use, beta):F2})");
            // Judged on the odd rollouts only: the old and new choices, and the plan the even rollouts say is best (what knowing the future's
            // average perfectly would buy, less its own noise). The gap from 0 to that last number is rollout noise, not bot error.
            double Odd(Func<Row, double> pick) => part.GroupBy(r => r.Root).Average(g => g.Max(r => r.OddHalf) - g.MaxBy(pick)!.OddHalf);
            Console.WriteLine($"          on half the rollouts: old {Odd(r => r.OldValue):F2}, fitted {Odd(r => use.Select((i, j) => beta[j] * r.X[i]).Sum()):F2}, best by the other half {Odd(r => r.EvenHalf):F2}");
        }
        Console.WriteLine();
        Console.WriteLine("BOT_TUNE=" + ToTuning(tuning, use, beta));
        return 0;
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>HP lost from a plan's end state to the end of the fight, with the bench's +40 for a death.</summary>
    private static double Finish(Combat state, BasicBot bot, ulong salt)
    {
        Combat c = state.Clone(salt);
        c.Rng.Shuffle(c.DrawPile);
        // A plan that stopped at a draw is planned again with the new cards; otherwise the turn ends here.
        if (c.Hand.Any(card => card.Tag == 0)) FightSimulator.PlayTurn(c, bot);
        while (c.Result == CombatResult.Ongoing)
        {
            c.EndPlayerTurn();
            if (c.Result != CombatResult.Ongoing || c.Turn > 40) break;
            FightSimulator.PlayTurn(c, bot);
        }
        // A fight still going at the turn cap counts as a death, or stalling would look better than a fight lost to the Waterfall Giant's explosion.
        return c.Result == CombatResult.Won ? c.HpLost : c.HpLost + Math.Max(0, c.Hp) + 40;
    }

    /// <summary>
    /// Fits for choosing well rather than predicting every plan: the leaf values of a turn's plans become choice probabilities (a softmax
    /// at <paramref name="temperature"/> HP), and gradient ascent (Adam) maximises the rollout value of that soft choice, starting from the
    /// current weights and pulled back toward them by a small ridge. As the temperature falls this approaches "pick the best plan".
    /// </summary>
    private static double[] FitChoice(List<Row> rows, int[] use, double[] start, double temperature, double ridge)
    {
        int k = use.Length;
        // Standardise the terms so one learning rate suits them all.
        double[] scale = use.Select(i => { double m = rows.Average(r => r.X[i]); double sd = Math.Sqrt(rows.Average(r => (r.X[i] - m) * (r.X[i] - m))); return sd > 1e-9 ? sd : 1; }).ToArray();
        var groups = rows.GroupBy(r => r.Root).Select(g =>
        {
            var list = g.ToList();
            double my = list.Average(r => r.Target);
            return (Z: list.Select(r => use.Select((i, j) => r.X[i] / scale[j]).ToArray()).ToArray(), T: list.Select(r => r.Target - my).ToArray());
        }).ToList();
        double[] theta = start.Select((b, j) => b * scale[j]).ToArray();
        double[] theta0 = (double[])theta.Clone();
        double[] m1 = new double[k], m2 = new double[k];
        const double rate = 0.02, b1 = 0.9, b2 = 0.999;
        for (int step = 1; step <= 3000; step++)
        {
            var grad = new double[k];
            foreach (var (z, t) in groups)
            {
                int n = t.Length;
                var p = new double[n];
                double max = double.NegativeInfinity;
                for (int i = 0; i < n; i++) { double v = 0; for (int j = 0; j < k; j++) v += theta[j] * z[i][j]; p[i] = v / temperature; max = Math.Max(max, p[i]); }
                double sum = 0;
                for (int i = 0; i < n; i++) { p[i] = Math.Exp(p[i] - max); sum += p[i]; }
                double tp = 0;
                var zp = new double[k];
                for (int i = 0; i < n; i++) { p[i] /= sum; tp += p[i] * t[i]; for (int j = 0; j < k; j++) zp[j] += p[i] * z[i][j]; }
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < k; j++) grad[j] += p[i] * (t[i] - tp) * (z[i][j] - zp[j]) / temperature;
            }
            for (int j = 0; j < k; j++)
            {
                double g = grad[j] / groups.Count - 2 * ridge * (theta[j] - theta0[j]);
                m1[j] = b1 * m1[j] + (1 - b1) * g;
                m2[j] = b2 * m2[j] + (1 - b2) * g * g;
                theta[j] += rate * (m1[j] / (1 - Math.Pow(b1, step))) / (Math.Sqrt(m2[j] / (1 - Math.Pow(b2, step))) + 1e-8);
            }
        }
        return theta.Select((v, j) => v / scale[j]).ToArray();
    }

    /// <summary>Least squares on the differences from each turn's mean (a fixed effect per turn), so only how plans of one turn differ counts.</summary>
    private static double[] Fit(List<Row> rows, int[] use)
    {
        int k = use.Length;
        var xtx = new double[k, k];
        var xty = new double[k];
        foreach (var g in rows.GroupBy(r => r.Root))
        {
            var list = g.ToList();
            double[] mean = use.Select(i => list.Average(r => r.X[i])).ToArray();
            double my = list.Average(r => r.Target);
            foreach (Row r in list)
            {
                for (int a = 0; a < k; a++)
                {
                    double xa = r.X[use[a]] - mean[a];
                    xty[a] += xa * (r.Target - my);
                    for (int b = 0; b < k; b++) xtx[a, b] += xa * (r.X[use[b]] - mean[b]);
                }
            }
        }
        // A little ridge keeps terms that never vary (no Sandpit in Act 1, rare exhausts) from blowing up.
        for (int a = 0; a < k; a++) xtx[a, a] += 1e-3 * Math.Max(1, xtx[a, a]) + 1e-6;
        return Solve(xtx, xty);
    }

    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var y = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = Enumerable.Range(col, n - col).MaxBy(r => Math.Abs(m[r, col]));
            for (int c = 0; c < n; c++) (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            (y[col], y[pivot]) = (y[pivot], y[col]);
            for (int r = 0; r < n; r++)
            {
                if (r == col || m[col, col] == 0) continue;
                double factor = m[r, col] / m[col, col];
                for (int c = col; c < n; c++) m[r, c] -= factor * m[col, c];
                y[r] -= factor * y[col];
            }
        }
        return Enumerable.Range(0, n).Select(i => m[i, i] == 0 ? 0 : y[i] / m[i, i]).ToArray();
    }

    /// <summary>Average HP the chosen plan (highest predicted value) loses against the best plan of the same turn, by the rollouts.</summary>
    private static double Regret(List<Row> rows, Func<Row, double> predict) =>
        rows.GroupBy(r => r.Root).Average(g => g.Max(r => r.Target) - g.MaxBy(predict)!.Target);

    private static double R2(List<Row> rows, int[] use, double[] beta)
    {
        double ss = 0, res = 0;
        foreach (var g in rows.GroupBy(r => r.Root))
        {
            var list = g.ToList();
            double my = list.Average(r => r.Target);
            var preds = list.Select(r => use.Select((i, j) => beta[j] * r.X[i]).Sum()).ToList();
            double mp = preds.Average();
            for (int i = 0; i < list.Count; i++)
            {
                ss += Math.Pow(list[i].Target - my, 2);
                res += Math.Pow(list[i].Target - my - (preds[i] - mp), 2);
            }
        }
        return ss == 0 ? 0 : 1 - res / ss;
    }

    /// <summary>The current weight of a term, signed the way the leaf adds it.</summary>
    private static double CurrentWeight(BotTuning t, int feature) => feature switch
    {
        LeafFeatures.Lost => -t.LostWeight,
        LeafFeatures.Future => -t.FutureWeight,
        LeafFeatures.ExhaustLoss => -t.ExhaustLoss,
        LeafFeatures.Progress => -t.Progress,
        LeafFeatures.Drawn => t.DrawValue,
        LeafFeatures.ReplanEnergy => t.ReplanEnergy,
        LeafFeatures.PowerGain => t.PowerGain,
        _ => t.Get(LeafFeatures.Names[feature]),
    };

    private static string ToTuning(BotTuning t, int[] use, double[] beta)
    {
        var parts = new List<string>();
        for (int j = 0; j < use.Length; j++)
        {
            (string name, double value) = use[j] switch
            {
                LeafFeatures.Lost => ("LostWeight", -beta[j]),
                LeafFeatures.Future => ("FutureWeight", -beta[j]),
                LeafFeatures.ExhaustLoss => ("ExhaustLoss", -beta[j]),
                LeafFeatures.Progress => ("Progress", -beta[j]),
                LeafFeatures.Drawn => ("DrawValue", beta[j]),
                LeafFeatures.ReplanEnergy => ("ReplanEnergy", beta[j]),
                LeafFeatures.PowerGain => ("PowerGain", beta[j]),
                _ => (LeafFeatures.Names[use[j]], beta[j]),
            };
            parts.Add($"{name}={F(value)}");
        }
        return string.Join(",", parts);
    }
}
