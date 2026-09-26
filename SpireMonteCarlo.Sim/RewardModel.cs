using SpireMonteCarlo.Codex;

namespace SpireMonteCarlo.Sim;

public enum CardRarity { Common, Uncommon, Rare }

public enum RewardKind { Normal, Elite }

/// <summary>
/// The game's card-reward rarity odds (checked against the decompiled CardRarityOdds): each offered card rolls a rarity
/// with a rare chance that grows until a rare shows up. Ascension 7+ (Scarcity) makes rares scarcer.
/// </summary>
public sealed class RarityOdds
{
    private readonly bool _scarcity;
    public float Offset { get; private set; }

    public RarityOdds(int ascension, float offset = -0.05f)
    {
        _scarcity = ascension >= 7;
        Offset = offset;
    }

    public RarityOdds Clone(int ascension) => new(ascension, Offset);

    public CardRarity Roll(RewardKind kind, SimRng rng)
    {
        (float rare, float uncommon) = kind == RewardKind.Elite
            ? (_scarcity ? 0.05f : 0.1f, 0.4f)
            : (_scarcity ? 0.0149f : 0.03f, 0.37f);
        float r = (float)rng.NextDouble();
        float rareThreshold = rare + Offset;
        CardRarity result = r < rareThreshold ? CardRarity.Rare : r < rareThreshold + uncommon ? CardRarity.Uncommon : CardRarity.Common;
        Offset = result == CardRarity.Rare ? -0.05f : Math.Min(Offset + (_scarcity ? 0.005f : 0.01f), 0.4f);
        return result;
    }
}

/// <summary>Cards a character can be offered as rewards, by rarity, plus their Codex Elo.</summary>
public sealed class RewardPool
{
    private readonly Dictionary<CardRarity, string[]> _byRarity;
    private readonly IReadOnlyDictionary<string, double> _elo;

    public const double DefaultElo = 1547;   // the mean Ironclad card, for cards Codex has no Elo for

    public RewardPool(IReadOnlyDictionary<string, CodexCard> cards, IReadOnlyDictionary<string, CodexMetricRow> metrics, string character)
    {
        _elo = metrics.Where(kv => kv.Value.Elo != null).ToDictionary(kv => kv.Key, kv => kv.Value.Elo!.Value);
        string color = character.ToLowerInvariant();
        _byRarity = new Dictionary<CardRarity, string[]>();
        foreach (CardRarity rarity in Enum.GetValues<CardRarity>())
            _byRarity[rarity] = cards.Values.Where(c => c.Color == color && c.Rarity == rarity.ToString()).Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    public double Elo(string cardId) => _elo.TryGetValue(cardId, out double e) ? e : DefaultElo;

    public bool HasElo(string cardId) => _elo.ContainsKey(cardId);

    /// <summary>Three different cards, each with its own rarity roll.</summary>
    public string[] GenerateOffer(RewardKind kind, RarityOdds odds, SimRng rng, int count = 3)
    {
        var offer = new List<string>(count);
        for (int guard = 0; offer.Count < count && guard < 50; guard++)
        {
            CardRarity rarity = odds.Roll(kind, rng);
            string[] pool = _byRarity[rarity];
            if (pool.Length == 0) continue;
            string card = pool[rng.Next(pool.Length)];
            if (!offer.Contains(card)) offer.Add(card);
        }
        return offer.ToArray();
    }
}

/// <summary>
/// How a typical player chooses from a card reward: a Bradley-Terry pick among the offered cards and "skip", using Codex
/// Elo (the same model Codex fits: a taken card beats the ones passed over). Skip has no Elo of its own, so it is given
/// one that grows with deck size: few skips early, more as the deck fills, and more when the offered cards are weak,
/// which matches the observed skip rates (about 25% in Act 1, 42% in Act 2, 52% in Act 3).
/// </summary>
public static class PickPolicy
{
    /// <summary>Elo of "skip" for a deck of this size. Fit to the per-act skip rates in the Codex a10 metrics.</summary>
    public static double SkipElo(int deckSize) => 1547 + 19.0 * (deckSize - 12);

    public static double Weight(double elo) => Math.Pow(10, elo / 400.0);

    /// <returns>Index into <paramref name="offer"/>, or -1 to skip.</returns>
    public static int Choose(IReadOnlyList<string> offer, RewardPool pool, int deckSize, SimRng rng)
    {
        double skipWeight = Weight(SkipElo(deckSize));
        double total = skipWeight;
        foreach (string card in offer) total += Weight(pool.Elo(card));

        double roll = rng.NextDouble() * total;
        for (int i = 0; i < offer.Count; i++)
        {
            roll -= Weight(pool.Elo(offer[i]));
            if (roll < 0) return i;
        }
        return -1;
    }

    /// <summary>Probability of skipping this particular offer (for reporting and calibration).</summary>
    public static double SkipProbability(IReadOnlyList<string> offer, RewardPool pool, int deckSize)
    {
        double skip = Weight(SkipElo(deckSize));
        return skip / (skip + offer.Sum(c => Weight(pool.Elo(c))));
    }
}
