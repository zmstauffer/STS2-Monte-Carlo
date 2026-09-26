using System.Diagnostics;
using SpireMonteCarlo.Sim;

namespace SpireMonteCarlo.Advisor;

/// <summary>
/// <c>sim cardvalue</c>: how much each of the character's cards helps the bot in fights, next to how real players rate it (Codex Elo).
/// For every card in the reward pool it adds one copy to a base deck and replays the same seeded fights (normal fights, elites and
/// bosses of Act 1) with and without it, reporting the HP saved per fight. Where the bot gets much less out of a card than players
/// rate it, either the card's value comes later in a run (the advisor's long-term term covers that) or the bot plays it badly
/// (Bloodletting was a dead card because the search never tried it); this is the list to check.
/// </summary>
public static class CardValueCommand
{
    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static int Run(string[] args, SimData data)
    {
        int n = int.Parse(Option(args, "--n") ?? "40");
        int hp = int.Parse(Option(args, "--hp") ?? "200");
        string baseName = Option(args, "--deck") ?? "mid";
        string character = Option(args, "--character") ?? "ironclad";
        string baseSpec = TuneCommand.Decks.FirstOrDefault(d => d.Name == baseName).Deck ?? baseName;
        List<CardDef> baseDeck = data.ParseDeck(baseSpec);
        RewardPool pool = data.PoolFor(character);

        var encounters = new List<(string Id, string Room)>();
        encounters.AddRange(TuneCommand.Normals.Where(e => !e.EndsWith("_WEAK") && data.Encounters.Contains(e)).Select(e => (e, "normal")));
        encounters.AddRange(TuneCommand.Elites.Select(e => (e, "elite")));
        encounters.AddRange(TuneCommand.Bosses.Select(e => (e, "boss")));

        var cards = Enum.GetValues<CardRarity>().SelectMany(r => pool.OfRarity(r).Select(id => (Id: id, Rarity: r))).ToList();
        // --cards A,B limits the list (any ids, e.g. an unplayable curse as the "dead card" reference).
        if (Option(args, "--cards") is { } only)
            cards = only.Split(',').Select(id => (Id: id.Trim().ToUpperInvariant(), Rarity: pool.RarityOf(id.Trim().ToUpperInvariant()))).ToList();
        // An unplayable curse with no effect: what a card that does nothing costs. A card scoring well below it is being misplayed.
        if (!cards.Any(c => c.Id == "INJURY")) cards.Add(("INJURY", CardRarity.Common));
        var sw = Stopwatch.StartNew();
        var bot = new BasicBot();

        // Loss per (deck variant, encounter): variant 0 is the base deck, variant k is the base deck plus card k-1.
        var loss = new double[cards.Count + 1, encounters.Count];
        Parallel.For(0, (cards.Count + 1) * encounters.Count, job =>
        {
            int variant = job / encounters.Count, e = job % encounters.Count;
            List<CardDef> deck = variant == 0 ? baseDeck : baseDeck.Append(data.Cards.Get(cards[variant - 1].Id, false)).ToList();
            EncounterDef encounter = data.Encounters.Get(encounters[e].Id);
            int stakes = encounters[e].Room switch { "boss" => 2, "elite" => 1, _ => 0 };
            double total = 0;
            for (int i = 0; i < n; i++)
            {
                ulong seed = SimRng.Mix(4242, (ulong)(e * 100003 + i));
                string[] lineup = encounter.Generate(new SimRng(SimRng.Mix(seed, 1)));
                FightResult r = FightSimulator.Run(deck, hp, hp, lineup.Select(data.Monsters.Get), 10, seed, bot: bot, altStarts: encounter.AltStarts, services: data.Services, stakes: stakes);
                total += r.Won ? r.HpLost : hp + 40;
            }
            loss[variant, e] = total / n;
        });

        double Saved(int variant, string room) => Enumerable.Range(0, encounters.Count).Where(e => encounters[e].Room == room).Average(e => loss[0, e] - loss[variant, e]);
        var rows = cards.Select((c, k) => (c.Id, c.Rarity, Elo: pool.HasElo(c.Id) ? pool.Elo(c.Id) : double.NaN, Normal: Saved(k + 1, "normal"), Elite: Saved(k + 1, "elite"), Boss: Saved(k + 1, "boss"),
            Approx: data.Cards.Get(c.Id, false).Approximate)).ToList();
        double Overall((string Id, CardRarity Rarity, double Elo, double Normal, double Elite, double Boss, bool Approx) r) => (r.Normal + r.Elite + r.Boss) / 3;

        var withElo = rows.Where(r => !double.IsNaN(r.Elo)).ToList();
        if (withElo.Count < 3) withElo = new();
        var simRank = Ranks(withElo.Select(Overall).ToList());
        var eloRank = Ranks(withElo.Select(r => r.Elo).ToList());
        double spearman = withElo.Count >= 3 ? Pearson(simRank, eloRank) : double.NaN;

        Console.WriteLine($"Base deck worth per energy {BasicBot.EnergyWorth(baseDeck):F1} (bench reference {BasicBot.BenchEnergyWorth}), damage per energy {BasicBot.DamageRate(baseDeck, 0):F1} (bench reference {BasicBot.BenchDamageRate}).");
        Console.WriteLine($"Base deck \"{baseName}\" ({baseDeck.Count} cards), {encounters.Count} Act 1 fights x {n} seeds at {hp} HP, {sw.Elapsed.TotalSeconds:F0}s.");
        Console.WriteLine($"Base deck loses {Enumerable.Range(0, encounters.Count).Average(e => loss[0, e]):F1} HP per fight. Rank correlation of HP saved with Codex Elo: {spearman:F2}");
        var dead = rows.First(r => r.Id == "INJURY");
        Console.WriteLine($"A dead card (Injury) saves {Overall(dead):F1} per fight; cards well below that are likely misplayed: {string.Join(", ", rows.Where(r => r.Id != "INJURY" && Overall(r) < Overall(dead) - 1.5).Select(r => $"{r.Id} {Overall(r):F1}"))}");
        Console.WriteLine();
        Console.WriteLine($"{"card",-22} {"rarity",-9} {"elo",6} {"saved/fight",11} {"normal",7} {"elite",7} {"boss",7}  rank sim/elo");
        var order = withElo.Select((r, i) => (Row: r, Gap: simRank[i] - eloRank[i], SimRank: simRank[i], EloRank: eloRank[i])).OrderBy(x => x.Gap).ToList();
        foreach (var (r, gap, sr, er) in order)
            Console.WriteLine($"{r.Id,-22} {r.Rarity,-9} {r.Elo,6:F0} {Overall(r),11:F1} {r.Normal,7:F1} {r.Elite,7:F1} {r.Boss,7:F1}  {sr,3:F0}/{er,-3:F0} {(gap <= -25 ? ">> sim much higher than players" : gap >= 25 ? "<< sim much lower than players" : "")}{(r.Approx ? "  (approximate)" : "")}");
        foreach (var r in rows.Where(r => double.IsNaN(r.Elo)))
            Console.WriteLine($"{r.Id,-22} {r.Rarity,-9} {"-",6} {Overall(r),11:F1} {r.Normal,7:F1} {r.Elite,7:F1} {r.Boss,7:F1}  (no Elo)");
        return 0;
    }

