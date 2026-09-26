namespace SpireMonteCarlo.Sim;

/// <summary>Playing cards: effects, piles, and the player powers that react to them (rules from the decompiled card and power classes).</summary>
public sealed partial class Combat
{
    private bool _playerTurn;
    private bool _cardsChangeInPlace, _pilesScanned;

    /// <summary>Cards that change themselves while they lie in a pile or when played from one (Stomp, Rampage, Thrash): a cloned combat can't share these.</summary>
    private static bool ChangesInPlace(CardDef c) =>
        c.CheaperPerAttackPlayed || c.DamageGrowthPerPlay > 0 || c.Effects.Any(e => e.Op == EffectOp.AbsorbRandomAttack);
    private int _attacksPlayedThisTurn;
    private int _cardBlockGainsThisTurn;
    private int _cardsExhaustedThisTurn;
    private bool _lostHpThisTurn;
    private int _timesHurt;
    private int _cardsInPlay;
    private int _pendingRupture;

    private sealed class PlayContext
    {
        public required CardDef Card;
        public int Target = -1;
        public int X;
        public int HandExhausted;
        public bool LastKilled;
        /// <summary>Unsettling Lamp is waiting for the first debuff card of the combat; this card's debuffs count double.</summary>
        public bool DoubleDebuffs, LampFired;
        public Enemy? LastTarget;
    }

    // ---- values the bot and the effects both need -----------------------------------------------------------

    public bool LostHpThisTurn => _lostHpThisTurn;
    public int CardsExhaustedThisTurn => _cardsExhaustedThisTurn;

    /// <summary>The number a <see cref="Source"/> currently stands for.</summary>
    public int SourceValue(Effect e, Source source, CardDef card, Enemy? target, int x, int handExhausted) => source switch
    {
        Source.X => x,
        Source.Block => Block,
        Source.Strength => Math.Max(0, PlayerPowers[(int)PowerKind.Strength]),
        Source.StrikeCards => StrikeCount(card),
        Source.ExhaustPileCount => ExhaustPile.Count,
        Source.TargetVulnerable => target?.Powers[(int)PowerKind.Vulnerable] ?? 0,
        Source.TargetIsVulnerable => target != null && target.Powers[(int)PowerKind.Vulnerable] > 0 ? 1 : 0,
        Source.HandExhausted => handExhausted,
        Source.DrawPileCount => DrawPile.Count,
        Source.DiscardPileCount => DiscardPile.Count,
        Source.CardsPlayedInCombat => CardsPlayed,
        Source.TargetDebuffs => target == null ? 0 : Enum.GetValues<PowerKind>().Count(k => PowerRules.IsDebuff(k) && target.Powers[(int)k] > 0),
        Source.MaulPlays => _rr.MaulPlays,
        Source.TimesHurtPlusOne => 1 + _timesHurt,
        Source.LostHpThisTurn => _lostHpThisTurn ? 1 : 0,
        Source.ExhaustedThisTurn => _cardsExhaustedThisTurn > 0 ? 1 : 0,
        Source.ExhaustAtLeast => ExhaustPile.Count >= e.Arg ? 1 : 0,
        _ => 0,
    };

    private static bool IsDamageOp(EffectOp op) => op is EffectOp.Damage or EffectOp.DamageAll or EffectOp.DamageRandom;

    /// <summary>Base amount of an effect right now (before Strength, Dexterity, and the like), including this copy's own growth.</summary>
    public int EffectAmount(Effect e, CardDef card, Enemy? target, int x = 0, int handExhausted = 0)
    {
        int amount = e.Amount;
        if (e.AmountSource != Source.None) amount += e.Per * SourceValue(e, e.AmountSource, card, target, x, handExhausted);
        if (IsDamageOp(e.Op)) amount += card.BonusDamage;
        return amount;
    }

    public int EffectHits(Effect e, CardDef card, Enemy? target, int x = 0, int handExhausted = 0)
    {
        int hits = e.Hits;
        if (e.HitsSource != Source.None) hits += e.HitsPer * SourceValue(e, e.HitsSource, card, target, x, handExhausted);
        return Math.Max(0, hits);
    }

