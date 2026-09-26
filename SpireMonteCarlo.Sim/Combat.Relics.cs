namespace SpireMonteCarlo.Sim;

/// <summary>
/// Relic effects during a combat. Trigger timing and amounts follow the decompiled relic classes: "every N cards" relics fire at
/// every multiple of N (not just the first), turn-number relics fire at the start of that turn after block is cleared, and flat
/// HP amounts are multiplied by <see cref="HpScale"/> so they stay in proportion to the scaled HP pool.
/// </summary>
public sealed partial class Combat
{
    private bool[] _relics = new bool[RelicRules.Count];

    /// <summary>Multiplier applied to the player's HP pool by the rollout; flat HP amounts from relics scale with it.</summary>
    public double HpScale { get; } = 1.0;

    public bool Has(RelicKind kind) => _relics[(int)kind];

    private int Scaled(int amount) => (int)Math.Round(amount * HpScale);

    // ---- per-combat relic state ----
    private int _totalAttacks, _totalSkills, _skillsThisTurn, _exhaustedTotal, _cardsLastTurn, _carriedEnergy, _clayBlock, _hpLostThisRound;
    private bool _puzzleUsed, _tongueUsed, _lizardUsed, _vambraceUsed, _permafrostUsed, _ruinedHelmetUsed, _noAttackLastTurn, _postCombatDone;
    private bool _rainbowAttack, _rainbowSkill, _rainbowPower, _rainbowDone, _penNibActive;

    private void SetRelics(IEnumerable<RelicKind>? relics)
    {
        if (relics == null) return;
        foreach (RelicKind k in relics) _relics[(int)k] = true;
    }

    private void CopyRelicState(Combat s)
    {
        _relics = s._relics;   // never changes during a combat
        _rr = s._rr.Clone();
        _totalAttacks = s._totalAttacks; _totalSkills = s._totalSkills; _skillsThisTurn = s._skillsThisTurn; _exhaustedTotal = s._exhaustedTotal;
        _cardsLastTurn = s._cardsLastTurn; _carriedEnergy = s._carriedEnergy; _clayBlock = s._clayBlock; _hpLostThisRound = s._hpLostThisRound;
        _puzzleUsed = s._puzzleUsed; _tongueUsed = s._tongueUsed; _lizardUsed = s._lizardUsed; _vambraceUsed = s._vambraceUsed;
        _permafrostUsed = s._permafrostUsed; _ruinedHelmetUsed = s._ruinedHelmetUsed; _noAttackLastTurn = s._noAttackLastTurn; _postCombatDone = s._postCombatDone;
        _rainbowAttack = s._rainbowAttack; _rainbowSkill = s._rainbowSkill; _rainbowPower = s._rainbowPower; _rainbowDone = s._rainbowDone; _penNibActive = s._penNibActive;
    }

    private void HitAllEnemies(int damage)
    {
        foreach (Enemy e in Enemies.ToList())
            if (Targetable(e)) DamageEnemy(e, damage, fromCard: false);
    }

    // ---- damage modifiers ----

    /// <summary>Extra damage a card's attack gets from relics and low-HP effects, added before Weak and Vulnerable.</summary>
    private int RelicDamageBonus(CardDef? card)
    {
        int bonus = 0;
        if (Has(RelicKind.RedSkull) && Hp * 2 <= MaxHp) bonus += 3;
        if (card != null)
        {
            if (Has(RelicKind.StrikeDummy) && card.IsStrike) bonus += 3;
            if (Has(RelicKind.FakeStrikeDummy) && card.IsStrike) bonus += 1;
            if (Has(RelicKind.MiniatureCannon) && card.Kind == CardKind.Attack && card.Upgraded) bonus += 3;
        }
        return bonus + PlayerPowers[(int)PowerKind.Vigor];
    }

    private double VulnerableMultiplier => Has(RelicKind.PaperPhrog) ? 1.75 : 1.5;

    // ---- starting a turn ----

    /// <summary>Everything relics do at the start of the first turn (the game's start-of-combat hooks).</summary>
    private void RelicCombatStart()
    {
        if (Has(RelicKind.Anchor)) GainBlockRaw(10);
        if (Has(RelicKind.Vajra)) PlayerPowers[(int)PowerKind.Strength] += 1;
        if (Has(RelicKind.OddlySmoothStone)) PlayerPowers[(int)PowerKind.Dexterity] += 1;
        if (Has(RelicKind.Gorget)) PlayerPowers[(int)PowerKind.Plating] += 4;
        if (Has(RelicKind.BronzeScales)) PlayerPowers[(int)PowerKind.Thorns] += 3;
        if (Has(RelicKind.Akabeko)) PlayerPowers[(int)PowerKind.Vigor] += 8;
        if (Has(RelicKind.Lantern)) Energy += 1;
        if (Has(RelicKind.BoomingConch) && Stakes == 1) Energy += 1;
        if (Has(RelicKind.BloodVial)) Heal(Scaled(2));
        if (Has(RelicKind.Pantograph) && Stakes == 2) Heal(Scaled(25));
        if (Has(RelicKind.BagOfMarbles))
            foreach (Enemy e in Enemies.ToList()) if (Targetable(e)) ApplyDebuff(e, PowerKind.Vulnerable, 1);
        if (Has(RelicKind.RedMask))
            foreach (Enemy e in Enemies.ToList()) if (Targetable(e)) ApplyDebuff(e, PowerKind.Weak, 1);
        if (Has(RelicKind.FestivePopper)) HitAllEnemies(9);
        if (Has(RelicKind.StoneCracker))
        {
            var upgradable = DrawPile.Where(c => c.UpgradedForm != null).ToList();
            Rng.Shuffle(upgradable);
            foreach (CardDef c in upgradable.Take(2)) Upgrade(DrawPile, c);
        }
    }

