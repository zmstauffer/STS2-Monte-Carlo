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
    private readonly Dictionary<(string Type, CardRarity Rarity), string[]> _byTypeAndRarity = new();
    private readonly Dictionary<CardRarity, string[]> _colorless = new();
    private readonly Dictionary<string, CardRarity> _rarityOf = new();
    private readonly HashSet<string> _colorlessIds = new();

    public const double DefaultElo = 1547;   // the mean Ironclad card, for cards Codex has no Elo for

    /// <summary>The character this pool belongs to (lower case: "ironclad").</summary>
    public string Character { get; }

    public RewardPool(IReadOnlyDictionary<string, CodexCard> cards, IReadOnlyDictionary<string, CodexMetricRow> metrics, string character)
    {
        Character = character.ToLowerInvariant();
        _elo = metrics.Where(kv => kv.Value.Elo != null).ToDictionary(kv => kv.Key, kv => kv.Value.Elo!.Value);
        string color = character.ToLowerInvariant();
        _byRarity = new Dictionary<CardRarity, string[]>();
        foreach (CardRarity rarity in Enum.GetValues<CardRarity>())
            _byRarity[rarity] = cards.Values.Where(c => c.Color == color && c.Rarity == rarity.ToString()).Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        foreach (CodexCard c in cards.Values)
            if ((c.Color == color || c.Color == "colorless") && Enum.TryParse(c.Rarity, out CardRarity parsed))
            {
                _rarityOf[c.Id] = parsed;
                if (c.Color == "colorless") _colorlessIds.Add(c.Id);
            }
        foreach (CardRarity rarity in Enum.GetValues<CardRarity>())
        {
            foreach (string type in new[] { "Attack", "Skill", "Power" })
                _byTypeAndRarity[(type, rarity)] = cards.Values.Where(c => c.Color == color && c.Type == type && c.Rarity == rarity.ToString()).Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            _colorless[rarity] = cards.Values.Where(c => c.Color == "colorless" && c.Rarity == rarity.ToString()).Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>A merchant's card of a given type: the rarity is rolled first, falling back to a lower rarity when the character has none of that type (Ironclad has no common Powers).</summary>
    public string? RollShopCard(string type, RarityOdds odds, SimRng rng, ICollection<string> exclude)
    {
        CardRarity rarity = odds.Roll(RewardKind.Normal, rng);
        for (int r = (int)rarity; r >= 0; r--)
        {
            var candidates = _byTypeAndRarity[(type, (CardRarity)r)].Where(id => !exclude.Contains(id)).ToList();
            if (candidates.Count > 0) return candidates[rng.Next(candidates.Count)];
        }
        for (int r = (int)rarity + 1; r <= (int)CardRarity.Rare; r++)
        {
            var candidates = _byTypeAndRarity[(type, (CardRarity)r)].Where(id => !exclude.Contains(id)).ToList();
            if (candidates.Count > 0) return candidates[rng.Next(candidates.Count)];
        }
        return null;
    }

    /// <summary>A random card of the character's own pool with this rarity, avoiding the excluded ones.</summary>
    public string? RollClass(CardRarity rarity, SimRng rng, ICollection<string>? exclude = null)
    {
        var candidates = _byRarity[rarity].Where(id => exclude == null || !exclude.Contains(id)).ToList();
        return candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
    }

    /// <summary>A random Power of the character's pool, whatever its rarity (Lasting Candy's extra card).</summary>
    public string? RollPower(SimRng rng, ICollection<string> exclude)
    {
        var candidates = Enum.GetValues<CardRarity>().SelectMany(r => _byTypeAndRarity[("Power", r)]).Where(id => !exclude.Contains(id)).ToList();
        return candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
    }

    /// <summary>Like <see cref="GenerateOffer"/> but colorless cards of the rolled rarity can be drawn too (Dingy Rug).</summary>
    public string[] GenerateOfferWithColorless(RewardKind kind, RarityOdds odds, SimRng rng, int count = 3)
    {
        var offer = new List<string>(count);
        for (int guard = 0; offer.Count < count && guard < 50; guard++)
        {
            CardRarity rarity = odds.Roll(kind, rng);
            string[] pool = _byRarity[rarity].Concat(_colorless[rarity]).ToArray();
            if (pool.Length == 0) continue;
            string card = pool[rng.Next(pool.Length)];
            if (!offer.Contains(card)) offer.Add(card);
        }
        return offer.ToArray();
    }

    public bool IsColorless(string cardId) => _colorlessIds.Contains(cardId);

    /// <summary>The card's rarity for pricing (Common for anything the pool doesn't list).</summary>
    public CardRarity RarityOf(string cardId) => _rarityOf.GetValueOrDefault(cardId, CardRarity.Common);

    /// <summary>A merchant's colorless card of the given rarity (the shop always has one uncommon and one rare).</summary>
    public string? RollColorless(CardRarity rarity, SimRng rng, ICollection<string> exclude)
    {
        var candidates = _colorless[rarity].Where(id => !exclude.Contains(id)).ToList();
        return candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
    }

    /// <summary>The character's own cards of one rarity (the reward pool).</summary>
    public IReadOnlyList<string> OfRarity(CardRarity rarity) => _byRarity[rarity];

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
