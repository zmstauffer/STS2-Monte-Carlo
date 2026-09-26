namespace SpireMonteCarlo.Sim;

/// <summary>The enchantments a card can carry (rules read from the decompiled enchantment classes, v0.111).</summary>
public enum Enchant
{
    None,
    /// <summary>Gain N Block when the card is played.</summary>
    Adroit,
    /// <summary>Pael's Growth: the card can be cloned at rest sites.</summary>
    Clone,
    /// <summary>Attack: 1.5x damage, lose 2 HP each play.</summary>
    Corrupted,
    /// <summary>The first play each combat happens one extra time.</summary>
    Glam,
    /// <summary>A Defend: exhausts, and each play gives one more Block than the last.</summary>
    Goopy,
    /// <summary>A Skill that is played for free at the start of the combat.</summary>
    Imbued,
    /// <summary>Applies 1 Weak when played.</summary>
    Inky,
    /// <summary>Attack: doubles damage.</summary>
    Instinct,
    /// <summary>Attack: gains N damage every time it is played.</summary>
    Momentum,
    /// <summary>Cards that gain Block gain N more.</summary>
    Nimble,
    /// <summary>Goes to the top of the draw pile whenever the piles are reshuffled.</summary>
    PerfectFit,
    /// <summary>Innate and Retain.</summary>
    RoyallyApproved,
    /// <summary>Attack: N extra damage.</summary>
    Sharp,
    /// <summary>Its cost is randomised (0 to 3) each time it is drawn.</summary>
    Slither,
    SlumberingEssence,
    /// <summary>Removes Exhaust.</summary>
    SoulsPower,
    /// <summary>The first play each combat gives N energy.</summary>
    Sown,
    /// <summary>A basic Strike or Defend that plays one extra time.</summary>
    Spiral,
    /// <summary>Retain.</summary>
    Steady,
    /// <summary>The first play each combat draws N cards.</summary>
    Swift,
    /// <summary>Costs 0, +3 damage.</summary>
    TezcatarasEmber,
    /// <summary>Attack: N extra damage on the first play only.</summary>
    Vigorous,
}

public static class Enchantments
{
    /// <summary>Whether the game lets this enchantment go on this card.</summary>
    public static bool CanEnchant(CardDef card, Enchant e)
    {
        if (card.Enchantment != Enchant.None || card.Kind is CardKind.Curse or CardKind.Status) return false;
        bool basic = card.Id.StartsWith("STRIKE_") || card.Id.StartsWith("DEFEND_");
        bool gainsBlock = card.Effects.Any(x => x.Op is EffectOp.Block or EffectOp.BlockFlat or EffectOp.DoubleBlock or EffectOp.ExhaustNonAttacksForBlock);
        return e switch
        {
            Enchant.Corrupted or Enchant.Instinct or Enchant.Momentum or Enchant.Sharp or Enchant.Vigorous => card.Kind == CardKind.Attack,
            Enchant.Imbued => card.Kind == CardKind.Skill,
            Enchant.RoyallyApproved => card.Kind is CardKind.Attack or CardKind.Skill,
            Enchant.Goopy => card.Id.StartsWith("DEFEND_"),
            Enchant.Nimble => gainsBlock,
            Enchant.Spiral => basic,
            Enchant.Slither => card.Cost >= 0,
            Enchant.SoulsPower => card.Exhaust,
            _ => card.Cost != CardDef.Unplayable,
        };
    }

    /// <summary>The card with the enchantment on it (a new definition; the original is unchanged). The keyword and cost changes an enchantment makes on being applied are made here.</summary>
    public static CardDef Apply(CardDef card, Enchant e, int amount)
    {
        var effects = card.Effects;
        return new CardDef
        {
            Id = card.Id, Upgraded = card.Upgraded, Kind = card.Kind,
            Cost = e == Enchant.TezcatarasEmber && card.Cost >= 0 ? 0 : card.Cost,
            Exhaust = e == Enchant.Goopy ? true : e == Enchant.SoulsPower ? false : card.Exhaust,
            Ethereal = card.Ethereal,
            Innate = card.Innate || e == Enchant.RoyallyApproved,
            Retain = card.Retain || e is Enchant.RoyallyApproved or Enchant.Steady,
            IsStrike = card.IsStrike, Effects = effects, EndTurnDamage = card.EndTurnDamage, EndTurnHpLoss = card.EndTurnHpLoss,
            DamageGrowthPerPlay = card.DamageGrowthPerPlay + (e == Enchant.Momentum ? amount : 0),
            CheaperPerAttackPlayed = card.CheaperPerAttackPlayed, CostsMoreEachPlay = card.CostsMoreEachPlay, EnergyWhenExhausted = card.EnergyWhenExhausted,
            PlaysFromExhaustPile = card.PlaysFromExhaustPile, Approximate = card.Approximate,
            UpgradedForm = card.UpgradedForm == null ? null : Apply(card.UpgradedForm, e, amount),
            Enchantment = e, EnchantAmount = amount,
        };
    }

    /// <summary>What a player enchants: the deck's best-liked cards among those the enchantment fits (or its worst, for a drawback).</summary>
    public static IEnumerable<int> Choose(IReadOnlyList<CardDef> deck, Enchant e, int count, RewardPool pool, bool preferBasics = false)
    {
        return Enumerable.Range(0, deck.Count).Where(i => CanEnchant(deck[i], e))
            .OrderByDescending(i => (preferBasics && (deck[i].Id.StartsWith("STRIKE_") || deck[i].Id.StartsWith("DEFEND_")) ? 1000 : 0) + pool.Elo(deck[i].Id))
            .Take(count);
    }
}
