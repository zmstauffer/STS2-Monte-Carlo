namespace SpireMonteCarlo.Sim;

/// <summary>
/// The rest of the combat relics (shop, event, and Ancient relics), written from the decompiled relic classes (v0.111): energy and draw
/// modifiers, start-of-combat and start-of-turn effects, card-play reactions, shuffle and exhaust reactions, and end-of-combat effects.
/// Per-combat counters live in <see cref="RelicRuntime"/> so that cloning a combat copies them in one step.
/// </summary>
public sealed partial class Combat
{
    /// <summary>Counters and once-per-combat flags of the relics below.</summary>
    private sealed class RelicRuntime
    {
        public bool BurningSticksUsed, ThrowingAxeUsed, LampUsed, MusicBoxUsedThisTurn, PaelsEyeUsed, GamblingDone, SkippedFirstTurnDraw;
        public int PollinousTurns, HappyFlowerTurns, IronClubCards, HandPlaysThisTurn, BlurTurns, TearsPending, RetainHandTurns;
        public int ClarityTurns, RadiantTurns, ShipBlockPending, LegionCooldown, DuplicatorPlays, GoldSpent, GiganticAttack;
        public bool LegionTriggeredLastTurn, ExtraTurnPending;
        public CardDef? LastAttackThisTurn, LastAttackLastTurn;
        public List<CardDef> PendingHandAdds = new();

        public RelicRuntime Clone()
        {
            var copy = (RelicRuntime)MemberwiseClone();
            copy.PendingHandAdds = PendingHandAdds.Select(c => c.Copy()).ToList();
            return copy;
        }
    }

    private RelicRuntime _rr = new();

    /// <summary>Gold the player spent during this combat (Seal of Gold); the rollout takes it out of the purse.</summary>
    public int GoldSpent => _rr.GoldSpent;

    /// <summary>How many potions the player can hold, from the relics that change it.</summary>
    public int PotionSlotCount => 3 + (Has(RelicKind.PotionBelt) ? 2 : 0) + (Has(RelicKind.PhialHolster) ? 1 : 0) + (Has(RelicKind.AlchemicalCoffer) ? 4 : 0);

    private CardDef? CardByIdFromServices(string id) => _services?.Card(id);

    private IReadOnlyList<CardDef> ClassPool() => _services?.CardPool?.Invoke(Character) ?? Array.Empty<CardDef>();

    private IReadOnlyList<CardDef> ColorlessPool() => _services?.CardPool?.Invoke("colorless") ?? Array.Empty<CardDef>();

    // ---- energy and drawing ----

    /// <summary>Extra energy at the start of this turn from relics (and potions that give energy over several turns).</summary>
    private int TurnEnergyBonus()
    {
        int bonus = 0;
        if (Has(RelicKind.Bread)) bonus += Turn == 1 ? -2 : 1;
        foreach (RelicKind k in EnergyRelics) if (Has(k)) bonus += 1;
        if (Has(RelicKind.PumpkinCandle)) bonus += 1;
        if (Has(RelicKind.PaelsFlesh) && Turn >= 3) bonus += 1;
        if (_rr.TearsPending > 0) { bonus += _rr.TearsPending; _rr.TearsPending = 0; }
        if (Has(RelicKind.VeryHotCocoa) && Turn == 1) bonus += 4;
        if (Has(RelicKind.VenerableTeaSetBonus) && Turn == 1) bonus += 2;
        if (Has(RelicKind.FakeVenerableTeaSetBonus) && Turn == 1) bonus += 1;
        if (Has(RelicKind.FakeHappyFlower)) { _rr.HappyFlowerTurns++; if (_rr.HappyFlowerTurns % 5 == 0) bonus += 1; }
        if (_rr.RadiantTurns > 0) { bonus += 1; _rr.RadiantTurns--; }
        if (Has(RelicKind.SealOfGold)) { bonus += 1; _rr.GoldSpent += 5; }
        return bonus;
    }

    private static readonly RelicKind[] EnergyRelics =
    {
        RelicKind.BlessedAntler, RelicKind.BloodSoakedRose, RelicKind.Ectoplasm, RelicKind.PhilosophersStone, RelicKind.Sozu,
        RelicKind.SpikedGauntlets, RelicKind.VelvetChoker, RelicKind.WhisperingEarring,
    };

