using SpireMonteCarlo.Codex;

namespace SpireMonteCarlo.Sim;

/// <summary>Everything the simulator reads from the local Codex cache, loaded once and shared read-only.</summary>
public sealed class SimData
{
    public static readonly string EloBracket = Environment.GetEnvironmentVariable("SIM_BRACKET") ?? "a10";

    public CardLibrary Cards { get; }
    public MonsterLibrary Monsters { get; }
    public EncounterLibrary Encounters { get; }
    public IReadOnlyDictionary<string, CodexCharacter> Characters { get; }

    /// <summary>What a combat needs to hand out status cards and spawn monsters mid-fight.</summary>
    public CombatServices Services { get; }

    private readonly IReadOnlyDictionary<string, CodexCard> _codexCards;
    private readonly IReadOnlyDictionary<string, CodexMetricRow> _cardMetrics;
    private readonly Dictionary<string, RewardPool> _pools = new(StringComparer.OrdinalIgnoreCase);

    public SimData(CodexCache cache)
    {
        _codexCards = cache.LoadCards();
        Cards = new CardLibrary(_codexCards, cache.LoadCardDefs());
        Monsters = new MonsterLibrary(cache.LoadMonsters(), cache.LoadMonsterAi(), cache.LoadMonsterClasses());
        Encounters = new EncounterLibrary(cache.LoadEncounters(), cache.LoadEncounterLineups());
        Characters = cache.LoadCharacters();
        _cardMetrics = cache.LoadMetrics("cards", EloBracket);
        _relics = cache.LoadRelics();
        RelicRatings = new RelicRatings(cache, _relics);
        Services = new CombatServices(
            id => Cards.Contains(id) ? Cards.Get(id, false) : null,
            id => Monsters.Contains(id) ? Monsters.Get(id) : null,
            Cards.CombatGenerationPool);
    }

    /// <summary>Real players' results with each relic by act, the relics' stand-in for Elo.</summary>
    public RelicRatings RelicRatings { get; }

    private readonly Dictionary<string, RelicPool> _relicPools = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<CodexRelic> _relics;

    /// <summary>The relics a character can find as elite and chest rewards.</summary>
    public RelicPool RelicPoolFor(string character)
    {
        lock (_relicPools)
        {
            if (!_relicPools.TryGetValue(character, out RelicPool? pool))
                _relicPools[character] = pool = new RelicPool(_relics, character);
            return pool;
        }
    }

    /// <summary>Every card id of a Codex color ("curse", "status", "event", "colorless", a character).</summary>
    public IReadOnlyList<string> CardIdsOfColor(string color) =>
        _codexCards.Values.Where(c => string.Equals(c.Color, color, StringComparison.OrdinalIgnoreCase)).Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>The cards a character can be offered as rewards, with their Elo.</summary>
    public RewardPool PoolFor(string character)
    {
        lock (_pools)
        {
            if (!_pools.TryGetValue(character, out RewardPool? pool))
                _pools[character] = pool = new RewardPool(_codexCards, _cardMetrics, character);
            return pool;
        }
    }

    /// <summary>Parses "STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH+" (a trailing + means upgraded, *N repeats) into card defs.</summary>
    public List<CardDef> ParseDeck(string spec)
    {
        var deck = new List<CardDef>();
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string id = part;
            int count = 1;
            int star = id.IndexOf('*');
            if (star >= 0) { count = int.Parse(id[(star + 1)..]); id = id[..star]; }
            bool upgraded = id.EndsWith('+');
            if (upgraded) id = id[..^1];
            CardDef def = Cards.Get(id, upgraded);
            for (int i = 0; i < count; i++) deck.Add(def);
        }
        return deck;
    }
}