    private int StrikeCount(CardDef card)
    {
        int n = 0;
        foreach (var pile in new[] { DrawPile, Hand, DiscardPile, ExhaustPile })
            foreach (CardDef c in pile)
                if (c.IsStrike) n++;
        if (card.IsStrike && !Hand.Contains(card)) n++;
        return n;
    }

    // ---- potions --------------------------------------------------------------------------------------------

    public bool CanUsePotion(PotionDef potion) => Result == CombatResult.Ongoing && _playerTurn && !potion.Automatic;

    /// <summary>Drinks the potion in <paramref name="potionIndex"/>. <paramref name="targetIndex"/> is an index into <see cref="Enemies"/> for aimed potions.</summary>
    public void UsePotion(int potionIndex, int targetIndex)
    {
        PotionDef potion = Potions[potionIndex];
        if (!CanUsePotion(potion)) throw new InvalidOperationException($"Cannot use {potion.Id} now.");
        Potions.RemoveAt(potionIndex);
        RelicOnPotionUsed();
        int target = potion.NeedsTarget ? ResolveTarget(targetIndex) : -1;
        _cardsInPlay++;
        try { RunEffects(potion.AsCard, target, 0, fromPotion: true); }
        finally { _cardsInPlay--; }
        if (_cardsInPlay == 0 && _pendingRupture > 0)
        {
            PlayerPowers[(int)PowerKind.Strength] += _pendingRupture;
            _pendingRupture = 0;
        }
        CheckEnd();
    }

    /// <summary>Fairy in a Bottle: when the player would die, it is used up and they get back up with 30% of max HP.</summary>
    private bool TryFairyInABottle()
    {
        int i = Potions.FindIndex(p => p.Id == "FAIRY_IN_A_BOTTLE");
        if (i < 0) return false;
        Potions.RemoveAt(i);
        Hp = Math.Max(1, (int)(MaxHp * 0.3));
        return true;
    }

    // ---- playing --------------------------------------------------------------------------------------------

    /// <summary>Plays the card in <paramref name="handIndex"/>. <paramref name="targetIndex"/> is an index into <see cref="Enemies"/>.</summary>
    public void Play(int handIndex, int targetIndex)
    {
        CardDef card = Hand[handIndex];
        if (!CanPlay(card)) throw new InvalidOperationException($"Cannot play {card} with {Energy} energy.");
        Hand.RemoveAt(handIndex);

        int cost = EffectiveCost(card);
        _rr.HandPlaysThisTurn++;
        int x = 0;
        if (cost == CardDef.XCost) { x = Energy; Energy = 0; }
        else Energy -= cost;
        if (card.Kind == CardKind.Attack && PlayerPowers[(int)PowerKind.FreeAttack] > 0) PlayerPowers[(int)PowerKind.FreeAttack]--;

        Resolve(card, ResolveTarget(targetIndex), x, forceExhaust: false, costPaid: cost == CardDef.XCost ? x : cost);
    }

    /// <summary>A card played by an effect rather than the player: free, against a random enemy if it needs one.</summary>
    private void AutoPlay(CardDef card, bool forceExhaust, bool alreadyRemoved = false)
    {
        if (!alreadyRemoved && !Hand.Remove(card) && !DrawPile.Remove(card)) DiscardPile.Remove(card);
        if (card.Cost == CardDef.Unplayable)
        {
            if (forceExhaust) ExhaustCard(card); else DiscardPile.Add(card);
            return;
        }
        int target = -1;
        if (NeedsTarget(card))
        {
            var alive = Enemies.Where(Targetable).ToList();
            if (alive.Count == 0) { DiscardPile.Add(card); return; }
            target = alive[Rng.Next(alive.Count)].Index;
        }
        Resolve(card, target, x: 0, forceExhaust);
    }

    public static bool NeedsTarget(CardDef card) =>
        card.Effects.Any(e => e.Op is EffectOp.Damage or EffectOp.DebuffEnemy or EffectOp.BuffEnemy or EffectOp.DoubleDebuff or EffectOp.StrengthFromVulnerable);

    private int ResolveTarget(int targetIndex)
    {
        if (targetIndex >= 0 && targetIndex < Enemies.Count && Targetable(Enemies[targetIndex])) return targetIndex;
        return Enemies.FindIndex(Targetable);
    }