    /// <summary>Relics that act every turn: extra energy and block on particular turns, and how many extra cards to draw.</summary>
    private int RelicTurnStart()
    {
        int extraDraw = 0;
        if (Has(RelicKind.IceCream)) Energy += _carriedEnergy;
        if (Has(RelicKind.Candelabra) && Turn == 2) Energy += 2;
        if (Has(RelicKind.Chandelier) && Turn == 3) Energy += 3;
        if (Has(RelicKind.HappyFlower) && Turn % 3 == 0) Energy += 1;
        if (Has(RelicKind.ArtOfWar) && Turn > 1 && _noAttackLastTurn) Energy += 1;
        if (Has(RelicKind.HornCleat) && Turn == 2) GainBlockRaw(14);
        if (Has(RelicKind.CaptainsWheel) && Turn == 3) GainBlockRaw(18);
        if (Has(RelicKind.SparklingRouge) && Turn == 3)
        {
            PlayerPowers[(int)PowerKind.Strength] += 1;
            PlayerPowers[(int)PowerKind.Dexterity] += 1;
        }
        if (_clayBlock > 0) { GainBlockRaw(_clayBlock); _clayBlock = 0; }
        if (Has(RelicKind.BagOfPreparation) && Turn == 1) extraDraw += 2;
        if (Has(RelicKind.BoomingConch) && Turn == 1 && Stakes == 1) extraDraw += 2;
        if (Has(RelicKind.Pocketwatch) && Turn > 1 && _cardsLastTurn <= 3) extraDraw += 3;
        if (Has(RelicKind.MercuryHourglass)) HitAllEnemies(3);
        _carriedEnergy = 0;
        _hpLostThisRound = 0;
        _tongueUsed = false;
        _skillsThisTurn = 0;
        _rainbowAttack = _rainbowSkill = _rainbowPower = _rainbowDone = false;
        return extraDraw;
    }

    private void RelicAfterDraw()
    {
        if (Has(RelicKind.Bellows) && Turn == 1)
            foreach (CardDef c in Hand.ToList()) if (c.UpgradedForm != null) Upgrade(Hand, c);
        if (Has(RelicKind.Pendulum) && Turn % 3 == 0) DrawCards(1);
    }

    // ---- playing cards ----

    /// <summary>Counts an attack that is about to resolve (Pen Nib doubles every 10th).</summary>
    private void RelicBeforeAttack()
    {
        _totalAttacks++;
        _penNibActive = Has(RelicKind.PenNib) && _totalAttacks % 10 == 0;
    }

    /// <summary>After a card has resolved: the "every N cards" relics and the ones that watch for particular kinds of card.</summary>
    private void RelicAfterCard(CardDef card, int costPaid)
    {
        _penNibActive = false;
        if (Has(RelicKind.IntimidatingHelmet) && costPaid >= 2) GainBlockRaw(4);
        switch (card.Kind)
        {
            case CardKind.Attack:
                PlayerPowers[(int)PowerKind.Vigor] = 0;
                if (Has(RelicKind.Nunchaku) && _totalAttacks % 10 == 0) Energy += 1;
                if (_attacksPlayedThisTurn % 3 == 0)
                {
                    if (Has(RelicKind.Shuriken)) PlayerPowers[(int)PowerKind.Strength] += 1;
                    if (Has(RelicKind.Kunai)) PlayerPowers[(int)PowerKind.Dexterity] += 1;
                    if (Has(RelicKind.OrnamentalFan)) GainBlockRaw(4);
                    if (Has(RelicKind.Kusarigama))
                    {
                        var alive = Enemies.Where(Targetable).ToList();
                        if (alive.Count > 0) DamageEnemy(alive[Rng.Next(alive.Count)], 6, fromCard: false);
                    }
                }
                _rainbowAttack = true;
                break;
            case CardKind.Skill:
                _totalSkills++;
                _skillsThisTurn++;
                if (Has(RelicKind.TuningFork) && _totalSkills % 10 == 0) GainBlockRaw(7);
                if (Has(RelicKind.LetterOpener) && _skillsThisTurn % 3 == 0) HitAllEnemies(5);
                _rainbowSkill = true;
                break;
            case CardKind.Power:
                if (Has(RelicKind.GamePiece)) DrawCards(1);
                if (Has(RelicKind.MummifiedHand))
                {
                    // Mummified Hand: another card in hand that costs energy becomes free this turn.
                    var costly = Hand.Where(c => c != card && c.CurrentCost >= 1 && !c.FreeThisTurn).ToList();
                    if (costly.Count > 0) costly[Rng.Next(costly.Count)].FreeThisTurn = true;
                }
                if (Has(RelicKind.Permafrost) && !_permafrostUsed) { _permafrostUsed = true; GainBlockRaw(7); }
                _rainbowPower = true;
                break;
        }
        if (Has(RelicKind.RainbowRing) && !_rainbowDone && _rainbowAttack && _rainbowSkill && _rainbowPower)
        {
            _rainbowDone = true;
            PlayerPowers[(int)PowerKind.Strength] += 1;
            PlayerPowers[(int)PowerKind.Dexterity] += 1;
        }
    }

