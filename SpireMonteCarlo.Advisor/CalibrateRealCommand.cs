using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;

namespace SpireMonteCarlo.Advisor;

/// <summary>
/// <c>advisor sim calibrate-real [--n N] [--scales a,b,..] [--act A] [--list]</c>: replays the owner's real fights from the decision log
/// (the deck, relics, potions and HP of the map snapshot before each fight, the encounter the act plan says came next) and compares the HP
/// they cost in simulation, at several player HP scales, with the HP they really cost (the HP on the card reward screen after the fight, so
/// Burning Blood's heal counts on both sides). The HP scale stands in for everything the simulated player lacks; fit on the simulator's own
/// decks it also absorbs how much weaker those decks are than real ones, which this fit on real decks does not.
/// </summary>
public static class CalibrateRealCommand
{
    public sealed record RealFight(string Seed, int Act, int Floor, string Encounter, string RoomType, int HpBefore, int HpAfter, int MaxHp, RunSnapshot Before);

    public static int Run(string[] args, SimData data)
    {
        int n = int.Parse(Option(args, "--n") ?? "100");
        double[] scales = (Option(args, "--scales") ?? "1.0,1.5,2.0,2.5,3.0,3.5,4.0").Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        int? onlyAct = Option(args, "--act") is { } a ? int.Parse(a) : null;
        bool list = args.Contains("--list");
        if (args.Contains("--survival")) return Survival(data, scales, int.Parse(Option(args, "--rollouts") ?? "300"), onlyAct);

        List<RealFight> fights = Extract(DecisionLog.Load(), data).Where(f => onlyAct == null || f.Act == onlyAct).ToList();
        Console.WriteLine($"{fights.Count} real fights with the HP before and after, {n} simulations each per scale.");

        // sim[f][s] = mean real-HP loss (death = all HP left) and death rate of fight f at scale s.
        var loss = new double[fights.Count, scales.Length];
        var deaths = new double[fights.Count, scales.Length];
        Parallel.For(0, fights.Count * scales.Length, k =>
        {
            int f = k / scales.Length, s = k % scales.Length;
            (loss[f, s], deaths[f, s]) = Simulate(data, fights[f], scales[s], n);
        });

        foreach (var group in fights.Select((f, i) => (f, i)).GroupBy(x => x.f.Act).OrderBy(g => g.Key))
        {
            var idx = group.Select(x => x.i).ToList();
            Console.WriteLine();
            Console.WriteLine($"Act {group.Key}: {idx.Count} fights, real HP lost per fight {idx.Average(i => fights[i].HpBefore - fights[i].HpAfter):F1}"
                + $" (normal {Mean(idx, i => fights[i].RoomType == "Monster")}, elite {Mean(idx, i => fights[i].RoomType == "Elite")}, boss {Mean(idx, i => fights[i].RoomType == "Boss")})");
            double real = idx.Average(i => fights[i].HpBefore - fights[i].HpAfter);
            for (int s = 0; s < scales.Length; s++)
            {
                double sim = idx.Average(i => loss[i, s]);
                Console.WriteLine($"  scale {scales[s],4:F1}: simulated {sim,5:F1} HP per fight (normal {SimMean(idx, s, "Monster")}, elite {SimMean(idx, s, "Elite")}, boss {SimMean(idx, s, "Boss")}), deaths {100 * idx.Average(i => deaths[i, s]):F1}%");
            }
            // The scale where the simulated average matches the real one (linear between the two scales that bracket it).
            double? fit = null;
            for (int s = 0; s + 1 < scales.Length; s++)
            {
                double a0 = idx.Average(i => loss[i, s]) - real, a1 = idx.Average(i => loss[i, s + 1]) - real;
                if (a0 >= 0 && a1 <= 0) { fit = scales[s] + (scales[s + 1] - scales[s]) * a0 / (a0 - a1); break; }
            }
            Console.WriteLine(fit is double x ? $"  -> simulated damage matches real at scale {x:F2}" : "  -> no scale in the list matches; widen --scales");
            // The same, with the rooms weighted like a whole act (about 6 normal fights, 2 elites and a boss; the log over-represents normal fights).
            double Weighted(Func<int, double> value)
            {
                double total = 0, weight = 0;
                foreach ((string room, double w) in new[] { ("Monster", 6.0), ("Elite", 2.0), ("Boss", 1.0) })
                {
                    var sel = idx.Where(i => fights[i].RoomType == room).ToList();
                    if (sel.Count == 0) continue;
                    total += w * sel.Average(value);
                    weight += w;
                }
                return total / weight;
            }
            double realW = Weighted(i => fights[i].HpBefore - fights[i].HpAfter);
            double? fitW = null;
            for (int s = 0; s + 1 < scales.Length; s++)
            {
                int s0 = s;
                double a0 = Weighted(i => loss[i, s0]) - realW, a1 = Weighted(i => loss[i, s0 + 1]) - realW;
                if (a0 >= 0 && a1 <= 0) { fitW = scales[s] + (scales[s + 1] - scales[s]) * a0 / (a0 - a1); break; }
            }
            Console.WriteLine($"  -> weighted like an act (6 normal : 2 elite : 1 boss): real {realW:F1} HP per fight; " + (fitW is double y ? $"simulated matches at scale {y:F2}" : "no scale in the list matches"));
            double sd = Math.Sqrt(idx.Average(i => Math.Pow(fights[i].HpBefore - fights[i].HpAfter - real, 2)));
            Console.WriteLine($"  (real per-fight spread {sd:F1} HP, so the real average is known to about +/-{sd / Math.Sqrt(idx.Count):F1})");

            if (list)
            {
                int mid = Array.FindIndex(scales, v => Math.Abs(v - ActRollout.CalibratedScaleFor(group.Key)) < 0.01);
                if (mid < 0) mid = scales.Length / 2;
                Console.WriteLine($"  per fight (simulated at scale {scales[mid]:F1}):");
                foreach (int i in idx)
                    Console.WriteLine($"    {fights[i].Seed} floor {fights[i].Floor,2} {fights[i].Encounter,-32} HP {fights[i].HpBefore,3} -> {fights[i].HpAfter,3}: real {fights[i].HpBefore - fights[i].HpAfter,4}, simulated {loss[i, mid],5:F1} (deaths {100 * deaths[i, mid]:F0}%)");
            }
        }
        return 0;

        string Mean(List<int> idx, Func<int, bool> where)
        {
            var sel = idx.Where(where).ToList();
            return sel.Count == 0 ? "-" : $"{sel.Average(i => fights[i].HpBefore - fights[i].HpAfter):F1} x{sel.Count}";
        }
        string SimMean(List<int> idx, int s, string room)
        {
            var sel = idx.Where(i => fights[i].RoomType == room).ToList();
            return sel.Count == 0 ? "-" : $"{sel.Average(i => loss[i, s]):F1}";
        }
    }

