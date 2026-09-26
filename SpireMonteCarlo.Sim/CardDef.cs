namespace SpireMonteCarlo.Sim;

public enum CardKind { Attack, Skill, Power, Status, Curse }

public enum EffectOp
{
    /// <summary>Damage to the chosen enemy (Hits times; the hit count can come from a <see cref="Source"/>).</summary>
    Damage,
    DamageAll,
    /// <summary>Each hit goes to a randomly chosen living enemy.</summary>
    DamageRandom,
    Block,
    Draw,
    Energy,
    /// <summary>HP loss that ignores block (a card's own cost).</summary>
    LoseHp,
    Heal,
    GainMaxHp,
    DebuffEnemy,
    DebuffAll,
    DebuffSelf,
    BuffSelf,
    BuffEnemy,
    /// <summary>Doubles the target's stacks of Power (Molten Fist).</summary>
    DoubleDebuff,
    /// <summary>Gains Strength equal to the target's Vulnerable stacks (Dominate).</summary>
    StrengthFromVulnerable,

    // ---- pile operations ----
    ExhaustRandomFromHand,
    ExhaustChosenFromHand,
    ExhaustHand,
    /// <summary>Exhausts every non-Attack in hand, gaining Amount block for each (Second Wind).</summary>
    ExhaustNonAttacksForBlock,
    UpgradeChosenInHand,
    UpgradeAllInHand,
    /// <summary>Adds a copy of this card to the discard pile (Anger).</summary>
    CopyToDiscard,
    /// <summary>Plays the top Amount cards of the draw pile for free (Havoc, Cascade). Hits = 1 forces them to exhaust.</summary>
    PlayTopOfDraw,
    /// <summary>Puts a card of the bot's choice from the discard pile on top of the draw pile (Headbutt).</summary>
    DiscardToDrawTop,
    /// <summary>Adds random cards of the character's pool to hand: one per card exhausted by this play (Stoke). Hits = 1 upgrades them.</summary>
    GenerateForExhausted,
    /// <summary>Adds Amount random Attacks of the character's pool to hand, free this turn (Infernal Blade).</summary>
    GenerateFreeAttack,
    /// <summary>Turns every Attack in hand into a Giant Rock (Primal Force). Hits = 1 upgrades them.</summary>
    TransformAttacks,
    /// <summary>Keeps drawing until a non-Attack is drawn (Pillage).</summary>
    DrawUntilNonAttack,
    /// <summary>After an attack that kills its target: gains Amount max HP (Feed).</summary>
    GainMaxHpIfFatal,
    /// <summary>Exhausts a random Attack in hand and adds its damage to this card's damage for the rest of the combat (Thrash).</summary>
    AbsorbRandomAttack,

    // ---- potion effects: the game marks their damage and block Unpowered, so Strength, Vulnerable, Dexterity, and the like don't apply ----
    DamageFlat,
    DamageAllFlat,
    BlockFlat,
    /// <summary>Heals Amount percent of max HP.</summary>
    HealPercent,
    /// <summary>Doubles the block the player has (Fortifier).</summary>
    DoubleBlock,
}

/// <summary>Where a number that isn't fixed comes from.</summary>
public enum Source
{
    None,
    /// <summary>The value chosen when an X-cost card is played (energy spent).</summary>
    X,
    Block,
    Strength,
    /// <summary>Cards with the Strike tag anywhere in the deck during combat.</summary>
    StrikeCards,
    ExhaustPileCount,
    TargetVulnerable,
    /// <summary>1 if the target is Vulnerable, else 0 (used for hit counts: Dismantle).</summary>
    TargetIsVulnerable,
    /// <summary>Cards exhausted earlier in this same play by an ExhaustHand step (Fiend Fire, Stoke).</summary>
    HandExhausted,
    /// <summary>1 + times the player has taken unblocked damage this combat (Tear Asunder).</summary>
    TimesHurtPlusOne,
    /// <summary>1 if the player lost HP this turn (Spite).</summary>
    LostHpThisTurn,
    /// <summary>1 if any card was exhausted this turn (Evil Eye, Forgotten Ritual).</summary>
    ExhaustedThisTurn,
    /// <summary>1 if the exhaust pile holds at least <c>Arg</c> cards (Pact's End).</summary>
    ExhaustAtLeast,
}

/// <summary>
/// One step of a card. <c>Amount</c> is the base number; when <c>AmountSource</c> is set it becomes
/// <c>Amount + Per * value(source)</c>. The hit count is <c>Hits + HitsPer * value(HitsSource)</c>.
/// <c>Arg</c> is a threshold for the sources that need one.
/// </summary>
public readonly record struct Effect(
    EffectOp Op,
    int Amount,
    int Hits = 1,
    PowerKind Power = PowerKind.Unsupported,
    Source AmountSource = Source.None,
    int Per = 1,
    Source HitsSource = Source.None,
    int HitsPer = 1,
    int Arg = 0);

