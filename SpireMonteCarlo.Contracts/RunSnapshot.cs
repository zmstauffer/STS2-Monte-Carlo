namespace SpireMonteCarlo.Contracts;

/// <summary>Everything the advisor app needs to evaluate one run-level decision. Plain data, no game types.</summary>
public sealed class RunSnapshot
{
    /// <summary>Bumped on any breaking change to this format.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string GameVersion { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>One of <see cref="DecisionType"/>.</summary>
    public string Decision { get; set; } = DecisionType.Unknown;

    /// <summary>Set for event decisions (EventModel id entry).</summary>
    public string? EventId { get; set; }

    public RunInfo Run { get; set; } = new();
    public List<CardSnapshot> Deck { get; set; } = new();
    public List<string> Relics { get; set; } = new();
    public List<PotionSnapshot> Potions { get; set; } = new();

    /// <summary>What the player is choosing between. Empty for decisions that offer nothing (map, rest site).</summary>
    public Offer Offer { get; set; } = new();

    /// <summary>The current act's map. Null when it couldn't be read.</summary>
    public MapSnapshot? Map { get; set; }
}

public static class DecisionType
{
    public const string Unknown = "unknown";
    public const string CardReward = "card_reward";
    public const string RelicReward = "relic_reward";
    public const string Shop = "shop";
    public const string Map = "map";
    public const string Event = "event";
    public const string RestSite = "rest_site";
    public const string CardUpgrade = "card_upgrade";
    public const string Combat = "combat";
}

public sealed class RunInfo
{
    /// <summary>Lowercase character id: ironclad, silent, defect, regent, necrobinder.</summary>
    public string Character { get; set; } = "";
    public int Ascension { get; set; }
    public string Seed { get; set; } = "";
    /// <summary>1-based.</summary>
    public int Act { get; set; }
    public int TotalFloor { get; set; }
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }
    public int Gold { get; set; }
}

public sealed class CardSnapshot
{
    /// <summary>Game id, UPPER_SNAKE_CASE (BODY_SLAM).</summary>
    public string Id { get; set; } = "";
    public bool Upgraded { get; set; }
    /// <summary>Gold price when the card is for sale; 0 otherwise.</summary>
    public int Price { get; set; }
}

public sealed class PotionSnapshot
{
    public string Id { get; set; } = "";
    public int Price { get; set; }
}

public sealed class RelicOffer
{
    public string Id { get; set; } = "";
    public int Price { get; set; }
}

public sealed class Offer
{
    public List<CardSnapshot> Cards { get; set; } = new();
    public List<RelicOffer> Relics { get; set; } = new();
    public List<PotionSnapshot> Potions { get; set; } = new();
}

public readonly record struct MapCoordinate(int Col, int Row);

public sealed class MapPointSnapshot
{
    public int Col { get; set; }
    public int Row { get; set; }
    /// <summary>Unknown, Shop, Treasure, RestSite, Monster, Elite, Boss, Ancient.</summary>
    public string Type { get; set; } = "";
    public List<MapCoordinate> Children { get; set; } = new();
}

public sealed class MapSnapshot
{
    public int Columns { get; set; }
    public int Rows { get; set; }
    public List<MapPointSnapshot> Points { get; set; } = new();
    /// <summary>The node the player is standing on; null before the first move of an act.</summary>
    public MapCoordinate? Current { get; set; }
    public List<MapCoordinate> Visited { get; set; } = new();
    public MapCoordinate? Boss { get; set; }
}