    /// <summary>Vambrace doubles the first block a card gives each combat.</summary>
    private int RelicBlockFromCard(int amount)
    {
        if (amount > 0 && Has(RelicKind.Vambrace) && !_vambraceUsed)
        {
            _vambraceUsed = true;
            return amount * 2;
        }
        return amount;
    }

    private int RelicStrengthGain(int amount)
    {
        if (amount > 0 && Has(RelicKind.RuinedHelmet) && !_ruinedHelmetUsed)
        {
            _ruinedHelmetUsed = true;
            return amount * 2;
        }
        return amount;
    }

    private void RelicOnExhaust(CardDef card)
    {
        RelicOnExhaustCard(card);
        if (Has(RelicKind.CharonsAshes)) HitAllEnemies(3);
        _exhaustedTotal++;
        if (Has(RelicKind.JossPaper) && _exhaustedTotal % 5 == 0) DrawCards(1);
    }

    private void RelicOnEnemyDeath()
    {
        if (!Has(RelicKind.GremlinHorn)) return;
        Energy += 1;
        DrawCards(1);
    }

    // ---- losing HP ----

    /// <summary>Relics that shrink or cap HP loss. Returns what is actually lost.</summary>
    private int RelicReduceHpLoss(int amount)
    {
        if (Has(RelicKind.TungstenRod)) amount = Math.Max(0, amount - Scaled(1));
        if (amount > 0 && Has(RelicKind.BeatingRemnant)) amount = Math.Max(0, Math.Min(amount, Scaled(20) - _hpLostThisRound));
        return amount;
    }

    private void RelicAfterHpLost(int amount)
    {
        _hpLostThisRound += amount;
        if (Hp <= 0) return;
        if (Has(RelicKind.CentennialPuzzle) && !_puzzleUsed) { _puzzleUsed = true; DrawCards(3); }
        if (Has(RelicKind.DemonTongue) && _playerTurn && !_tongueUsed) { _tongueUsed = true; Heal(amount); }
        if (Has(RelicKind.SelfFormingClay)) _clayBlock += 3;
    }

    /// <summary>Lizard Tail (once) and Fairy in a Bottle bring the player back from 0 HP.</summary>
    private bool TryRevive()
    {
        if (Has(RelicKind.LizardTail) && !_lizardUsed)
        {
            _lizardUsed = true;
            Hp = Math.Max(1, MaxHp / 2);
            return true;
        }
        return TryFairyInABottle();
    }

    // ---- ending a turn ----

    /// <summary>Relics that add block or damage as the player's turn ends. Returns via the block the player will have.</summary>
    private void RelicEndOfTurn(bool hadNoBlock)
    {
        if (Has(RelicKind.Orichalcum) && hadNoBlock) GainBlockRaw(6);
        if (Has(RelicKind.CloakClasp)) GainBlockRaw(Hand.Count);
        if (Has(RelicKind.RippleBasin) && _attacksPlayedThisTurn == 0) GainBlockRaw(4);
        if (Has(RelicKind.StoneCalendar) && Turn == 7) HitAllEnemies(52);
        if (Has(RelicKind.ParryingShield) && Block >= 10)
        {
            var alive = Enemies.Where(Targetable).ToList();
            if (alive.Count > 0) DamageEnemy(alive[Rng.Next(alive.Count)], 6, fromCard: false);
        }
        _noAttackLastTurn = _attacksPlayedThisTurn == 0;
        _cardsLastTurn = CardsPlayedThisTurn;
        if (Has(RelicKind.IceCream)) _carriedEnergy = Energy;
    }

    /// <summary>After the fight is won: Meat on the Bone, then Burning Blood.</summary>
    private void RelicAfterVictory()
    {
        if (_postCombatDone) return;
        _postCombatDone = true;
        if (Has(RelicKind.MeatOnTheBone) && Hp * 2 <= MaxHp) Heal(Scaled(12));
        if (Has(RelicKind.BurningBlood)) Heal(Scaled(6));
        RelicAfterVictoryExtra();
    }
}