    /// <summary>Cards drawn at the start of this turn beyond the usual hand (negative for Big Mushroom on turn one).</summary>
    private int ExtraHandDraw()
    {
        int extra = 0;
        if (Has(RelicKind.Fiddle)) extra += 2;
        if (Has(RelicKind.SneckoEye)) extra += 2;
        if (Has(RelicKind.PaelsBlood)) extra += 1;
        if (Has(RelicKind.BigMushroom) && Turn == 1) extra -= 2;
        if (Has(RelicKind.PollinousCore))
        {
            _rr.PollinousTurns++;
            if (_rr.PollinousTurns >= 4) { extra += 2; _rr.PollinousTurns = 0; }
        }
        if (_rr.ClarityTurns > 0) { extra += 1; _rr.ClarityTurns--; }
        return extra;
    }

    /// <summary>True when cards stay in hand at the end of this turn.</summary>
    private bool RetainsWholeHand => Has(RelicKind.RunicPyramid) || (Turn == 1 && Has(RelicKind.RingingTriangle)) || _rr.RetainHandTurns > 0;

    // ---- combat start (before the first hand is drawn) ----

    /// <summary>Relics that change the deck or the enemies as the fight begins, and the "next combat" teas.</summary>
    private void RelicBeforeFirstDraw()
    {
        if (Has(RelicKind.PhilosophersStone))
            foreach (Enemy e in Enemies) if (e.Alive) e.Powers[(int)PowerKind.Strength] += 1;
        if (Has(RelicKind.SwordOfJade)) GainPower(PowerKind.Strength, 3);
        if (Has(RelicKind.EmberTeaActive)) GainPower(PowerKind.Strength, 2);
        if (Has(RelicKind.SlingOfCourage) && Stakes == 1) GainPower(PowerKind.Strength, 2);
        if (Has(RelicKind.GiryaLift1)) GainPower(PowerKind.Strength, 1);
        if (Has(RelicKind.GiryaLift2)) GainPower(PowerKind.Strength, 1);
        if (Has(RelicKind.GiryaLift3)) GainPower(PowerKind.Strength, 1);
        if (Has(RelicKind.FakeAnchor)) GainBlockRaw(4);
        if (Has(RelicKind.FakeBloodVial)) Heal(Scaled(1));
        if (Has(RelicKind.FakeSneckoEye) || Has(RelicKind.SneckoEye)) PlayerPowers[(int)PowerKind.Confused] = 1;
        if (Has(RelicKind.DiamondDiadem)) { GainBlockRaw(20); _rr.BlurTurns = 1; }
        if (Has(RelicKind.PetrifiedToad) && Potions.Count < PotionSlotCount && PotionLibrary.Find("POTION_SHAPED_ROCK") is { } rock) Potions.Add(rock);
        if (Has(RelicKind.DelicateFrond))
            while (Potions.Count < PotionSlotCount && PotionLibrary.Roll(Rng) is { } filler) Potions.Add(filler);
        if (Has(RelicKind.BlessedAntler) || Has(RelicKind.TeaOfDiscourtesyActive))
            AddStatusToDraw("DAZED", Has(RelicKind.BlessedAntler) ? 3 : 2);
        if (Has(RelicKind.BlessedAntler) && Has(RelicKind.TeaOfDiscourtesyActive)) AddStatusToDraw("DAZED", 2);
        if (Has(RelicKind.VexingPuzzlebox) && ClassPool().Count > 0) { CardDef made = ClassPool()[Rng.Next(ClassPool().Count)].Instantiate(); made.FreeThisTurn = true; _rr.PendingHandAdds.Add(made); }
        if (Has(RelicKind.RadiantPearl) && CardByIdFromServices("LUMINESCE") is { } lum) _rr.PendingHandAdds.Add(lum.Instantiate());
        if (Has(RelicKind.Toolbox) && ColorlessPool().Count > 0)
        {
            // Choose 1 of 3 random colorless cards: the one the bot values most.
            CardDef best = ColorlessPool().OrderBy(_ => Rng.NextU64()).Take(3).OrderByDescending(CardChoices.KeepValue).First();
            _rr.PendingHandAdds.Add(best.Instantiate());
        }
        if (Has(RelicKind.JeweledMask))
        {
            var powers = DrawPile.Where(c => c.Kind == CardKind.Power).ToList();
            if (powers.Count > 0)
            {
                CardDef pick = powers[Rng.Next(powers.Count)];
                DrawPile.Remove(pick);
                pick.FreeThisTurn = true;
                _rr.PendingHandAdds.Add(pick);
            }
        }
    }

    private void AddStatusToDraw(string id, int count)
    {
        CardDef? status = CardByIdFromServices(id);
        if (status == null) return;
        for (int i = 0; i < count; i++) DrawPile.Insert(Rng.Next(DrawPile.Count + 1), status.Instantiate());
    }

