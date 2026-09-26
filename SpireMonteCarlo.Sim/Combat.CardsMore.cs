namespace SpireMonteCarlo.Sim;

/// <summary>
/// The colorless, event, curse, and status cards' mechanics that the Ironclad cards never needed: returning cards, colorless powers, end-of-turn drawbacks,
/// limits on which cards can be played, and effects that pick cards from piles (rules from the decompiled card and power classes, v0.111).
/// </summary>
public sealed partial class Combat
{
    /// <summary>Gold the player gained during this combat (Hand of Greed); the rollout adds it to the purse.</summary>
    public int GoldGained => _rr.GoldGained;

    private int _damageDealtByCard;

    // ---- can the card be played ----

    private bool CardRulesAllowPlay(CardDef card)
    {
        if (card.OnlyIfHandAllAttacks && Hand.Any(c => c.Kind != CardKind.Attack)) return false;
        foreach (CardDef held in Hand)
        {
            if (held.MustPlayFirst && held != card) return false;
            if (held.PlayCap > 0 && _rr.HandPlaysThisTurn >= held.PlayCap) return false;
        }
        return true;
    }

    // ---- drawing ----

    private void OnCardDrawn(CardDef card)
    {
        if (card.LoseEnergyWhenDrawn > 0) Energy = Math.Max(0, Energy - card.LoseEnergyWhenDrawn);
        if (PlayerPowers[(int)PowerKind.Automation] > 0)
        {
            _rr.AutomationCards++;
            if (_rr.AutomationCards >= 10) { _rr.AutomationCards = 0; Energy += PlayerPowers[(int)PowerKind.Automation]; }
        }
    }

    // ---- start of turn ----

    /// <summary>Cards that come back to the hand and the powers that add cards before the new hand is drawn.</summary>
    private void PowersBeforeHandDraw()
    {
        foreach (CardDef c in _rr.Returning) AddToHand(c);
        _rr.Returning.Clear();
        if (PlayerPowers[(int)PowerKind.HelloWorld] > 0 && ClassPool().Count > 0)
            for (int i = 0; i < PlayerPowers[(int)PowerKind.HelloWorld]; i++) AddToHand(ClassPool()[Rng.Next(ClassPool().Count)].Instantiate());
        if (_rr.ToricTurns > 0) { GainBlockRaw(_rr.ToricBlock); _rr.ToricTurns--; }
    }

    /// <summary>Powers that act at the start of the turn once the hand is drawn.</summary>
    private void PowersAfterHandDraw()
    {
        PlayerPowers[(int)PowerKind.Vigor] += PlayerPowers[(int)PowerKind.PrepTime];
        if (PlayerPowers[(int)PowerKind.RollingBoulder] > 0)
        {
            HitAllEnemies(PlayerPowers[(int)PowerKind.RollingBoulder] + _rr.BoulderExtra);
            _rr.BoulderExtra += 5;
        }
        if (PlayerPowers[(int)PowerKind.Entropy] > 0 && Hand.Count > 0 && ClassPool().Count > 0)
            for (int i = 0; i < PlayerPowers[(int)PowerKind.Entropy] && Hand.Count > 0; i++)
            {
                CardDef worst = CardChoices.WorstToExhaust(Hand)!;
                int at = Hand.IndexOf(worst);
                Hand[at] = ClassPool()[Rng.Next(ClassPool().Count)].Instantiate();
            }
        if (PlayerPowers[(int)PowerKind.Mayhem] > 0)
            for (int i = 0; i < PlayerPowers[(int)PowerKind.Mayhem] && Result == CombatResult.Ongoing; i++)
            {
                if ((DrawPile.Count == 0 && !ReshuffleDiscard()) || DrawPile.Count == 0) break;
                CardDef top = DrawPile[^1];
                DrawPile.RemoveAt(DrawPile.Count - 1);
                AutoPlay(top, forceExhaust: false, alreadyRemoved: true);
            }
    }

    // ---- after a card ----

    private void PowersAfterCard(CardDef card)
    {
        if (PlayerPowers[(int)PowerKind.Panache] > 0)
        {
            if (_rr.PanacheStarted)
            {
                _rr.PanacheCount++;
                if (_rr.PanacheCount >= 5) { _rr.PanacheCount = 0; HitAllEnemies(PlayerPowers[(int)PowerKind.Panache]); }
            }
            _rr.PanacheStarted = true;
        }
        if (card.Kind == CardKind.Attack && PlayerPowers[(int)PowerKind.Calamity] > 0)
        {
            var attacks = ClassPool().Where(c => c.Kind == CardKind.Attack).ToList();
            for (int i = 0; i < PlayerPowers[(int)PowerKind.Calamity] && attacks.Count > 0; i++) AddToHand(attacks[Rng.Next(attacks.Count)].Instantiate());
        }
    }

