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

    // Monster mechanics (rules read from the decompiled power classes).
    /// <summary>Takes +10% damage for each card the player has played this turn.</summary>
    Slow,
    /// <summary>Gains Strength at the end of every enemy turn (no skipped first trigger, unlike Ritual).</summary>
    Territorial,
    /// <summary>Does nothing for a few turns; woken early by unblocked damage. Only Lagavulin uses it.</summary>
    Asleep,
    /// <summary>A secondary enemy: the fight ends when every non-Minion enemy is dead.</summary>
    Minion,
    /// <summary>After the first unblocked card attack each turn, gains block.</summary>
    Skittish,
    /// <summary>When it dies, spawns 4 Wrigglers.</summary>
    Infested,
    /// <summary>Takes at most this much HP loss per player turn.</summary>
    HardenedShell,
    /// <summary>Each hit that gets through block deals only 1; each such hit uses up one stack.</summary>
    Slippery,
    /// <summary>Every hit deals at most 1 damage; one stack ticks down each enemy turn.</summary>
    Intangible,
    /// <summary>Stunned (and this power removed) once its HP falls to this value or lower.</summary>
    Shriek,
    /// <summary>Adds this much to the next attack, then is used up.</summary>
    Vigor,
    /// <summary>Loses its Strength and is stunned once its HP falls to this value or lower.</summary>
    Plow,
    /// <summary>On the player: only one card can be played this turn.</summary>
    Ringing,
    /// <summary>Keeps the fight going when its owner dies, then explodes for this much damage.</summary>
    SteamEruption,

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
        kind is PowerKind.Vulnerable or PowerKind.Weak or PowerKind.Frail or PowerKind.Poison or PowerKind.Ringing;

    /// <summary>Debuffs that count down one step each time the enemy side finishes its turn.</summary>
    public static bool TicksDownAfterEnemyTurn(PowerKind kind) =>
        kind is PowerKind.Vulnerable or PowerKind.Weak or PowerKind.Frail or PowerKind.Intangible;
}
