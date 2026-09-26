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