    // Real A10 Ironclad survival of each act for those who reach it (Codex: 65% and 61%, and 84% of Act 3 given the 33.4% run win rate).
    private static readonly double[] RealSurvival = { 0.65, 0.61, 0.84 };

    /// <summary>
    /// Whole-act rollouts from the first map screen of each act in the owner's runs (on the map: the screen before an act's Ancient comes
    /// before its heal) (a real deck and the act's real encounter plan), at each
    /// scale, against real A10 Ironclad act survival. Matching average damage per fight is a lower bound on the scale: players spend HP when
    /// they have it and play safe when they don't, which the bot does not, so survival is the better anchor for the rollouts' life-or-death.
    /// </summary>
    private static int Survival(SimData data, double[] scales, int rollouts, int? onlyAct)
    {
        var starts = new List<RunSnapshot>();
        foreach (DecisionLog.Run run in DecisionLog.Load())
            foreach (var act in run.Decisions.Select(d => d.Snapshot).Where(s => s.Decision == DecisionType.Map && s.Plan != null && s.Map?.Current != null).GroupBy(s => s.Run.Act))
                if (onlyAct == null || act.Key == onlyAct) starts.Add(act.OrderBy(s => s.Run.TotalFloor).First());
        Console.WriteLine($"{starts.Count} act starts from the decision log, {rollouts} rollouts each per scale.");
        foreach (var act in starts.GroupBy(s => s.Run.Act).OrderBy(g => g.Key))
        {
            Console.WriteLine();
            Console.WriteLine($"Act {act.Key} (real A10 Ironclads survive {100 * RealSurvival[Math.Min(act.Key, 3) - 1]:F0}%): " + string.Join(", ", act.Select(s => $"{s.Run.Seed} floor {s.Run.TotalFloor} HP {s.Run.CurrentHp}/{s.Run.MaxHp} {s.Deck.Count} cards")));
            var survival = new double[scales.Length];
            for (int k = 0; k < scales.Length; k++)
            {
                double scale = scales[k];
                var rates = act.Select(snap =>
                {
                    var rollout = new ActRollout(data, snap) { PlayerHpScale = scale };
                    var deck = snap.Deck.Where(c => data.Cards.Contains(c.Id)).Select(c => data.Cards.Get(c.Id, c.Upgraded)).ToList();
                    int survived = 0;
                    Parallel.For(0, rollouts, () => 0, (i, _, n) => n + (rollout.Run(deck, (ulong)i + 1).Survived ? 1 : 0), n => Interlocked.Add(ref survived, n));
                    return (double)survived / rollouts;
                }).ToList();
                survival[k] = rates.Average();
                Console.WriteLine($"  scale {scale,4:F2}: survival {100 * survival[k]:F1}%  ({string.Join(" / ", rates.Select(r => $"{100 * r:F0}%"))})");
            }
            double target = RealSurvival[Math.Min(act.Key, 3) - 1];
            for (int k = 0; k + 1 < scales.Length; k++)
                if ((survival[k] - target) * (survival[k + 1] - target) <= 0 && survival[k] != survival[k + 1])
                {
                    Console.WriteLine($"  -> matches real survival at scale {scales[k] + (scales[k + 1] - scales[k]) * (target - survival[k]) / (survival[k + 1] - survival[k]):F2}");
                    break;
                }
        }
        return 0;
    }

