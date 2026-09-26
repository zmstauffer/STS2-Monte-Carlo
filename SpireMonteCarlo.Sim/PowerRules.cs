namespace SpireMonteCarlo.Sim;

/// <summary>The powers the engine understands. Anything else in the data is counted as unsupported and ignored.</summary>
public enum PowerKind
{
    Strength,
    Dexterity,
    Vulnerable,
    Weak,
    Frail,
    Artifact,
    Plating,
    Thorns,
    Ritual,
    Poison,
    Metallicize,
    Barricade,
    Unsupported,
}

public static class PowerRules
{
    public const int Count = (int)PowerKind.Unsupported + 1;

    /// <summary>Maps a Codex power id/key ("VULNERABLE", "Vulnerable", "VULNERABLE_POWER") to a kind.</summary>
    public static PowerKind Parse(string? id)
    {
        if (string.IsNullOrEmpty(id)) return PowerKind.Unsupported;
        string key = id.Replace("_", "").Replace(" ", "");
        if (key.EndsWith("Power", StringComparison.OrdinalIgnoreCase) && key.Length > 5) key = key[..^5];
        foreach (PowerKind kind in Enum.GetValues<PowerKind>())
            if (kind != PowerKind.Unsupported && string.Equals(kind.ToString(), key, StringComparison.OrdinalIgnoreCase))
                return kind;
        return PowerKind.Unsupported;
    }

    public static bool IsDebuff(PowerKind kind) =>
        kind is PowerKind.Vulnerable or PowerKind.Weak or PowerKind.Frail or PowerKind.Poison;

    /// <summary>Debuffs that count down one step each time the enemy side finishes its turn.</summary>
    public static bool TicksDownAfterEnemyTurn(PowerKind kind) =>
        kind is PowerKind.Vulnerable or PowerKind.Weak or PowerKind.Frail;
}
