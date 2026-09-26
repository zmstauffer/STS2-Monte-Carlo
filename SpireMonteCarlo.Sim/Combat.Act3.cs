namespace SpireMonteCarlo.Sim;

/// <summary>
/// Act 3 rules that act on the player's cards, written from the decompiled power classes (v0.111): Chains of Binding (the Queen's Puppet
/// Strings), Dampen (the Magi Knight), Withering Presence and Wither's growth (Aeonglass). The monsters' own parts are in MonsterBehaviorsAct3.cs.
/// </summary>
public sealed partial class Combat
{
    // Chains of Binding: the first N cards drawn on the player's turn that can be afflicted become Bound, and only one Bound card can be
    // played per turn; Bound clears as the turn ends. Card objects are shared between a combat and its copies (and repeated copies of a card
    // are one object), so Bound copies are counted per card (id, upgraded) instead of flagged on the card.
    private Dictionary<(string, bool), int>? _bound;
    private int _boundThisTurn;
    private bool _boundPlayed;

    // Withering Presence: every 6 cards the player plays, a Wither goes to the hand; each Increasing Intensity makes every Wither deal 3 more.
    private int _witheringCountdown;
    private int _witherLevel;

    // Dampen: how many Magi Knights have cast it and are alive (it lifts when the last one dies).
    private int _dampenCasters;

    private void CopyAct3State(Combat s)
    {
        _bound = s._bound == null ? null : new Dictionary<(string, bool), int>(s._bound);
        _boundThisTurn = s._boundThisTurn;
        _boundPlayed = s._boundPlayed;
        _witheringCountdown = s._witheringCountdown;
        _witherLevel = s._witherLevel;
        _dampenCasters = s._dampenCasters;
    }

    private static bool CanBeBound(CardDef card) => card.Kind is not (CardKind.Status or CardKind.Curse) && card.Cost != CardDef.Unplayable;

    private void BindOnDraw(CardDef card)
    {
        int chains = PlayerPowers[(int)PowerKind.ChainsOfBinding];
        if (chains <= 0 || !_playerTurn || _boundThisTurn >= chains || !CanBeBound(card)) return;
        _bound ??= new();
        var key = (card.Id, card.Upgraded);
        _bound[key] = _bound.GetValueOrDefault(key) + 1;
        _boundThisTurn++;
    }

    /// <summary>How many copies of this card in hand are Bound (at most the copies in hand).</summary>
    private int BoundCopies(CardDef card) =>
        _bound == null ? 0 : Math.Min(_bound.GetValueOrDefault((card.Id, card.Upgraded)), Hand.Count(c => c.Id == card.Id && c.Upgraded == card.Upgraded));

    /// <summary>False when every copy of the card in hand is Bound and a Bound card was already played this turn.</summary>
    private bool BoundAllows(CardDef card)
    {
        if (!_boundPlayed || _bound == null) return true;
        int copies = Hand.Count(c => c.Id == card.Id && c.Upgraded == card.Upgraded);
        return copies == 0 || BoundCopies(card) < copies;
    }

    /// <summary>Before a card leaves the hand to be played: an unbound copy is used first; playing a Bound one uses up the turn's Bound play.</summary>
    private void OnPlayedFromHand(CardDef card)
    {
        if (_bound == null) return;
        int copies = Hand.Count(c => c.Id == card.Id && c.Upgraded == card.Upgraded);
        int bound = BoundCopies(card);
        if (bound == 0 || copies > bound) return;
        _bound[(card.Id, card.Upgraded)] = bound - 1;
        _boundPlayed = true;
    }

    private void ClearBound()
    {
        _bound = null;
        _boundThisTurn = 0;
        _boundPlayed = false;
    }

    /// <summary>Aeonglass puts Withering Presence on the player at the start of the fight.</summary>
    public void StartWitheringPresence() => _witheringCountdown = 6;

    /// <summary>Aeonglass's Increasing Intensity: every Wither, in any pile or still to come, deals 3 more damage.</summary>
    public void RaiseWitherLevel() => _witherLevel++;

    private void WitheringPresenceAfterPlay()
    {
        if (_witheringCountdown <= 0 || !Enemies.Any(e => e.Alive && e.Def.Id == "AEONGLASS")) return;
        if (--_witheringCountdown > 0) return;
        _witheringCountdown = 6;
        if (_services?.Card("WITHER") is CardDef wither) AddToHand(wither.Instantiate());
    }

    private int WitherBonus(CardDef card) => card.Id == "WITHER" ? 3 * _witherLevel : 0;

    /// <summary>The Magi Knight's Dampen: every upgraded card in the fight is downgraded until the last caster dies.</summary>
    public void Dampen()
    {
        _dampenCasters++;
        if (_dampenCasters > 1 || _services == null) return;
        foreach (List<CardDef> pile in new[] { DrawPile, Hand, DiscardPile, ExhaustPile })
            for (int i = 0; i < pile.Count; i++)
                if (pile[i].Upgraded && _services.Card(pile[i].Id) is CardDef plain && !plain.Upgraded)
                {
                    CardDef down = plain.Instantiate();
                    down.DampenedFrom = pile[i];
                    pile[i] = down;
                }
    }

    /// <summary>A Dampen caster died: when none is left, the downgraded cards get their upgrades back.</summary>
    public void DampenCasterDied()
    {
        if (_dampenCasters <= 0 || --_dampenCasters > 0) return;
        foreach (List<CardDef> pile in new[] { DrawPile, Hand, DiscardPile, ExhaustPile })
            for (int i = 0; i < pile.Count; i++)
                if (pile[i].DampenedFrom is CardDef up) pile[i] = up;
    }

    /// <summary>Whether a monster that is down but due to come back keeps the fight going (the Test Subject's Adaptable).</summary>
    private static bool HoldsFight(Enemy e) => e.Reviving && e.Powers[(int)PowerKind.Adaptable] > 0;

    /// <summary>Hits of the enemy's move this turn, including hits it has added to it during the fight (the Test Subject's Multi Claw).</summary>
    public int MoveHits(Enemy e, MoveDef move) => move.HitsAt(Ascension) + e.ExtraHits;
}