    /// <summary>After the first hand is drawn: relics that add cards to it, and the ones that fiddle with what was drawn.</summary>
    private void RelicAfterFirstDraw()
    {
        foreach (CardDef c in _rr.PendingHandAdds) AddToHand(c);
        _rr.PendingHandAdds.Clear();
        if (Has(RelicKind.RoyalPoison)) LoseHp(Scaled(4));
        if (Has(RelicKind.BoneTeaActive))
            foreach (CardDef c in Hand.ToList()) if (c.UpgradedForm != null) Upgrade(Hand, c);
        if (Has(RelicKind.GamblingChip)) GamblingChipDiscard();
        if (Has(RelicKind.ChoicesParadox) && ClassPool().Count > 0)
        {
            // Add 1 of 5 random cards to hand; the bot takes the one it values most, and it Retains.
            var options = ClassPool().OrderBy(_ => Rng.NextU64()).Take(5).ToList();
            CardDef best = options.OrderByDescending(CardChoices.KeepValue).First().Instantiate();
            AddToHand(best);
        }
    }

    /// <summary>Discards the cards the bot doesn't want and draws as many new ones (Gambling Chip, Gambler's Brew).</summary>
    private void GamblingChipDiscard()
    {
        var unwanted = Hand.Where(c => CardChoices.KeepValue(c) < 1.0).ToList();
        foreach (CardDef c in unwanted) { Hand.Remove(c); DiscardPile.Add(c); }
        DrawCards(unwanted.Count);
    }

    // ---- every turn ----

    /// <summary>Start-of-turn effects other than energy and drawing.</summary>
    private void RelicTurnStartEffects()
    {
        if (Has(RelicKind.Brimstone))
        {
            GainPower(PowerKind.Strength, 2);
            foreach (Enemy e in Enemies) if (e.Alive) e.Powers[(int)PowerKind.Strength] += 1;
        }
        if (Has(RelicKind.Sai)) GainBlockRaw(7);
        if (Has(RelicKind.MrStruggles)) HitAllEnemies(Turn);
        if (_rr.ShipBlockPending > 0) { GainBlockRaw(_rr.ShipBlockPending); _rr.ShipBlockPending = 0; }
        if (Has(RelicKind.Crossbow) && ClassPool().Where(c => c.Kind == CardKind.Attack).ToList() is { Count: > 0 } attacks)
        {
            CardDef made = attacks[Rng.Next(attacks.Count)].Instantiate();
            made.FreeThisTurn = true;
            _rr.PendingHandAdds.Add(made);
        }
        _rr.MusicBoxUsedThisTurn = false;
        _rr.HandPlaysThisTurn = 0;
        _rr.LastAttackLastTurn = _rr.LastAttackThisTurn;
        _rr.LastAttackThisTurn = null;
    }

    /// <summary>After the hand is drawn each turn (not just the first).</summary>
    private void RelicAfterEveryDraw()
    {
        if (Turn > 1 || !Has(RelicKind.GamblingChip)) { /* gambling chip only on turn one, handled in RelicAfterFirstDraw */ }
        foreach (CardDef c in _rr.PendingHandAdds) AddToHand(c);
        _rr.PendingHandAdds.Clear();
        if (Has(RelicKind.ToastyMittens) && Hand.Count > 0)
        {
            if (CardChoices.WorstToExhaust(Hand) is { } worst) ExhaustCard(worst);
            GainPower(PowerKind.Strength, 1);
        }
        if (Has(RelicKind.HistoryCourse) && Turn > 1 && _rr.LastAttackLastTurn is { } last && Result == CombatResult.Ongoing)
        {
            CardDef dupe = Clone(last);
            dupe.FreeThisTurn = true;
            AutoPlay(dupe, forceExhaust: false, alreadyRemoved: true);
        }
    }

    // ---- playing cards ----

    /// <summary>Cost adjustments from relics, applied to a card's normal cost.</summary>
    private int RelicCostAdjust(CardDef card, int cost)
    {
        if (Has(RelicKind.SpikedGauntlets) && card.Kind == CardKind.Power) cost += 1;
        return cost;
    }

    private bool RelicMakesCardFree(CardDef card) =>
        Has(RelicKind.BrilliantScarf) && _rr.HandPlaysThisTurn == 4;

    /// <summary>Extra plays of a card that is about to resolve (Throwing Axe's first card; Duplicator's next card).</summary>
    private int RelicExtraPlays(CardDef card)
    {
        int extra = 0;
        if (Has(RelicKind.ThrowingAxe) && !_rr.ThrowingAxeUsed) { _rr.ThrowingAxeUsed = true; extra++; }
        if (_rr.DuplicatorPlays > 0) { _rr.DuplicatorPlays--; extra++; }
        return extra;
    }

