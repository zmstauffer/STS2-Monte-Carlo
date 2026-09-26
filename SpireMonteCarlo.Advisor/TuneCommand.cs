using System.Diagnostics;
using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Sim;

namespace SpireMonteCarlo.Advisor;

/// <summary>Scores the combat bot on a fixed suite of Act 1 fights (<c>sim bench</c>) and searches for better valuation weights (<c>sim tune</c>). Lower is better: HP lost, with deaths counted extra.</summary>
public static class TuneCommand
{
    private static readonly (string Name, string Deck)[] Decks =
    {
        ("starter", "STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH"),
        ("mid", "STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH,POMMEL_STRIKE,SHRUG_IT_OFF,INFLAME,ANGER,TRUE_GRIT,IRON_WAVE,THUNDERCLAP"),
        ("built", "STRIKE_IRONCLAD*3,DEFEND_IRONCLAD*4,BASH+,POMMEL_STRIKE+,SHRUG_IT_OFF,INFLAME,ANGER,IRON_WAVE,THUNDERCLAP,HEADBUTT,BATTLE_TRANCE,WHIRLWIND,FLAME_BARRIER,TWIN_STRIKE"),
    };

    private static readonly string[] Normals =
    {
        "TOADPOLES_WEAK", "SLIMES_WEAK", "NIBBITS_WEAK", "SEAPUNK_WEAK", "CORPSE_SLUGS_WEAK", "SLUDGE_SPINNER_WEAK", "FUZZY_WURM_CRAWLER_WEAK", "SHRINKER_BEETLE_WEAK",
        "MAWLER_NORMAL", "CULTISTS_NORMAL", "RUBY_RAIDERS_NORMAL", "GREMLIN_MERC_NORMAL", "SLIMES_NORMAL", "FLYCONID_NORMAL", "HAUNTED_SHIP_NORMAL", "TWO_TAILED_RATS_NORMAL",
    };
    private static readonly string[] Elites =
    {
        "PHROG_PARASITE_ELITE", "BYGONE_EFFIGY_ELITE", "TERROR_EEL_ELITE", "PHANTASMAL_GARDENERS_ELITE", "BYRDONIS_ELITE", "SKULKING_COLONY_ELITE",
    };
    private static readonly string[] Bosses =
    {
        "THE_KIN_BOSS", "CEREMONIAL_BEAST_BOSS", "VANTOM_BOSS", "WATERFALL_GIANT_BOSS", "SOUL_FYSH_BOSS", "LAGAVULIN_MATRIARCH_BOSS",
    };

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static int Bench(string[] args, SimData data)
    {
        int n = int.Parse(Option(args, "--n") ?? "300");
        var tuning = BotTuning.Default.Clone();
        if (Option(args, "--tune") is { } spec) tuning.Apply(spec);
        var sw = Stopwatch.StartNew();
        Suite suite = new(data, n, Option(args, "--room"));
        Report report = suite.Score(tuning);
        Console.WriteLine($"score {report.Score:F2}   normal {report.Normal:F1}  elite {report.Elite:F1}  boss {report.Boss:F1}   ({sw.Elapsed.TotalSeconds:F1}s)");
        foreach (var (name, loss) in report.PerDeck) Console.WriteLine($"  {name}: {loss:F1}");
        return 0;
    }

