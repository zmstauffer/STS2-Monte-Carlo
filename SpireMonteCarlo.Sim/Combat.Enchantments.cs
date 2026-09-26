namespace SpireMonteCarlo.Sim;

/// <summary>What enchantments do during a combat (rules from the decompiled enchantment classes, v0.111).</summary>
public sealed partial class Combat
{
    private int EnchantDamageBonus(CardDef? card)
    {
        if (card == null) return 0;
        int bonus = 0;
        switch (card.Enchantment)
        {
            case Enchant.Sharp: bonus += card.EnchantAmount; break;
            case Enchant.TezcatarasEmber: bonus += 3; break;
            case Enchant.Vigorous: if (!card.EnchantUsed) bonus += card.EnchantAmount; break;
        }
        if (card.Enchantment != Enchant.None && Has(RelicKind.MysticLighter)) bonus += 9;
        return bonus;
    }

    private static double EnchantDamageMultiplier(CardDef? card) => card?.Enchantment switch
    {
        Enchant.Instinct => 2.0,
        Enchant.Corrupted => 1.5,
        _ => 1.0,
    };

    private static int EnchantBlockBonus(CardDef? card) => card?.Enchantment switch
    {
        Enchant.Nimble => card.EnchantAmount,
        Enchant.Goopy => Math.Max(0, card.EnchantAmount - 1),
        _ => 0,
    };

    /// <summary>Extra plays an enchantment gives a card that is about to resolve.</summary>
    private static int EnchantExtraPlays(CardDef card) => card.Enchantment switch
    {
        Enchant.Glam when !card.EnchantUsed => 1,
        Enchant.Spiral => Math.Max(1, card.EnchantAmount),
        _ => 0,
    };

    /// <summary>The effects that come with each play of an enchanted card, after its own effects.</summary>
    private void EnchantOnPlay(CardDef card, int target)
    {
        switch (card.Enchantment)
        {
            case Enchant.Adroit: GainBlockRaw(PlayerBlockGain(card.EnchantAmount)); break;
            case Enchant.Corrupted: LoseHp(Scaled(2)); break;
            case Enchant.Inky:
                if (NeedsTarget(card) && target >= 0 && target < Enemies.Count) ApplyDebuff(Enemies[target], PowerKind.Weak, 1);
                else foreach (Enemy e in Enemies.ToList()) if (Targetable(e)) ApplyDebuff(e, PowerKind.Weak, 1);
                break;
            case Enchant.Sown when !card.EnchantUsed: card.EnchantUsed = true; Energy += card.EnchantAmount; break;
            case Enchant.Swift when !card.EnchantUsed: card.EnchantUsed = true; DrawCards(card.EnchantAmount); break;
            case Enchant.Vigorous: card.EnchantUsed = true; break;
            case Enchant.Glam: card.EnchantUsed = true; break;
            case Enchant.Goopy: card.EnchantAmount++; break;
        }
    }

    /// <summary>After the opening hand is drawn: Imbued skills are played for free before the player acts.</summary>
    private void EnchantAtCombatStart()
    {
        foreach (CardDef card in DrawPile.Concat(Hand).Where(c => c.Enchantment == Enchant.Imbued).ToList())
        {
            if (Result != CombatResult.Ongoing) return;
            AutoPlay(card, forceExhaust: false);
        }
    }

    private void EnchantOnDraw(CardDef card)
    {
        if (card.Enchantment == Enchant.Slither && card.Cost >= 0) card.RandomCost = Rng.Next(4);
    }

    /// <summary>After a reshuffle: Perfect Fit cards go on top of the draw pile (the top is the end of the list).</summary>
    private void EnchantOnShuffle()
    {
        var fits = DrawPile.Where(c => c.Enchantment == Enchant.PerfectFit).ToList();
        foreach (CardDef c in fits) { DrawPile.Remove(c); DrawPile.Add(c); }
    }
}
