namespace SpireMonteCarlo.Sim;

/// <summary>The potion effects that need more than a number: timed buffs, card generation and choices, and pile changes (rules from the decompiled potion and power classes, v0.111).</summary>
public sealed partial class Combat
{
    private void ApplySpecial(SpecialEffect kind, int amount, int target)
    {
        switch (kind)
        {
            case SpecialEffect.Clarity:
                DrawCards(1);
                _rr.ClarityTurns = 3;
                break;
            case SpecialEffect.RadiantTincture:
                Energy += 1;
                _rr.RadiantTurns = 3;
                break;
            case SpecialEffect.ShipInABottle:
                GainBlockRaw(amount);
                _rr.ShipBlockPending += amount;
                break;
            case SpecialEffect.Duplicator:
                _rr.DuplicatorPlays += 1;
                break;
            case SpecialEffect.Gigantification:
                _rr.GiganticAttack = 1;
                break;
            case SpecialEffect.StableSerum:
                _rr.RetainHandTurns = amount;
                break;
            case SpecialEffect.BottledPotential:
                foreach (CardDef c in Hand.ToList()) { Hand.Remove(c); DrawPile.Add(c); }
                Rng.Shuffle(DrawPile);
                EnchantOnShuffle();
                RelicOnShuffle();
                DrawCards(amount);
                break;
            case SpecialEffect.Ashwater:
                foreach (CardDef c in Hand.Where(c => CardChoices.KeepValue(c) < 0.5).ToList()) ExhaustCard(c);
                break;
            case SpecialEffect.GamblersBrew:
                GamblingChipDiscard();
                break;
            case SpecialEffect.DropletOfPrecognition:
                if (DrawPile.Count > 0 && Hand.Count < MaxHandSize)
                {
                    CardDef best = DrawPile.OrderByDescending(CardChoices.KeepValue).First();
                    DrawPile.Remove(best);
                    Hand.Add(best);
                }
                break;
            case SpecialEffect.LiquidMemories:
                if (DiscardPile.Count > 0 && Hand.Count < MaxHandSize)
                {
                    CardDef best = CardChoices.BestToRecall(DiscardPile)!;
                    DiscardPile.Remove(best);
                    best.FreeThisTurn = true;
                    Hand.Add(best);
                }
                break;
            case SpecialEffect.EntropicBrew:
                while (Potions.Count < PotionSlotCount && PotionLibrary.Roll(Rng) is { } filler) Potions.Add(filler);
                break;
            case SpecialEffect.Glowwater:
                foreach (CardDef c in Hand.ToList()) ExhaustCard(c);
                DrawCards(amount);
                break;
            case SpecialEffect.SneckoOil:
                DrawCards(amount);
                foreach (CardDef c in Hand) if (c.Cost >= 0) c.RandomCost = Rng.Next(4);
                break;
            case SpecialEffect.SoldiersStew:
                _rr.StewReplays++;
                break;
            case SpecialEffect.OrobicAcid:
                foreach (CardKind k in new[] { CardKind.Attack, CardKind.Skill, CardKind.Power })
                    AddFreeFromPool(ClassPool().Where(c => c.Kind == k).ToList(), 1);
                break;
            case SpecialEffect.GenerateColorless:
                AddFreeFromPool(ColorlessPool().ToList(), 3);
                break;
            case SpecialEffect.GenerateSkill:
                AddFreeFromPool(ClassPool().Where(c => c.Kind == CardKind.Skill).ToList(), 3);
                break;
            case SpecialEffect.GeneratePower:
                AddFreeFromPool(ClassPool().Where(c => c.Kind == CardKind.Power).ToList(), 3);
                break;
            case SpecialEffect.TouchOfInsanity:
                {
                    CardDef? pick = Hand.Where(c => c.Cost > 0).OrderByDescending(c => c.CurrentCost).ThenByDescending(CardChoices.KeepValue).FirstOrDefault();
                    if (pick != null) pick.FreeThisCombat = true;
                    break;
                }
            case SpecialEffect.FoulPotion:
                HitAllEnemies(amount);
                HitPlayer(Scaled(amount), null);
                break;
        }
    }

    /// <summary>Picks a few distinct cards from a pool, adds the one the bot values most to the hand, free for this turn.</summary>
    private void AddFreeFromPool(List<CardDef> pool, int choices)
    {
        if (pool.Count == 0) return;
        var options = new List<CardDef>();
        for (int i = 0; i < choices && pool.Count > 0; i++)
        {
            CardDef c = pool[Rng.Next(pool.Count)];
            if (!options.Contains(c)) options.Add(c);
        }
        CardDef made = (choices == 1 ? options[0] : options.OrderByDescending(CardChoices.KeepValue).First()).Instantiate();
        made.FreeThisTurn = true;
        AddToHand(made);
    }
}