    /// <summary>Where a played card goes: Rebound and Nostalgia put it on top of the draw pile, Bolas and the Hatchet return next turn. Null when it goes to the discard pile as usual.</summary>
    private bool PlaceAfterPlay(CardDef card)
    {
        if (card.ReturnsNextTurn) { _rr.Returning.Add(card); return true; }
        if (_rr.ReboundPending)
        {
            _rr.ReboundPending = false;
            DrawPile.Add(card);
            return true;
        }
        if (PlayerPowers[(int)PowerKind.Nostalgia] > 0 && card.Kind is CardKind.Attack or CardKind.Skill)
        {
            _rr.NostalgiaPlays++;
            if (_rr.NostalgiaPlays <= PlayerPowers[(int)PowerKind.Nostalgia]) { DrawPile.Add(card); return true; }
        }
        return false;
    }

    // ---- end of turn ----

    /// <summary>The end-of-turn drawbacks of cards still in hand that need more than damage, and the timed effects that go off.</summary>
    private void EndOfTurnCardEffects()
    {
        int inHand = Hand.Count;
        foreach (CardDef card in Hand.ToList())
        {
            if (card.EndTurnPower != PowerKind.Unsupported) AddPower(PlayerPowers, card.EndTurnPower, card.EndTurnPowerAmount, debuff: true);
            if (card.EndTurnGold > 0) _rr.GoldSpent += card.EndTurnGold;
            if (card.RegretLike) LoseHp(inHand);
        }
        for (int i = _rr.Bombs.Count - 1; i >= 0; i--)
        {
            (int turns, int damage) = _rr.Bombs[i];
            if (turns > 1) _rr.Bombs[i] = (turns - 1, damage);
            else { _rr.Bombs.RemoveAt(i); HitAllEnemies(damage); }
        }
        _rr.PanacheCount = 0;
        _rr.PanacheStarted = false;
        _rr.NostalgiaPlays = 0;
        _rr.ReboundPending = false;
        if (PlayerPowers[(int)PowerKind.NoCardBlock] > 0) PlayerPowers[(int)PowerKind.NoCardBlock]--;
    }

    // ---- effects that pick cards ----