/// <summary>
/// One card variant (base or upgraded). Definitions are shared and never change; a combat works on its own copies
/// (<see cref="Instantiate"/>), which is where per-copy state such as Rampage's growing damage lives.
/// </summary>
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
    public bool IsStrike { get; init; }
    public Effect[] Effects { get; init; } = Array.Empty<Effect>();

    /// <summary>Damage taken at the end of the turn if this card is still in hand (Burn, Infection, ...); blockable.</summary>
    public int EndTurnDamage { get; init; }

    /// <summary>HP lost (not blockable) at the end of the turn if this card is still in hand (Beckon).</summary>
    public int EndTurnHpLoss { get; init; }

    /// <summary>Amount added to this copy's damage each time it is played (Rampage).</summary>
    public int DamageGrowthPerPlay { get; init; }

    /// <summary>Costs 1 less this turn for each Attack played this turn (Stomp).</summary>
    public bool CheaperPerAttackPlayed { get; init; }

    /// <summary>Costs 1 more energy every time it is played, for the rest of the combat (Frantic Escape).</summary>
    public bool CostsMoreEachPlay { get; init; }

    /// <summary>When exhausted, gains Amount energy (Drum of Battle).</summary>
    public int EnergyWhenExhausted { get; init; }

    /// <summary>Plays itself at the end of the turn while in the exhaust pile (Howl from Beyond).</summary>
    public bool PlaysFromExhaustPile { get; init; }

    /// <summary>
    /// True when the card's behavior is not fully modeled. The card still plays, but only with its modeled effects.
    /// The audit lists these; the aim is for none of them to exist.
    /// </summary>
    public bool Approximate { get; init; }

    public bool IsAttack => Kind == CardKind.Attack;

    // ---- per-copy state, changed during a combat ----
    public int BonusDamage { get; set; }
    public int CostReductionThisTurn { get; set; }
    public int CostIncreaseThisCombat { get; set; }
    public bool FreeThisTurn { get; set; }
    /// <summary>Ethereal or Retain gained during this combat (Music Box's copy, Ghost Seed).</summary>
    public bool ExtraEthereal { get; set; }
    public bool ExtraRetain { get; set; }
    /// <summary>Costs 0 for the rest of the combat (Touch of Insanity, Jeweled Mask).</summary>
    public bool FreeThisCombat { get; set; }
    /// <summary>Cost set at random when drawn under Confused (-1 when not set).</summary>
    public int RandomCost { get; set; } = -1;
    /// <summary>Plays an extra time each time it is played (Soldier's Stew).</summary>
    public int Replay { get; set; }
    public bool IsEthereal => Ethereal || ExtraEthereal;
    public bool IsRetained => Retain || ExtraRetain;
    public bool UpgradedInCombat { get; set; }
    public CardDef? UpgradedForm { get; set; }

    /// <summary>A fresh copy for use in one combat.</summary>
    public CardDef Instantiate() => new()
    {
        Id = Id, Upgraded = Upgraded, Kind = Kind, Cost = Cost, Exhaust = Exhaust, Ethereal = Ethereal, Innate = Innate,
        Retain = Retain, IsStrike = IsStrike, Effects = Effects, EndTurnDamage = EndTurnDamage, EndTurnHpLoss = EndTurnHpLoss,
        DamageGrowthPerPlay = DamageGrowthPerPlay, CheaperPerAttackPlayed = CheaperPerAttackPlayed, CostsMoreEachPlay = CostsMoreEachPlay,
        EnergyWhenExhausted = EnergyWhenExhausted, PlaysFromExhaustPile = PlaysFromExhaustPile, Approximate = Approximate,
        UpgradedForm = UpgradedForm,
    };

    /// <summary>Copy of this exact card with its current in-combat state, for cloning a whole combat.</summary>
    public CardDef Copy()
    {
        CardDef c = Instantiate();
        c.BonusDamage = BonusDamage;
        c.CostReductionThisTurn = CostReductionThisTurn;
        c.CostIncreaseThisCombat = CostIncreaseThisCombat;
        c.FreeThisTurn = FreeThisTurn;
        c.ExtraEthereal = ExtraEthereal; c.ExtraRetain = ExtraRetain; c.FreeThisCombat = FreeThisCombat; c.RandomCost = RandomCost; c.Replay = Replay;
        c.UpgradedInCombat = UpgradedInCombat;
        c.Tag = Tag;
        return c;
    }

    /// <summary>Marks a card so a planner can recognise it on a cloned combat (0 = unmarked).</summary>
    public int Tag { get; set; }

    /// <summary>The cost to play this copy right now.</summary>
    public int CurrentCost => Cost < 0 ? Cost : Math.Max(0, Cost + CostIncreaseThisCombat - CostReductionThisTurn);

    public override string ToString() => Id + (Upgraded ? "+" : "");
}