    private void Resolve(CardDef card, int target, int x, bool forceExhaust, int costPaid = 0)
    {
        if (card.Cost == CardDef.XCost && Has(RelicKind.ChemicalX)) x += 2;
        _cardsInPlay++;
        _damageDealtByCard = 0;
        try
        {
            if (card.Kind == CardKind.Attack)
            {
                _attacksPlayedThisTurn++;
                RelicBeforeAttack();
                foreach (var pile in new[] { DrawPile, Hand, DiscardPile })
                    foreach (CardDef other in pile)
                        if (other.CheaperPerAttackPlayed) other.CostReductionThisTurn++;
                if (PlayerPowers[(int)PowerKind.Juggling] > 0 && _attacksPlayedThisTurn == 3)
                    for (int i = 0; i < PlayerPowers[(int)PowerKind.Juggling]; i++) AddToHand(Clone(card));
            }

            int plays = 1;
            if (card.Kind == CardKind.Attack && PlayerPowers[(int)PowerKind.OneTwoPunch] > 0)
            {
                plays = 2;
                PlayerPowers[(int)PowerKind.OneTwoPunch]--;
            }
            plays += RelicExtraPlays(card) + card.Replay + EnchantExtraPlays(card) + (card.IsStrike ? _rr.StewReplays : 0);
            for (int p = 0; p < plays && Result != CombatResult.Lost; p++) RunEffects(card, target, x);

            CardsPlayed++;
            CardsPlayedThisTurn++;
            foreach (Enemy e in Enemies)
                if (e.Powers[(int)PowerKind.Slow] > 0) e.SlowCards++;
            if (card.Kind == CardKind.Attack && PlayerPowers[(int)PowerKind.Rage] > 0) GainBlockRaw(PlayerPowers[(int)PowerKind.Rage]);
            if (card.Kind == CardKind.Attack) _rr.GiganticAttack = 0;
            RelicAfterCard(card, costPaid);
            RelicAfterCardExtra(card);
            PowersAfterCard(card);
            if (card.Id == "MAUL") _rr.MaulPlays++;
            AfterCardTender();
            AfterCardTainted(card);
            AfterCardEnemyPowers(card);
            if (card.CostsMoreEachPlay) card.CostIncreaseThisCombat++;
            ResolveCurlUps();
        }
        finally
        {
            _cardsInPlay--;
        }
        if (_cardsInPlay == 0 && _pendingRupture > 0)
        {
            PlayerPowers[(int)PowerKind.Strength] += _pendingRupture;
            _pendingRupture = 0;
        }

        card = RelicUpgradePlayedCard(card);
        if (card.Kind == CardKind.Power) { /* stays in play for the rest of the combat */ }
        else if (forceExhaust || card.Exhaust || (card.Kind == CardKind.Skill && PlayerPowers[(int)PowerKind.Corruption] > 0)) ExhaustCard(card);
        else if (!PlaceAfterPlay(card)) DiscardPile.Add(card);

        CheckEnd();
    }

    private void RunEffects(CardDef card, int target, int x, bool fromPotion = false)
    {
        var ctx = new PlayContext { Card = card, Target = target, X = x, DoubleDebuffs = !fromPotion && Has(RelicKind.UnsettlingLamp) && !_rr.LampUsed };
        foreach (Effect effect in card.Effects)
        {
            if (Result == CombatResult.Lost) break;
            Apply(effect, ctx);
        }
        if (card.DamageGrowthPerPlay > 0) card.BonusDamage += card.DamageGrowthPerPlay;
        if (ctx.LampFired) _rr.LampUsed = true;
        if (!fromPotion) EnchantOnPlay(card, target);
    }

    private Enemy? TargetOf(PlayContext ctx) =>
        ctx.Target >= 0 && ctx.Target < Enemies.Count ? Enemies[ctx.Target] : null;