    /// <summary><c>sim synergy [--character C]</c>: each reward-pool card's enabler and payoff themes (see <see cref="Synergy"/>).</summary>
    public static int Synergies(string[] args, SimData data)
    {
        RewardPool pool = data.PoolFor(Option(args, "--character") ?? "ironclad");
        foreach (CardRarity rarity in Enum.GetValues<CardRarity>())
            foreach (string id in pool.OfRarity(rarity))
            {
                CardDef c = data.Cards.Get(id, false);
                string Tags(Func<CardDef, Theme, double> f) => string.Join(" ", Enum.GetValues<Theme>().Where(t => f(c, t) > 0).Select(t => $"{t}{(f(c, t) != 1 ? f(c, t).ToString("0.#") : "")}"));
                string enables = Tags(Synergy.Enables), pays = Tags(Synergy.PaysOff);
                if (enables.Length + pays.Length > 0) Console.WriteLine($"{id,-22} {rarity,-9} feeds: {enables,-28} pays off: {pays}");
            }
        return 0;
    }

    /// <summary>1-based ranks, highest value first, ties averaged.</summary>
    private static List<double> Ranks(List<double> values)
    {
        var order = values.Select((v, i) => (v, i)).OrderByDescending(x => x.v).ToList();
        var ranks = new double[values.Count];
        for (int k = 0; k < order.Count;)
        {
            int j = k;
            while (j + 1 < order.Count && order[j + 1].v == order[k].v) j++;
            for (int m = k; m <= j; m++) ranks[order[m].i] = (k + j) / 2.0 + 1;
            k = j + 1;
        }
        return ranks.ToList();
    }

    private static double Pearson(List<double> a, List<double> b)
    {
        double ma = a.Average(), mb = b.Average();
        double cov = a.Zip(b).Sum(p => (p.First - ma) * (p.Second - mb));
        double va = a.Sum(x => (x - ma) * (x - ma)), vb = b.Sum(x => (x - mb) * (x - mb));
        return va == 0 || vb == 0 ? 0 : cov / Math.Sqrt(va * vb);
    }
}
