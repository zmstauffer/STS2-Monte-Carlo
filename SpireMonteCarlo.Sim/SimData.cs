using SpireMonteCarlo.Codex;

namespace SpireMonteCarlo.Sim;

/// <summary>Everything the simulator reads from the local Codex cache, loaded once and shared read-only.</summary>
public sealed class SimData
{
    public const string EloBracket = "a10";

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
        Cards = new CardLibrary(_codexCards);
        Monsters = new MonsterLibrary(cache.LoadMonsters(), cache.LoadMonsterAi());
        Encounters = new EncounterLibrary(cache.LoadEncounters(), cache.LoadEncounterLineups());
        Characters = cache.LoadCharacters();
        _cardMetrics = cache.LoadMetrics("cards", EloBracket);
        Services = new CombatServices(
            id => _codexCards.ContainsKey(id) ? Cards.Get(id, false) : null,
            id => Monsters.Contains(id) ? Monsters.Get(id) : null);
    }

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