    /// <summary>Mean real-HP loss (a death costs all the HP the player had) and the death rate of one real fight replayed at an HP scale.</summary>
    public static (double Loss, double Deaths) Simulate(SimData data, RealFight fight, double scale, int n)
    {
        RunSnapshot snap = fight.Before;
        var deck = snap.Deck.Where(c => data.Cards.Contains(c.Id)).Select(c => data.Cards.Get(c.Id, c.Upgraded)).ToList();
        var relics = snap.Relics.Select(RelicRules.Parse).Where(k => k != RelicKind.Unknown).ToList();
        var potions = snap.Potions.Select(p => PotionLibrary.Find(p.Id)).OfType<PotionDef>().ToList();
        EncounterDef encounter = data.Encounters.Get(fight.Encounter);
        int hp = (int)Math.Round(fight.HpBefore * scale), maxHp = (int)Math.Round(fight.MaxHp * scale);
        int stakes = encounter.RoomType switch { "Boss" => 2, "Elite" => 1, _ => 0 };
        double lost = 0, died = 0;
        for (int k = 0; k < n; k++)
        {
            ulong seed = SimRng.Mix(0xCA11B, (ulong)(k * 7919 + fight.Floor));
            string[] lineup = encounter.Generate(new SimRng(SimRng.Mix(seed, 1)));
            FightResult r = FightSimulator.Run(deck, hp, maxHp, lineup.Select(data.Monsters.Get), snap.Run.Ascension, seed, new BasicBot(), altStarts: encounter.AltStarts,
                services: data.Services, potions: potions.ToList(), stakes: stakes, relics: relics, hpScale: scale);
            if (r.Won) lost += (hp - Math.Min(maxHp, r.HpAfter)) / scale;
            else { lost += fight.HpBefore; died++; }
        }
        return (lost / n, died / n);
    }

