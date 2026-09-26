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

    /// <summary>Only ever handed out by something else (Petrified Toad); never dropped as a reward.</summary>
    public bool Token { get; init; }

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

    private static readonly PotionDef[][] ModelledByRarity =
        Enum.GetValues<PotionRarity>().Select(r => ById.Values.Where(p => p.Rarity == r && !p.Token).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray()).ToArray();

    /// <summary>A random potion the way the game rolls one (10% rare, 25% uncommon, else common; then uniform within the rarity).</summary>
    public static PotionDef? Roll(SimRng rng)
    {
        double r = rng.NextDouble();
        PotionRarity rarity = r <= 0.1 ? PotionRarity.Rare : r <= 0.35 ? PotionRarity.Uncommon : PotionRarity.Common;
        PotionDef[] pool = ModelledByRarity[(int)rarity];
        return pool.Length == 0 ? null : pool[rng.Next(pool.Length)];
    }

    private static PotionDef P(string id, PotionRarity rarity, Effect[] effects, bool target = false, bool anyTime = false, bool automatic = false, bool token = false) =>
        new() { Id = id, Rarity = rarity, Effects = effects, NeedsTarget = target, AnyTime = anyTime, Automatic = automatic, Token = token };

    private static Effect Special(SpecialEffect kind, int amount = 0) => new(EffectOp.Special, amount, Arg: (int)kind);
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

        yield return P("COLORLESS_POTION", C, new[] { Special(SpecialEffect.GenerateColorless) });
        yield return P("SKILL_POTION", C, new[] { Special(SpecialEffect.GenerateSkill) });
        yield return P("POWER_POTION", C, new[] { Special(SpecialEffect.GeneratePower) });
        yield return P("POTION_SHAPED_ROCK", C, new[] { new Effect(EffectOp.DamageFlat, 15) }, target: true, token: true);

        yield return P("HEART_OF_IRON", U, new[] { Buff(PowerKind.Plating, 7) });
        yield return P("FORTIFIER", U, new[] { new Effect(EffectOp.DoubleBlock, 0) });
        yield return P("FYSH_OIL", U, new[] { Buff(PowerKind.Strength, 1), Buff(PowerKind.Dexterity, 1) });
        yield return P("CURE_ALL", U, new[] { new Effect(EffectOp.Energy, 1), new Effect(EffectOp.Draw, 2) });
        yield return P("BLESSING_OF_THE_FORGE", U, new[] { new Effect(EffectOp.UpgradeAllInHand, 0) });

        yield return P("ASHWATER", U, new[] { Special(SpecialEffect.Ashwater) });
        yield return P("CLARITY", U, new[] { Special(SpecialEffect.Clarity) });
        yield return P("DUPLICATOR", U, new[] { Special(SpecialEffect.Duplicator) });
        yield return P("GAMBLERS_BREW", U, new[] { Special(SpecialEffect.GamblersBrew) });
        yield return P("LIQUID_BRONZE", U, new[] { Buff(PowerKind.Thorns, 3) });
        yield return P("POTION_OF_BINDING", U, new[] { Debuff(PowerKind.Weak, 1) with { Op = EffectOp.DebuffAll }, Debuff(PowerKind.Vulnerable, 1) with { Op = EffectOp.DebuffAll } });
        yield return P("POWDERED_DEMISE", U, new[] { Debuff(PowerKind.Demise, 9) }, target: true);
        yield return P("RADIANT_TINCTURE", U, new[] { Special(SpecialEffect.RadiantTincture) });
        yield return P("REGEN_POTION", U, new[] { Buff(PowerKind.Regen, 5) });
        yield return P("STABLE_SERUM", U, new[] { Special(SpecialEffect.StableSerum, 2) });
        yield return P("TOUCH_OF_INSANITY", U, new[] { Special(SpecialEffect.TouchOfInsanity) });

        yield return P("BEETLE_JUICE", R, new[] { Debuff(PowerKind.Shrink, 4) }, target: true);
        yield return P("BOTTLED_POTENTIAL", R, new[] { Special(SpecialEffect.BottledPotential, 5) });
        yield return P("DROPLET_OF_PRECOGNITION", R, new[] { Special(SpecialEffect.DropletOfPrecognition) });
        yield return P("ENTROPIC_BREW", R, new[] { Special(SpecialEffect.EntropicBrew) });
        yield return P("GIGANTIFICATION_POTION", R, new[] { Special(SpecialEffect.Gigantification) });
        yield return P("LIQUID_MEMORIES", R, new[] { Special(SpecialEffect.LiquidMemories) });
        yield return P("LUCKY_TONIC", R, new[] { Buff(PowerKind.Buffer, 1) });
        yield return P("MAZALETHS_GIFT", R, new[] { Buff(PowerKind.Ritual, 1) });
        yield return P("OROBIC_ACID", R, new[] { Special(SpecialEffect.OrobicAcid) });
        yield return P("SHIP_IN_A_BOTTLE", R, new[] { Special(SpecialEffect.ShipInABottle, 10) });
        yield return P("SNECKO_OIL", R, new[] { Special(SpecialEffect.SneckoOil, 7) });
        yield return P("SOLDIERS_STEW", R, new[] { Special(SpecialEffect.SoldiersStew) });

        // Event potions: never dropped as rewards.
        yield return P("FOUL_POTION", C, new[] { Special(SpecialEffect.FoulPotion, 12) }, token: true);
        yield return P("GLOWWATER_POTION", C, new[] { Special(SpecialEffect.Glowwater, 10) }, token: true);

        yield return P("FAIRY_IN_A_BOTTLE", R, Array.Empty<Effect>(), automatic: true);
        yield return P("FRUIT_JUICE", R, new[] { new Effect(EffectOp.GainMaxHp, 5) }, anyTime: true);
        yield return P("SHACKLING_POTION", R, new[] { new Effect(EffectOp.DebuffAll, 7, Power: PowerKind.TempStrengthDown) });
        yield return P("DISTILLED_CHAOS", R, new[] { new Effect(EffectOp.PlayTopOfDraw, 3) });
    }
}