    private void Apply(Effect effect, PlayContext ctx)
    {
        CardDef card = ctx.Card;
        Enemy? target = TargetOf(ctx);
        int amount = EffectAmount(effect, card, target, ctx.X, ctx.HandExhausted);
        int hits = EffectHits(effect, card, target, ctx.X, ctx.HandExhausted);

        switch (effect.Op)
        {
            case EffectOp.Damage:
                {
                    bool killed = false;
                    for (int h = 0; h < hits && target != null && Targetable(target); h++)
                        killed |= DamageEnemy(target, PlayerAttackDamage(amount, target, card), fromCard: true);
                    ctx.LastKilled = killed;
                    ctx.LastTarget = target;
                    break;
                }
            case EffectOp.DamageAll:
                for (int h = 0; h < hits; h++)
                    foreach (Enemy e in Enemies.ToList())
                        if (Targetable(e)) DamageEnemy(e, PlayerAttackDamage(amount, e, card), fromCard: true);
                break;
            case EffectOp.DamageRandom:
                for (int h = 0; h < hits; h++)
                {
                    var alive = Enemies.Where(Targetable).ToList();
                    if (alive.Count == 0) break;
                    Enemy e = alive[Rng.Next(alive.Count)];
                    DamageEnemy(e, PlayerAttackDamage(amount, e, card), fromCard: true);
                }
                break;
            case EffectOp.Block:
                for (int h = 0; h < hits; h++) GainBlockFromCard(amount, card);
                break;
            case EffectOp.DamageFlat:
                if (target != null && Targetable(target)) DamageEnemy(target, amount, fromCard: false);
                break;
            case EffectOp.DamageAllFlat:
                foreach (Enemy e in Enemies.ToList())
                    if (Targetable(e)) DamageEnemy(e, amount, fromCard: false);
                break;
            case EffectOp.Special:
                if ((SpecialEffect)effect.Arg == SpecialEffect.GoldIfFatal) { if (ctx.LastKilled) _rr.GoldGained += amount; }
                else ApplySpecial((SpecialEffect)effect.Arg, amount, ctx.Target, hits, card);
                break;
            case EffectOp.BlockFlat: GainBlockRaw(amount); break;
            case EffectOp.DoubleBlock: GainBlockRaw(Block); break;
            case EffectOp.HealPercent: Heal((int)(MaxHp * amount / 100.0)); break;
            case EffectOp.Draw: DrawCards(amount); break;
            case EffectOp.Energy: Energy += amount; break;
            case EffectOp.LoseHp: LoseHp(amount); break;
            case EffectOp.Heal: Heal(amount); break;
            case EffectOp.GainMaxHp: GainMaxHp(amount); break;
            case EffectOp.GainMaxHpIfFatal:
                if (ctx.LastKilled && ctx.LastTarget is { Primary: true }) GainMaxHp(amount);
                break;

            case EffectOp.DebuffEnemy:
                if (ctx.DoubleDebuffs && PowerRules.IsDebuff(effect.Power)) { amount *= 2; ctx.LampFired = true; }
                if (target != null) ApplyDebuff(target, effect.Power, amount);
                break;
            case EffectOp.DebuffAll:
                if (ctx.DoubleDebuffs && PowerRules.IsDebuff(effect.Power)) { amount *= 2; ctx.LampFired = true; }
                foreach (Enemy e in Enemies.ToList())
                    if (Targetable(e)) ApplyDebuff(e, effect.Power, amount);
                break;
            case EffectOp.DebuffSelf: AddPower(PlayerPowers, effect.Power, effect.Power == PowerKind.NoDraw ? 1 - PlayerPowers[(int)PowerKind.NoDraw] : amount, debuff: false); break;
            case EffectOp.BuffSelf: GainPower(effect.Power, amount); break;
            case EffectOp.BuffEnemy:
                if (target != null && target.Alive && effect.Power != PowerKind.Unsupported) target.Powers[(int)effect.Power] += amount;
                break;
            case EffectOp.DoubleDebuff:
                if (target != null && target.Alive)
                {
                    int stacks = target.Powers[(int)effect.Power];
                    if (stacks > 0) ApplyDebuff(target, effect.Power, stacks);
                }
                break;
            case EffectOp.StrengthFromVulnerable:
                PlayerPowers[(int)PowerKind.Strength] += target?.Powers[(int)PowerKind.Vulnerable] ?? 0;
                break;

            case EffectOp.ExhaustRandomFromHand:
                if (Hand.Count > 0) ExhaustCard(Hand[Rng.Next(Hand.Count)]);
                break;
            case EffectOp.ExhaustChosenFromHand:
                if (CardChoices.WorstToExhaust(Hand) is { } worst) ExhaustCard(worst);
                break;
            case EffectOp.ExhaustHand:
                {
                    var all = Hand.ToList();
                    ctx.HandExhausted = all.Count;
                    foreach (CardDef c in all) ExhaustCard(c);
                    break;
                }
            case EffectOp.ExhaustNonAttacksForBlock:
                foreach (CardDef c in Hand.Where(c => c.Kind != CardKind.Attack).ToList())
                {
                    ExhaustCard(c);
                    GainBlockFromCard(amount, card);
                }
                break;
            case EffectOp.UpgradeChosenInHand:
                if (CardChoices.BestToUpgrade(Hand) is { } best) Upgrade(Hand, best);
                break;
            case EffectOp.UpgradeAllInHand:
                foreach (CardDef c in Hand.ToList())
                    if (c.UpgradedForm != null) Upgrade(Hand, c);
                break;
            case EffectOp.CopyToDiscard: DiscardPile.Add(Clone(card)); break;
            case EffectOp.PlayTopOfDraw:
                for (int i = 0; i < amount && Result == CombatResult.Ongoing; i++)
                {
                    if ((DrawPile.Count == 0 && !ReshuffleDiscard()) || DrawPile.Count == 0) break;
                    CardDef top = DrawPile[^1];
                    DrawPile.RemoveAt(DrawPile.Count - 1);
                    AutoPlay(top, forceExhaust: effect.Hits == 1, alreadyRemoved: true);
                }
                break;
            case EffectOp.DiscardToDrawTop:
                if (CardChoices.BestToRecall(DiscardPile) is { } recalled)
                {
                    DiscardPile.Remove(recalled);
                    DrawPile.Add(recalled);
                }
                break;
            case EffectOp.GenerateForExhausted:
                {
                    IReadOnlyList<CardDef> pool = _services?.CardPool?.Invoke(Character) ?? Array.Empty<CardDef>();
                    if (pool.Count == 0) break;
                    for (int i = 0; i < ctx.HandExhausted; i++)
                    {
                        CardDef made = pool[Rng.Next(pool.Count)].Instantiate();
                        if (effect.Hits == 1 && made.UpgradedForm != null) made = made.UpgradedForm.Instantiate();
                        AddToHand(made);
                    }
                    break;
                }
            case EffectOp.GenerateFreeAttack:
                {
                    IReadOnlyList<CardDef> pool = _services?.CardPool?.Invoke(Character) ?? Array.Empty<CardDef>();
                    var attacks = pool.Where(c => c.Kind == CardKind.Attack).ToList();
                    if (attacks.Count == 0) break;
                    CardDef made = attacks[Rng.Next(attacks.Count)].Instantiate();
                    made.FreeThisTurn = true;
                    AddToHand(made);
                    break;
                }
            case EffectOp.TransformAttacks:
                {
                    CardDef? rock = _services?.Card("GIANT_ROCK");
                    if (rock == null) break;
                    if (effect.Hits == 1 && rock.UpgradedForm != null) rock = rock.UpgradedForm;
                    for (int i = 0; i < Hand.Count; i++)
                        if (Hand[i].Kind == CardKind.Attack) Hand[i] = rock.Instantiate();
                    break;
                }
            case EffectOp.DrawUntilNonAttack:
                {
                    // Capped: with Hellraiser every drawn Strike is played at once and leaves the hand, so the hand never fills up.
                    CardDef? drawn;
                    int drawnCards = 0;
                    do drawn = DrawOne();
                    while (drawn != null && drawn.Kind == CardKind.Attack && Hand.Count < MaxHandSize && Result == CombatResult.Ongoing && ++drawnCards < 2 * MaxHandSize);
                    break;
                }
            case EffectOp.AbsorbRandomAttack:
                {
                    var attacks = Hand.Where(c => c.Kind == CardKind.Attack).ToList();
                    if (attacks.Count == 0) break;
                    CardDef picked = attacks[Rng.Next(attacks.Count)];
                    int value = 0;
                    foreach (Effect e in picked.Effects)
                        if (IsDamageOp(e.Op))
                        {
                            value = PlayerAttackDamage(EffectAmount(e, picked, null), null);
                            break;
                        }
                    card.BonusDamage += value;
                    ExhaustCard(picked);
                    break;
                }
        }
    }

