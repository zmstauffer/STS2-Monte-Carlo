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

    // Player powers from Ironclad cards (rules read from the decompiled power classes).
    /// <summary>Skills cost 0 and are exhausted when played.</summary>
    Corruption,
    /// <summary>At the start of each turn, lose CrimsonSelfDamage HP and gain this much Block.</summary>
    CrimsonMantle,
    CrimsonSelfDamage,
    /// <summary>Vulnerable enemies take this many percent more damage from the player's attacks.</summary>
    Cruelty,
    /// <summary>Draw this many cards whenever a card is exhausted (ethereal exhausts draw at end of turn).</summary>
    DarkEmbrace,
    DarkEmbraceEthereal,
    /// <summary>Gain this much Strength at the start of every turn.</summary>
    DemonForm,
    /// <summary>Gain this much Block whenever a card is exhausted.</summary>
    FeelNoPain,
    /// <summary>Drawing a Strike plays it against a random enemy.</summary>
    Hellraiser,
    /// <summary>Lose InfernoSelfDamage HP at the start of each turn; deal this much to all enemies whenever you lose HP on your turn.</summary>
    Inferno,
    InfernoSelfDamage,
    /// <summary>Whenever you gain Block, deal this much to a random enemy.</summary>
    Juggernaut,
    /// <summary>The third Attack played each turn is copied into hand (this many copies).</summary>
    Juggling,
    /// <summary>Extra maximum energy every turn.</summary>
    Pyre,
    /// <summary>Gain this much Block for each Attack played this turn.</summary>
    Rage,
    /// <summary>Gain this much Strength whenever you lose HP on your own turn.</summary>
    Rupture,
    /// <summary>At the end of your turn, this many random Attacks in hand are played.</summary>
    Stampede,
    /// <summary>The first this-many Block gains from cards each turn are doubled.</summary>
    Unmovable,
    /// <summary>Draw this many cards whenever you apply Vulnerable.</summary>
    Vicious,
    /// <summary>At the start of your turn, put this many random Attacks from the discard pile into hand, upgraded.</summary>
    Aggression,
    /// <summary>Attacks from Vulnerable enemies deal half damage; ticks down each enemy turn.</summary>
    Colossus,
    /// <summary>Deals this much damage back to every enemy that attacks you; gone after the enemy turn.</summary>
    FlameBarrier,
    /// <summary>The next this-many Attacks are played twice.</summary>
    OneTwoPunch,
    /// <summary>No drawing from cards for the rest of the turn.</summary>
    NoDraw,
    /// <summary>The next this-many Attacks cost 0.</summary>
    FreeAttack,
    /// <summary>Strength gained this turn only (Setup Strike); removed at the end of the player's turn.</summary>
    TempStrength,
    /// <summary>Dexterity gained this turn only (Speed Potion); removed at the end of the player's turn.</summary>
    TempDexterity,
    /// <summary>Strength lost until the end of that creature's next turn (Mangle).</summary>
    TempStrengthDown,

    // Monster powers (rules read from the decompiled power classes; see Combat.Monsters.cs).
    /// <summary>When another monster dies: skips its next move and gains this much Strength (Corpse Slug).</summary>
    Ravenous,
    /// <summary>Gains this much Strength for each of its attack hits that gets through block (Fossil Stalker).</summary>
    Suck,
    /// <summary>On the player: takes this much damage at the end of each of their turns, until the applier dies.</summary>
    Constrict,
    /// <summary>On the player: Attacks cost this much more energy this turn.</summary>
    Tangled,
    /// <summary>On the player: once a Skill has been played in a turn, no more Skills can be played that turn.</summary>
    Smoggy,
    /// <summary>On the player: attacks deal 30% less damage until the applier dies (negative amount: no end).</summary>
    Shrink,
    /// <summary>Killed: heals to full and returns instead (Eye With Teeth).</summary>
    Illusion,
    /// <summary>Killed: two gremlins appear (Gremlin Merc).</summary>
    Surprise,
    /// <summary>Gains this much Strength whenever the player plays a Skill.</summary>
    Enrage,
    /// <summary>The first time it takes an attack hit, gains this much Block and loses the power.</summary>
    CurlUp,
    /// <summary>Takes 50% less attack damage; after this many hits it is stunned.</summary>
    Flutter,
    EscapeArtist,
    /// <summary>Counts down each turn; hatches at zero.</summary>
    Hatch,
    /// <summary>Gains this much Strength at the end of every enemy turn.</summary>
    HighVoltage,
    Hex,
    Galvanic,
    /// <summary>Stunned when its attack is fully blocked.</summary>
    Imbalanced,
    /// <summary>Each unblocked attack hit costs the player this much maximum HP.</summary>
    PaperCuts,
    /// <summary>Each unblocked attack hit adds this many Wounds to the player's discard pile.</summary>
    PainfulStabs,
    Nemesis,
    Rampart,
    PersonalHive,
    PossessStrength,
    PossessSpeed,
    Reattach,
    Slumber,
    Soar,
    Stock,
    Tender,
    Burrowed,
    Surrounded,
    BackAttackLeft,
    BackAttackRight,
    CrabRage,
    Adaptable,
    VitalSpark,
    BattlewornDummyTimeLimit,
    ChainsOfBinding,
    HardToKill,

    /// <summary>On the player: the Insatiable's sandpit counts down each enemy turn and kills the player at 0; Frantic Escape adds 1.</summary>
    Sandpit,
    /// <summary>On the player (Knowledge Demon's curse): take this much damage at the end of each of your turns.</summary>
    Disintegration,
    /// <summary>On the player: each attack hit does this much more damage until the enemy turn ends (Infested Prism's Vital Spark).</summary>
    Tainted,

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
        kind is PowerKind.Vulnerable or PowerKind.Weak or PowerKind.Frail or PowerKind.Poison or PowerKind.Ringing
            or PowerKind.NoDraw or PowerKind.TempStrengthDown
            or PowerKind.Constrict or PowerKind.Tangled or PowerKind.Smoggy or PowerKind.Shrink or PowerKind.Hex
            or PowerKind.Surrounded or PowerKind.ChainsOfBinding or PowerKind.Tender or PowerKind.Imbalanced or PowerKind.Slow
            or PowerKind.Sandpit or PowerKind.Tainted;

    /// <summary>Debuffs that count down one step each time the enemy side finishes its turn.</summary>
    public static bool TicksDownAfterEnemyTurn(PowerKind kind) =>
        kind is PowerKind.Vulnerable or PowerKind.Weak or PowerKind.Frail or PowerKind.Intangible;
}
