using System.Text.Json;

namespace SpireMonteCarlo.Codex;

/// <summary>One card from the Codex export (cards.json). Only the fields the simulator uses so far.</summary>
public sealed class CodexCard
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>-1 for X-cost and unplayable cards.</summary>
    public int? Cost { get; set; }
    public bool? IsXCost { get; set; }
    public string Type { get; set; } = "";
    public string Rarity { get; set; } = "";
    /// <summary>Owning character or pool: ironclad, silent, defect, regent, necrobinder, colorless, curse, status, event, token, quest.</summary>
    public string Color { get; set; } = "";
    public List<string>? Keywords { get; set; }
    public List<string>? Tags { get; set; }

    /// <summary>Self, AnyEnemy, AllEnemies, RandomEnemy, AnyAlly, ...</summary>
    public string? Target { get; set; }
    public string? DescriptionRaw { get; set; }
    public JsonElement CardsDraw { get; set; }
    public JsonElement EnergyGain { get; set; }
    public JsonElement HpLoss { get; set; }
    public List<CodexPowerApplied>? PowersApplied { get; set; }

    /// <summary>Named numbers used by the card text (Damage, Block, Cards, ...).</summary>
    public Dictionary<string, JsonElement>? Vars { get; set; }

    /// <summary>Changes made by upgrading, keyed by lowercase var name, e.g. "damage": "+2", "cost": "-1".</summary>
    public Dictionary<string, JsonElement>? Upgrade { get; set; }

    public int? CardsDrawAmount => Number(CardsDraw);
    public int? EnergyGainAmount => Number(EnergyGain);
    public int? HpLossAmount => Number(HpLoss);

    // The export mixes numbers with strings like "+3" (a scaling amount), so keep the raw value.
    public JsonElement Damage { get; set; }
    public JsonElement Block { get; set; }
    public JsonElement HitCount { get; set; }

    public int? DamageAmount => Number(Damage);
    public int? BlockAmount => Number(Block);
    public int? Hits => Number(HitCount);

    private static int? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v) ? v : null;
}

/// <summary>One row of /runs/metrics/{cards,relics,potions}. Elo is a Bradley-Terry fit on reward screens (taken beats skipped).</summary>
public sealed class CodexMetricRow
{
    public string Id { get; set; } = "";
    public bool Upgraded { get; set; }
    public int? Score { get; set; }
    public string? Tier { get; set; }
    public double? Elo { get; set; }
    public double? WinRate { get; set; }
    public double? PickRate { get; set; }
    public int Picks { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Offered { get; set; }
    public int Picked { get; set; }
}

public sealed class CodexMetrics
{
    public string EntityType { get; set; } = "";
    public string Bracket { get; set; } = "";
    public double? BaselineWinRate { get; set; }
    public int TotalRuns { get; set; }
    public List<CodexMetricRow> Rows { get; set; } = new();
}

public sealed class CodexPowerApplied
{
    public string Power { get; set; } = "";
    public JsonElement Amount { get; set; }
    public string? PowerKey { get; set; }
    public int AmountValue => Amount.ValueKind == JsonValueKind.Number && Amount.TryGetInt32(out int v) ? v : 0;
}

public sealed class CodexMonsterPower
{
    public string PowerId { get; set; } = "";
    /// <summary>"player" or "self" (other values seen for ally-targeting moves).</summary>
    public string? Target { get; set; }
    public int? Amount { get; set; }
}

public sealed class CodexDamage
{
    public int? Normal { get; set; }
    /// <summary>Value at Deadly Enemies (A9) and above.</summary>
    public int? Ascension { get; set; }
    public int? HitCount { get; set; }
}

public sealed class CodexMove
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Attack, Defend, Buff, Debuff, Status, Summon, ... joined with " + ".</summary>
    public string Intent { get; set; } = "";
    public int? Block { get; set; }
    public CodexDamage? Damage { get; set; }
    public List<CodexMonsterPower>? Powers { get; set; }
}

public sealed class CodexBranch
{
    public string? MoveId { get; set; }
    public string? Condition { get; set; }
}

public sealed class CodexState
{
    public string Id { get; set; } = "";
    /// <summary>move, random, or conditional.</summary>
    public string Type { get; set; } = "";
    public string? MoveId { get; set; }
    public string? Next { get; set; }
    public List<CodexBranch>? Branches { get; set; }
}

public sealed class CodexAttackPattern
{
    /// <summary>cycle, random, conditional, mixed.</summary>
    public string? Type { get; set; }
    public string? InitialMove { get; set; }
    public List<CodexState>? States { get; set; }
}

public sealed class CodexInnatePower
{
    public string PowerId { get; set; } = "";
    public int Amount { get; set; }
}

public sealed class CodexMonster
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Normal, Elite, Boss.</summary>
    public string Type { get; set; } = "";
    public int? MinHp { get; set; }
    /// <summary>Null when the monster's HP is fixed at MinHp.</summary>
    public int? MaxHp { get; set; }
    /// <summary>Values at Tough Enemies (A8) and above.</summary>
    public int? MinHpAscension { get; set; }
    public int? MaxHpAscension { get; set; }
    public List<CodexMove> Moves { get; set; } = new();
    public CodexAttackPattern? AttackPattern { get; set; }
    public List<CodexInnatePower>? InnatePowers { get; set; }
}

public sealed class CodexEncounterMonster
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class CodexEncounter
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Monster, Elite, Boss.</summary>
    public string RoomType { get; set; } = "";
    public bool IsWeak { get; set; }
    /// <summary>"Act 1 - Overgrowth", "Act 1 - Underdocks", "Act 2 - Hive", "Act 3 - Glory", or null for event fights.</summary>
    public string? Act { get; set; }
    /// <summary>The monster pool; some encounters (e.g. slimes) spawn only a subset.</summary>
    public List<CodexEncounterMonster> Monsters { get; set; } = new();
}

public sealed class CodexCharacter
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int StartingHp { get; set; }
    public int StartingGold { get; set; }
    public int MaxEnergy { get; set; }
    /// <summary>Class names, e.g. "StrikeIronclad"; <see cref="DecompiledExtractor.ToSnakeCase"/> turns them into card ids.</summary>
    public List<string> StartingDeck { get; set; } = new();
    public List<string> StartingRelics { get; set; } = new();
}

public sealed class CodexCharacterStat
{
    public string Character { get; set; } = "";
    public int Total { get; set; }
    public int Fatal { get; set; }
    public double AvgDamage { get; set; }
    public double AvgTurns { get; set; }
}

/// <summary>Real-player results for one encounter from /runs/encounter-stats: fights, deaths, average HP lost and turns.</summary>
public sealed class CodexEncounterStat
{
    public string EncounterId { get; set; } = "";
    public int Act { get; set; }
    public string RoomType { get; set; } = "";
    public int Total { get; set; }
    public int Fatal { get; set; }
    public double AvgDamage { get; set; }
    public double AvgTurns { get; set; }
    public List<CodexCharacterStat> Characters { get; set; } = new();
}

public sealed class CodexEncounterStats
{
    public List<CodexEncounterStat> Encounters { get; set; } = new();
}

/// <summary>A relic from the Codex export: rarity is like "Common Relic"; pool is "shared" or a character id.</summary>
public sealed class CodexRelic
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Rarity { get; set; } = "";
    public string Pool { get; set; } = "";
    public CodexMerchantPrice? MerchantPrice { get; set; }
}

public sealed class CodexPotion
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Rarity { get; set; } = "";
    public string Pool { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class CodexMerchantPrice
{
    public int Base { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
}