    private void GainPower(PowerKind kind, int amount)
    {
        if (kind == PowerKind.Unsupported) return;
        switch (kind)
        {
            case PowerKind.TempStrength:
                PlayerPowers[(int)PowerKind.Strength] += amount;
                PlayerPowers[(int)PowerKind.TempStrength] += amount;
                break;
            case PowerKind.TempDexterity:
                PlayerPowers[(int)PowerKind.Dexterity] += amount;
                PlayerPowers[(int)PowerKind.TempDexterity] += amount;
                break;
            case PowerKind.Barricade or PowerKind.Corruption or PowerKind.Hellraiser:
                PlayerPowers[(int)kind] = 1;   // these don't stack
                break;
            case PowerKind.Strength:
                PlayerPowers[(int)kind] += RelicStrengthGain(amount);
                break;
            default:
                PlayerPowers[(int)kind] += amount;
                break;
        }
    }

    /// <summary>A debuff the player puts on an enemy: Artifact can stop it, and Vicious draws when Vulnerable lands.</summary>
    private void ApplyDebuff(Enemy enemy, PowerKind kind, int amount)
    {
        if (!enemy.Alive || kind == PowerKind.Unsupported || amount <= 0) return;
        if (PowerRules.IsDebuff(kind) && enemy.Powers[(int)PowerKind.Artifact] > 0)
        {
            enemy.Powers[(int)PowerKind.Artifact]--;
            return;
        }
        if (kind == PowerKind.TempStrengthDown) enemy.Powers[(int)PowerKind.Strength] -= amount;
        enemy.Powers[(int)kind] += amount;
        if (kind == PowerKind.Vulnerable && PlayerPowers[(int)PowerKind.Vicious] > 0) DrawCards(PlayerPowers[(int)PowerKind.Vicious]);
    }