    /// <summary>Reactions after a card resolves that the older relic hooks don't cover.</summary>
    private void RelicAfterCardExtra(CardDef card)
    {
        _rr.IronClubCards++;
        if (Has(RelicKind.IronClub) && _rr.IronClubCards % 4 == 0) DrawCards(1);
        switch (card.Kind)
        {
            case CardKind.Attack:
                if (Has(RelicKind.DaughterOfTheWind)) GainBlockRaw(1);
                if (Has(RelicKind.MusicBox) && !_rr.MusicBoxUsedThisTurn)
                {
                    _rr.MusicBoxUsedThisTurn = true;
                    CardDef copy = Clone(card);
                    copy.ExtraEthereal = true;
                    AddToHand(copy);
                }
                _rr.LastAttackThisTurn = card;
                break;
            case CardKind.Power:
                if (Has(RelicKind.LostWisp)) HitAllEnemies(8);
                break;
        }
    }

    /// <summary>Razor Tooth upgrades an Attack or Skill for the rest of the combat once it has been played.</summary>
    private CardDef RelicUpgradePlayedCard(CardDef card)
    {
        if (Has(RelicKind.RazorTooth) && card.Kind is CardKind.Attack or CardKind.Skill && card.UpgradedForm != null && !card.Upgraded)
        {
            CardDef up = card.UpgradedForm.Instantiate();
            up.BonusDamage = card.BonusDamage;
            up.CostIncreaseThisCombat = card.CostIncreaseThisCombat;
            return up;
        }
        return card;
    }

    private void RelicOnExhaustCard(CardDef card)
    {
        if (Has(RelicKind.BurningSticks) && !_rr.BurningSticksUsed && card.Kind == CardKind.Skill)
        {
            _rr.BurningSticksUsed = true;
            AddToHand(Clone(card));
        }
        if (Has(RelicKind.ForgottenSoul))
        {
            var alive = Enemies.Where(Targetable).ToList();
            if (alive.Count > 0) DamageEnemy(alive[Rng.Next(alive.Count)], 1, fromCard: false);
        }
    }

    private void RelicOnShuffle()
    {
        if (Has(RelicKind.TheAbacus)) GainBlockRaw(6);
        if (Has(RelicKind.BiiigHug) && CardByIdFromServices("SOOT") is { } soot) DrawPile.Insert(Rng.Next(DrawPile.Count + 1), soot.Instantiate());
    }

    private void RelicOnPotionUsed()
    {
        if (Has(RelicKind.ReptileTrinket)) GainPower(PowerKind.TempStrength, 3);
    }

    /// <summary>Dexterity from Belt Buckle while the player holds no potions.</summary>
    private int RelicDexterityBonus() => Has(RelicKind.BeltBuckle) && Potions.Count == 0 ? 2 : 0;

    /// <summary>An attack that would deal 1 to 4 unblocked damage deals 5 instead (The Boot).</summary>
    private int RelicMinimumDamage(int lost, bool fromCard) => Has(RelicKind.TheBoot) && fromCard && lost is > 0 and < 5 ? 5 : lost;

    // ---- ending a turn ----

    /// <summary>Relics that act just before the hand is discarded. Returns true when the turn should end early into an extra turn.</summary>
    private bool RelicBeforeHandDiscard()
    {
        if (Has(RelicKind.ScreamingFlagon) && Hand.Count == 0) HitAllEnemies(20);
        if (Has(RelicKind.FakeOrichalcum) && Block == 0) GainBlockRaw(3);
        _rr.TearsPending = Has(RelicKind.PaelsTears) && Energy > 0 ? 2 : 0;
        if (Has(RelicKind.PaelsEye) && !_rr.PaelsEyeUsed && _rr.HandPlaysThisTurn == 0 && Turn > 0 && !_rr.ExtraTurnPending)
        {
            _rr.PaelsEyeUsed = true;
            foreach (CardDef c in Hand.ToList()) ExhaustCard(c);
            return true;
        }
        _rr.ExtraTurnPending = false;
        if (_rr.RetainHandTurns > 0) _rr.RetainHandTurns--;
        return false;
    }

    // ---- winning ----

    private void RelicAfterVictoryExtra()
    {
        if (Has(RelicKind.BlackBlood)) Heal(Scaled(12));
        if (Has(RelicKind.ChosenCheese)) GainMaxHp(Scaled(1));
    }
}