    private void ApplyCardSpecial(SpecialEffect kind, int amount, int hits, CardDef card, int target)
    {
        switch (kind)
        {
            case SpecialEffect.ProcurePotion:
                if (Potions.Count < PotionSlotCount && PotionLibrary.Roll(Rng) is { } potion) Potions.Add(potion);
                break;
            case SpecialEffect.AnointedRares:
                foreach (CardDef c in DrawPile.Where(c => c.Rarity == "Rare").ToList()) { if (Hand.Count >= MaxHandSize) break; DrawPile.Remove(c); Hand.Add(c); }
                break;
            case SpecialEffect.BeatDown:
                for (int i = 0; i < amount && Result == CombatResult.Ongoing; i++)
                {
                    var attacks = DiscardPile.Where(c => c.Kind == CardKind.Attack).ToList();
                    if (attacks.Count == 0) break;
                    AutoPlay(attacks[Rng.Next(attacks.Count)], forceExhaust: false);
                }
                break;
            case SpecialEffect.Discovery:
                AddFreeFromPool(ClassPool().ToList(), 3);
                break;
            case SpecialEffect.JackOfAllTrades:
                if (ColorlessPool().Count > 0) AddToHand(ColorlessPool()[Rng.Next(ColorlessPool().Count)].Instantiate());
                break;
            case SpecialEffect.Jackpot:
                {
                    var free = ClassPool().Where(c => c.Cost == 0).ToList();
                    for (int i = 0; i < amount && free.Count > 0; i++) AddToHand(free[Rng.Next(free.Count)].Instantiate());
                    break;
                }
            case SpecialEffect.ImpatienceDraw:
                if (!Hand.Any(c => c.Kind == CardKind.Attack)) DrawCards(amount);
                break;
            case SpecialEffect.RestlessnessDraw:
                if (Hand.Count == 0) { DrawCards(amount); Energy += hits; }
                break;
            case SpecialEffect.ScrawlDraw:
                DrawCards(MaxHandSize);
                break;
            case SpecialEffect.SecretTechnique:
                PullBest(DrawPile.Where(c => c.Kind == CardKind.Skill).ToList());
                break;
            case SpecialEffect.SecretWeapon:
                PullBest(DrawPile.Where(c => c.Kind == CardKind.Attack).ToList());
                break;
            case SpecialEffect.SeekerStrike:
                {
                    var options = DrawPile.OrderBy(_ => Rng.NextU64()).Take(amount).ToList();
                    PullBest(options);
                    break;
                }
            case SpecialEffect.ThinkingAhead:
                DrawCards(amount);
                if (Hand.Count > 0)
                {
                    CardDef worst = CardChoices.WorstToExhaust(Hand.Where(c => c != card).ToList()) ?? Hand[0];
                    Hand.Remove(worst);
                    DrawPile.Add(worst);
                }
                break;
            case SpecialEffect.Purity:
                foreach (CardDef c in Hand.Where(c => c != card && CardChoices.KeepValue(c) < 0.5).Take(amount).ToList()) ExhaustCard(c);
                break;
            case SpecialEffect.Prolong:
                _rr.ShipBlockPending += Block;
                break;
            case SpecialEffect.RetainHandThisTurn:
                _rr.RetainHandTurns = Math.Max(_rr.RetainHandTurns, 1);
                break;
            case SpecialEffect.Rebound:
                _rr.ReboundPending = true;
                break;
            case SpecialEffect.NeowsFury:
                for (int i = 0; i < amount && DiscardPile.Count > 0 && Hand.Count < MaxHandSize; i++)
                {
                    CardDef best = CardChoices.BestToRecall(DiscardPile)!;
                    DiscardPile.Remove(best);
                    Hand.Add(best);
                }
                break;
            case SpecialEffect.Enlightenment:
                foreach (CardDef c in Hand) if (c.CurrentCost > 1) c.CostReductionThisTurn += c.CurrentCost - 1;
                break;
            case SpecialEffect.Metamorphosis:
                {
                    var attacks = ClassPool().Where(c => c.Kind == CardKind.Attack).ToList();
                    for (int i = 0; i < amount && attacks.Count > 0; i++)
                    {
                        CardDef made = attacks[Rng.Next(attacks.Count)].Instantiate();
                        made.FreeThisCombat = true;
                        DrawPile.Insert(Rng.Next(DrawPile.Count + 1), made);
                    }
                    break;
                }
            case SpecialEffect.Distraction:
                AddFreeFromPool(ClassPool().Where(c => c.Kind == CardKind.Skill).ToList(), 1);
                break;
            case SpecialEffect.DualWield:
                {
                    CardDef? best = Hand.Where(c => c != card && c.Kind is CardKind.Attack or CardKind.Power).OrderByDescending(CardChoices.KeepValue).FirstOrDefault();
                    if (best != null) for (int i = 0; i < Math.Max(1, amount); i++) AddToHand(Clone(best));
                    break;
                }
            case SpecialEffect.Wish:
                PullBest(DrawPile.ToList());
                break;
            case SpecialEffect.TheBomb:
                _rr.Bombs.Add((amount, hits));
                break;
            case SpecialEffect.Gambit:
                PlayerPowers[(int)PowerKind.Gambit] = 1;
                break;
            case SpecialEffect.PanicButton:
                PlayerPowers[(int)PowerKind.NoCardBlock] = amount;
                break;
            case SpecialEffect.BlockFromDamage:
                GainBlockRaw(_damageDealtByCard);
                break;
            case SpecialEffect.OmnisliceSpread:
                {
                    int dealt = _damageDealtByCard;
                    foreach (Enemy e in Enemies.ToList())
                        if (Targetable(e) && e.Index != target) DamageEnemy(e, dealt, fromCard: false);
                    break;
                }
            case SpecialEffect.GoldIfFatal:
                break;   // handled by the caller, which knows whether the attack killed
            case SpecialEffect.HiddenGem:
                {
                    var candidates = Enumerable.Range(0, DrawPile.Count).Where(i => DrawPile[i].Replay == 0).ToList();
                    if (candidates.Count > 0)
                    {
                        int i = candidates[Rng.Next(candidates.Count)];
                        CardDef copy = Clone(DrawPile[i]);
                        copy.Replay += amount;
                        DrawPile[i] = copy;
                    }
                    break;
                }
            case SpecialEffect.Stun:
                if (target >= 0 && target < Enemies.Count && Targetable(Enemies[target])) Enemies[target].Stun();
                break;
            case SpecialEffect.BrightestFlame:
                MaxHp = Math.Max(1, MaxHp - Scaled(amount));
                Hp = Math.Min(Hp, MaxHp);
                break;
            case SpecialEffect.Apotheosis:
                foreach (var pile in new[] { DrawPile, Hand, DiscardPile, ExhaustPile })
                    for (int i = 0; i < pile.Count; i++)
                        if (pile[i].UpgradedForm != null && !pile[i].Upgraded) pile[i] = pile[i].UpgradedForm.Instantiate();
                break;
            case SpecialEffect.Outmaneuver:
                _rr.NextTurnEnergy += amount;
                break;
            case SpecialEffect.Relax:
                _rr.NextTurnEnergy += hits;
                _rr.NextTurnDraw += amount;
                break;
            case SpecialEffect.TorcToughness:
                _rr.ToricTurns = hits;
                _rr.ToricBlock = amount;
                break;
        }
    }

    /// <summary>Moves the card the bot values most from the candidates (all in a pile of this combat) into the hand.</summary>
    private void PullBest(List<CardDef> candidates)
    {
        if (candidates.Count == 0 || Hand.Count >= MaxHandSize) return;
        CardDef best = candidates.OrderByDescending(CardChoices.KeepValue).First();
        if (!DrawPile.Remove(best)) return;
        Hand.Add(best);
    }
}