    // ---- piles ----------------------------------------------------------------------------------------------

    private CardDef Clone(CardDef card)
    {
        CardDef copy = card.Instantiate();
        if (ChangesInPlace(copy)) _cardsChangeInPlace = true;
        copy.BonusDamage = card.BonusDamage;
        return copy;
    }

    private void AddToHand(CardDef card)
    {
        if (Hand.Count < MaxHandSize) Hand.Add(card);
        else DiscardPile.Add(card);
    }

    private void Upgrade(List<CardDef> pile, CardDef card)
    {
        int i = pile.IndexOf(card);
        if (i < 0 || card.UpgradedForm == null) return;
        CardDef up = card.UpgradedForm.Instantiate();
        up.BonusDamage = card.BonusDamage;
        up.CostReductionThisTurn = card.CostReductionThisTurn;
        up.FreeThisTurn = card.FreeThisTurn;
        pile[i] = up;
    }

    private void PullAttacksFromDiscard(int count)
    {
        if (count <= 0) return;
        var attacks = DiscardPile.Where(c => c.Kind == CardKind.Attack).ToList();
        Rng.Shuffle(attacks);
        foreach (CardDef card in attacks.Take(count))
        {
            if (Hand.Count >= MaxHandSize) break;
            DiscardPile.Remove(card);
            Hand.Add(card);
            if (card.UpgradedForm != null) Upgrade(Hand, card);
        }
    }

    private bool ReshuffleDiscard()
    {
        if (DiscardPile.Count == 0) return false;
        DrawPile.AddRange(DiscardPile);
        DiscardPile.Clear();
        Rng.Shuffle(DrawPile);
        EnchantOnShuffle();
        RelicOnShuffle();
        return true;
    }

