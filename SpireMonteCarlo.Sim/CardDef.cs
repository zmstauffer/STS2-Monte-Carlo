namespace SpireMonteCarlo.Sim;

public enum CardKind { Attack, Skill, Power, Status, Curse }

public enum EffectOp
{
    /// <summary>Amount damage to the chosen enemy, Hits times (Hits = -1 means X).</summary>
    Damage,
    DamageAll,
    DamageRandom,
    /// <summary>Damage equal to current Block (Body Slam).</summary>
    DamageEqualBlock,
    Block,
    Draw,
    Energy,
    LoseHp,
    DebuffEnemy,
    DebuffAll,
    DebuffSelf,
    BuffSelf,
}

public readonly record struct Effect(EffectOp Op, int Amount, int Hits = 1, PowerKind Power = PowerKind.Unsupported);

/// <summary>One card variant (base or upgraded). Immutable, so any number of simulations can share it.</summary>
public sealed class CardDef
{
    public const int XCost = -1;
    public const int Unplayable = -2;

    public required string Id { get; init; }
    public bool Upgraded { get; init; }
    public CardKind Kind { get; init; }
    public int Cost { get; init; }
    public bool Exhaust { get; init; }
    public bool Ethereal { get; init; }
    public bool Innate { get; init; }
    public bool Retain { get; init; }
    public Effect[] Effects { get; init; } = Array.Empty<Effect>();

    /// <summary>
    /// True when the card text has behavior the effect list doesn't capture (conditions, triggered powers,
    /// card generation, ...). The card still plays, but only with its straightforward effects.
    /// </summary>
    public bool Approximate { get; init; }

    public bool IsAttack => Kind == CardKind.Attack;

    public override string ToString() => Id + (Upgraded ? "+" : "");
}
