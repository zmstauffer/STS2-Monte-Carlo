namespace SpireMonteCarlo.Sim;

public enum PotionRarity { Common, Uncommon, Rare }

/// <summary>
/// A potion the simulator can use, written from the decompiled potion classes. A potion runs its effects like a card would,
/// but costs no energy, is not a card played (no Slow, Rage, or play limits), and its damage and block ignore powers
/// (the game marks them Unpowered).
/// </summary>
public sealed class PotionDef
{
    public required string Id { get; init; }
    public PotionRarity Rarity { get; init; }
    public Effect[] Effects { get; init; } = Array.Empty<Effect>();

    /// <summary>The potion is aimed at one enemy.</summary>
    public bool NeedsTarget { get; init; }

    /// <summary>Works between fights too (Blood Potion, Fruit Juice).</summary>
    public bool AnyTime { get; init; }

    /// <summary>Triggers by itself when the player would die (Fairy in a Bottle); never used by choice.</summary>
    public bool Automatic { get; init; }

    private CardDef? _card;

    /// <summary>The effects wrapped as a card, which is what the combat's effect runner works on.</summary>
    internal CardDef AsCard => _card ??= new CardDef { Id = Id, Kind = CardKind.Skill, Cost = 0, Effects = Effects };
}

/// <summary>The potions that are modelled. Numbers come from each potion's canonical vars in the decompiled game (v0.111).</summary>
public static class PotionLibrary
{
    private static readonly Dictionary<string, PotionDef> ById = Build().ToDictionary(p => p.Id);

    public static IReadOnlyCollection<PotionDef> All => ById.Values;

    public static PotionDef? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>How many potions the Ironclad's reward pool holds per rarity (shared + Ironclad, from the Codex export): only some are modelled.</summary>
    private const int PoolSizePerRarity = 16;

    private static readonly PotionDef[][] ModelledByRarity =
        Enum.GetValues<PotionRarity>().Select(r => ById.Values.Where(p => p.Rarity == r).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray()).ToArray();

    /// <summary>
    /// A random potion the way the game rolls one (10% rare, 25% uncommon, else common; then uniform within the rarity).
    /// Returns null when the roll lands on a potion the simulator doesn't model, which is treated as no potion at all.
    /// </summary>
    public static PotionDef? Roll(SimRng rng)
    {
        double r = rng.NextDouble();
        PotionRarity rarity = r <= 0.1 ? PotionRarity.Rare : r <= 0.35 ? PotionRarity.Uncommon : PotionRarity.Common;
        int pick = rng.Next(PoolSizePerRarity);
        PotionDef[] modelled = ModelledByRarity[(int)rarity];
        return pick < modelled.Length ? modelled[pick] : null;
    }

    private static PotionDef P(string id, PotionRarity rarity, Effect[] effects, bool target = false, bool anyTime = false, bool automatic = false) =>
        new() { Id = id, Rarity = rarity, Effects = effects, NeedsTarget = target, AnyTime = anyTime, Automatic = automatic };

    private static Effect Buff(PowerKind power, int amount) => new(EffectOp.BuffSelf, amount, Power: power);
    private static Effect Debuff(PowerKind power, int amount) => new(EffectOp.DebuffEnemy, amount, Power: power);

    private static IEnumerable<PotionDef> Build()
    {
        const PotionRarity C = PotionRarity.Common, U = PotionRarity.Uncommon, R = PotionRarity.Rare;
        yield return P("FIRE_POTION", C, new[] { new Effect(EffectOp.DamageFlat, 20) }, target: true);
        yield return P("EXPLOSIVE_AMPOULE", C, new[] { new Effect(EffectOp.DamageAllFlat, 10) });
        yield return P("BLOCK_POTION", C, new[] { new Effect(EffectOp.BlockFlat, 12) });
        yield return P("STRENGTH_POTION", C, new[] { Buff(PowerKind.Strength, 2) });
        yield return P("DEXTERITY_POTION", C, new[] { Buff(PowerKind.Dexterity, 2) });
        yield return P("FLEX_POTION", C, new[] { Buff(PowerKind.TempStrength, 5) });
        yield return P("SPEED_POTION", C, new[] { Buff(PowerKind.TempDexterity, 5) });
        yield return P("ENERGY_POTION", C, new[] { new Effect(EffectOp.Energy, 2) });
        yield return P("SWIFT_POTION", C, new[] { new Effect(EffectOp.Draw, 3) });
        yield return P("VULNERABLE_POTION", C, new[] { Debuff(PowerKind.Vulnerable, 3) }, target: true);
        yield return P("WEAK_POTION", C, new[] { Debuff(PowerKind.Weak, 3) }, target: true);
        yield return P("BLOOD_POTION", C, new[] { new Effect(EffectOp.HealPercent, 20) }, anyTime: true);
        yield return P("ATTACK_POTION", C, new[] { new Effect(EffectOp.GenerateFreeAttack, 1) });

        yield return P("HEART_OF_IRON", U, new[] { Buff(PowerKind.Plating, 7) });
        yield return P("FORTIFIER", U, new[] { new Effect(EffectOp.DoubleBlock, 0) });
        yield return P("FYSH_OIL", U, new[] { Buff(PowerKind.Strength, 1), Buff(PowerKind.Dexterity, 1) });
        yield return P("CURE_ALL", U, new[] { new Effect(EffectOp.Energy, 1), new Effect(EffectOp.Draw, 2) });
        yield return P("BLESSING_OF_THE_FORGE", U, new[] { new Effect(EffectOp.UpgradeAllInHand, 0) });

        yield return P("FAIRY_IN_A_BOTTLE", R, Array.Empty<Effect>(), automatic: true);
        yield return P("FRUIT_JUICE", R, new[] { new Effect(EffectOp.GainMaxHp, 5) }, anyTime: true);
        yield return P("SHACKLING_POTION", R, new[] { new Effect(EffectOp.DebuffAll, 7, Power: PowerKind.TempStrengthDown) });
        yield return P("DISTILLED_CHAOS", R, new[] { new Effect(EffectOp.PlayTopOfDraw, 3) });
    }
}