    /// <summary>Draws one card into hand; null if nothing could be drawn. Hellraiser plays a drawn Strike at once.</summary>
    private CardDef? DrawOne(bool fromHandDraw = false)
    {
        if (!fromHandDraw && PlayerPowers[(int)PowerKind.NoDraw] > 0) return null;
        if (!fromHandDraw && _playerTurn && Has(RelicKind.Fiddle)) return null;   // no drawing during the turn
        if (Hand.Count >= MaxHandSize) return null;
        if ((DrawPile.Count == 0 && !ReshuffleDiscard()) || DrawPile.Count == 0) return null;
        CardDef card = DrawPile[^1];
        DrawPile.RemoveAt(DrawPile.Count - 1);
        Hand.Add(card);
        if (PlayerPowers[(int)PowerKind.Confused] > 0 && card.Cost >= 0) card.RandomCost = Rng.Next(4);
        EnchantOnDraw(card);
        OnCardDrawn(card);
        if (PlayerPowers[(int)PowerKind.Hellraiser] > 0 && card.IsStrike) AutoPlay(card, forceExhaust: false);
        return card;
    }

    private void DrawCards(int count, bool fromHandDraw = false)
    {
        for (int i = 0; i < count; i++)
        {
            if (Result != CombatResult.Ongoing || DrawOne(fromHandDraw) == null) return;
        }
    }

    /// <summary>Moves a card to the exhaust pile from wherever it is, and lets the powers that watch exhausting react.</summary>
    private void ExhaustCard(CardDef card, bool causedByEthereal = false)
    {
        Hand.Remove(card);
        ExhaustPile.Add(card);
        _cardsExhaustedThisTurn++;
        if (PlayerPowers[(int)PowerKind.FeelNoPain] > 0) GainBlockRaw(PlayerPowers[(int)PowerKind.FeelNoPain]);
        if (PlayerPowers[(int)PowerKind.DarkEmbrace] > 0)
        {
            if (causedByEthereal) PlayerPowers[(int)PowerKind.DarkEmbraceEthereal]++;
            else DrawCards(PlayerPowers[(int)PowerKind.DarkEmbrace]);
        }
        if (card.EnergyWhenExhausted > 0) Energy += card.EnergyWhenExhausted;
        RelicOnExhaust(card);
    }
}

/// <summary>How the bot picks a card when an effect asks the player to choose (which to exhaust, upgrade, or put back).</summary>
internal static class CardChoices
{
    /// <summary>A rough worth of having the card in the deck; status and curse cards are worth less than nothing.</summary>
    public static double KeepValue(CardDef card)
    {
        if (card.Kind is CardKind.Status or CardKind.Curse || card.Cost == CardDef.Unplayable) return -50;
        double v = 0;
        foreach (Effect e in card.Effects)
        {
            int hits = Math.Max(1, e.Hits);
            v += e.Op switch
            {
                EffectOp.Damage or EffectOp.DamageAll or EffectOp.DamageRandom => (e.Amount + card.BonusDamage) * hits * 0.3,
                EffectOp.Block => e.Amount * 0.3,
                EffectOp.Draw => e.Amount * 3,
                EffectOp.Energy => e.Amount * 4,
                EffectOp.BuffSelf => e.Power == PowerKind.Strength ? e.Amount * 4 : 6,
                EffectOp.LoseHp => -e.Amount,
                _ => 2,
            };
        }
        if (card.Kind == CardKind.Power) v += 3;
        return v - 0.4 * Math.Max(0, card.Cost);
    }

    public static CardDef? WorstToExhaust(IReadOnlyList<CardDef> hand)
    {
        CardDef? worst = null;
        double lowest = double.MaxValue;
        foreach (CardDef c in hand)
        {
            double v = KeepValue(c);
            if (v < lowest) { lowest = v; worst = c; }
        }
        return worst;
    }

    public static CardDef? BestToUpgrade(IReadOnlyList<CardDef> hand)
    {
        CardDef? best = null;
        double bestGain = double.MinValue;
        foreach (CardDef c in hand)
        {
            if (c.UpgradedForm == null) continue;
            double gain = KeepValue(c.UpgradedForm) - KeepValue(c) + 0.01 * KeepValue(c);
            if (gain > bestGain) { bestGain = gain; best = c; }
        }
        return best;
    }

    public static CardDef? BestToRecall(IReadOnlyList<CardDef> discard)
    {
        CardDef? best = null;
        double bestValue = double.MinValue;
        foreach (CardDef c in discard)
        {
            double v = KeepValue(c);
            if (v > bestValue) { bestValue = v; best = c; }
        }
        return best;
    }
}