    /// <summary>
    /// Real fights from the decision log: a card reward screen right after a map choice one floor earlier. The encounter is the one that left
    /// the act plan between the two snapshots (a boss when the player stands on the boss node).
    /// </summary>
    public static IEnumerable<RealFight> Extract(IEnumerable<DecisionLog.Run> runs, SimData data)
    {
        foreach (DecisionLog.Run run in runs)
        {
            var snaps = run.Decisions.Select(d => d.Snapshot).ToList();
            for (int i = 1; i < snaps.Count; i++)
            {
                RunSnapshot before = snaps[i - 1], after = snaps[i];
                if (after.Decision != DecisionType.CardReward || before.Decision != DecisionType.Map) continue;
                if (after.Run.TotalFloor != before.Run.TotalFloor + 1 || after.Run.Act != before.Run.Act) continue;
                if (before.Plan == null || after.Plan == null) continue;
                string? encounter = Removed(before.Plan.Normal, after.Plan.Normal) ?? Removed(before.Plan.Elite, after.Plan.Elite);
                if (encounter == null && after.Map?.Current is { } here && after.Map.Boss is { } boss && here.Col == boss.Col && here.Row == boss.Row)
                    encounter = before.Plan.Boss;
                if (encounter == null || !data.Encounters.Contains(encounter)) continue;
                EncounterDef def = data.Encounters.Get(encounter);
                if (def.Generate(new SimRng(1)).Any(m => !data.Monsters.Contains(m))) continue;
                yield return new RealFight(run.Seed, before.Run.Act, after.Run.TotalFloor, encounter, def.RoomType, before.Run.CurrentHp, after.Run.CurrentHp, before.Run.MaxHp, before);
            }
            if (Fatal(run, data) is { } fatal) yield return fatal;
        }
    }

    /// <summary>
    /// The fight that ended a run: the run died one floor after its last snapshot, a map choice whose next rooms are all one kind of fight.
    /// No card reward follows a death, so without this the fights that matter most (and cost all the HP left) were never counted.
    /// </summary>
    private static RealFight? Fatal(DecisionLog.Run run, SimData data)
    {
        if (run.End is not { Won: false } end || run.Decisions.Count == 0) return null;
        RunSnapshot last = run.Decisions[^1].Snapshot;
        if (last.Decision != DecisionType.Map || last.Plan == null || last.Map?.Current is not { } here) return null;
        if (end.Act != last.Run.Act || end.Floor != last.Run.TotalFloor + 1) return null;
        MapPointSnapshot? node = last.Map.Points.FirstOrDefault(p => p.Col == here.Col && p.Row == here.Row);
        if (node == null || node.Children.Count == 0) return null;
        var kinds = node.Children.Select(c => last.Map.Boss is { } b && c.Col == b.Col && c.Row == b.Row ? "Boss"
            : last.Map.Points.FirstOrDefault(p => p.Col == c.Col && p.Row == c.Row)?.Type).Distinct().ToList();
        if (kinds.Count != 1) return null;
        string? encounter = kinds[0] switch
        {
            "Monster" => last.Plan.Normal.FirstOrDefault(),
            "Elite" => last.Plan.Elite.FirstOrDefault(),
            "Boss" => last.Plan.Boss,
            _ => null,
        };
        if (encounter == null || !data.Encounters.Contains(encounter)) return null;
        EncounterDef def = data.Encounters.Get(encounter);
        if (def.Generate(new SimRng(1)).Any(m => !data.Monsters.Contains(m))) return null;
        return new RealFight(run.Seed, last.Run.Act, end.Floor, encounter, def.RoomType, last.Run.CurrentHp, 0, last.Run.MaxHp, last);
    }

    private static string? Removed(List<string> before, List<string> after) =>
        before.Count == after.Count + 1 && before.Skip(1).SequenceEqual(after) ? before[0] : null;

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
