using SpireMonteCarlo.Codex;

namespace SpireMonteCarlo.Sim;

/// <summary>Everything the simulator reads from the local Codex cache, loaded once and shared read-only.</summary>
public sealed class SimData
{
    public CardLibrary Cards { get; }
    public MonsterLibrary Monsters { get; }
    public EncounterLibrary Encounters { get; }
    public IReadOnlyDictionary<string, CodexCharacter> Characters { get; }

    public SimData(CodexCache cache)
    {
        Cards = new CardLibrary(cache.LoadCards());
        Monsters = new MonsterLibrary(cache.LoadMonsters(), cache.LoadMonsterAi());
        Encounters = new EncounterLibrary(cache.LoadEncounters(), cache.LoadEncounterLineups());
        Characters = cache.LoadCharacters();
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