    public static int Tune(string[] args, SimData data)
    {
        int n = int.Parse(Option(args, "--n") ?? "250");
        int rounds = int.Parse(Option(args, "--rounds") ?? "3");
        var best = BotTuning.Default.Clone();
        if (Option(args, "--tune") is { } spec) best.Apply(spec);
        Suite suite = new(data, n, Option(args, "--room"));
        Report bestReport = suite.Score(best);
        Console.WriteLine($"start: {bestReport.Score:F2} (normal {bestReport.Normal:F1} elite {bestReport.Elite:F1} boss {bestReport.Boss:F1})");
        double[] factors = { 0.6, 1.5 };
        for (int round = 0; round < rounds; round++)
        {
            bool improved = false;
            foreach (string name in BotTuning.Names.Where(n => n is not ("Leaf" or "Horizon" or "Finalists")))
            {
                foreach (double factor in factors)
                {
                    BotTuning trial = best.Clone();
                    double current = trial.Get(name);
                    trial.Set(name, current == 0 ? 0.5 : current * factor);
                    Report r = suite.Score(trial);
                    if (r.Score < bestReport.Score - 0.05)
                    {
                        best = trial;
                        bestReport = r;
                        improved = true;
                        Console.WriteLine($"  {name} -> {trial.Get(name):0.###}: {r.Score:F2} (normal {r.Normal:F1} elite {r.Elite:F1} boss {r.Boss:F1})");
                    }
                }
            }
            Console.WriteLine($"round {round + 1}: {bestReport.Score:F2}  {best}");
            if (!improved) break;
        }
        Console.WriteLine($"best: {best}");
        return 0;
    }

    private sealed record Report(double Score, double Normal, double Elite, double Boss, List<(string, double)> PerDeck);

    private sealed class Suite
    {
        private readonly SimData _data;
        private readonly int _n;
        private readonly string? _onlyRoom;
        private readonly List<(string Encounter, string Room, int Deck)> _fights = new();
        private readonly List<List<CardDef>> _decks = new();

        public Suite(SimData data, int n, string? onlyRoom = null)
        {
            _data = data;
            _n = n;
            _onlyRoom = onlyRoom;
            foreach (var (_, deck) in Decks) _decks.Add(data.ParseDeck(deck));
            for (int d = 0; d < Decks.Length; d++)
            {
                if (onlyRoom is null or "normal") foreach (string e in Normals) if (data.Encounters.Contains(e)) _fights.Add((e, "normal", d));
                if (onlyRoom is null or "elite") foreach (string e in Elites) _fights.Add((e, "elite", d));
                if (onlyRoom is null or "boss") foreach (string e in Bosses) _fights.Add((e, "boss", d));
            }
        }

        public Report Score(BotTuning tuning)
        {
            var bot = new BasicBot { Tuning = tuning, NodeBudget = int.Parse(Environment.GetEnvironmentVariable("BOT_NODES") ?? "30"), BranchWidth = int.Parse(Environment.GetEnvironmentVariable("BOT_WIDTH") ?? "3"), MaxDepth = int.Parse(Environment.GetEnvironmentVariable("BOT_DEPTH") ?? "6") };
            var losses = new double[_fights.Count];
            Parallel.For(0, _fights.Count, f =>
            {
                var (id, room, deckIndex) = _fights[f];
                EncounterDef encounter = _data.Encounters.Get(id);
                int stakes = room switch { "boss" => 2, "elite" => 1, _ => 0 };
                double total = 0;
                for (int i = 0; i < _n; i++)
                {
                    ulong seed = SimRng.Mix(17, (ulong)i);
                    string[] lineup = encounter.Generate(new SimRng(SimRng.Mix(seed, 1)));
                    FightResult r = FightSimulator.Run(_decks[deckIndex], 80, 80, lineup.Select(_data.Monsters.Get), 10, seed, bot: bot, altStarts: encounter.AltStarts, services: _data.Services, stakes: stakes);
                    total += r.HpLost + (r.Won ? 0 : 40);
                }
                losses[f] = total / _n;
            });
            double Mean(string room) => Enumerable.Range(0, _fights.Count).Where(i => _fights[i].Room == room).DefaultIfEmpty(-1).Average(i => i < 0 ? 0 : losses[i]);
            double normal = Mean("normal"), elite = Mean("elite"), boss = Mean("boss");
            var perDeck = Decks.Select((d, i) => (d.Name, Enumerable.Range(0, _fights.Count).Where(k => _fights[k].Deck == i).Average(k => losses[k]))).ToList();
            return new Report(_onlyRoom == "normal" ? normal : _onlyRoom == "elite" ? elite : _onlyRoom == "boss" ? boss : 3 * normal + 2 * elite + boss, normal, elite, boss, perDeck);
        }
    }
}
