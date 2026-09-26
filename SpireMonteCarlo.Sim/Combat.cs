namespace SpireMonteCarlo.Sim;

public enum CombatResult { Ongoing, Won, Lost }

/// <summary>Lookups the combat needs to create things mid-fight: cards (status cards from monsters, generated cards), and monsters they spawn.</summary>
public sealed record CombatServices(
    Func<string, CardDef?> Card,
    Func<string, MonsterDef?> Monster,
    Func<string, IReadOnlyList<CardDef>>? CardPool = null);

/// <summary>
/// One combat between the player and a group of enemies. Rules follow the game's own code (checked against the
/// decompiled v0.111 classes): Vulnerable +50% damage taken, Weak -25% damage dealt, Frail -25% block
/// gained, and those three tick down when the enemy side finishes its turn. Card behavior lives in Combat.Cards.cs,
/// monster mechanics (Slow, Asleep, Skittish, Hardened Shell, ...) follow their power classes. A combat owns all its
/// state, so many can run in parallel.
/// </summary>
public sealed partial class Combat
{
    public const int HandSize = 5;
    public const int MaxHandSize = 10;
    public const int TurnLimit = 80;

    private readonly CombatServices? _services;

    public SimRng Rng { get; }
    public int Ascension { get; }
    public string Character { get; }
    public bool ToughEnemies => Ascension >= 8;
    public bool DeadlyEnemies => Ascension >= 9;

    public int Hp { get; private set; }
    public int MaxHp { get; private set; }
    public int Block { get; private set; }
    public int Energy { get; private set; }
    public int MaxEnergy { get; }
    public int Turn { get; private set; }
    public int[] PlayerPowers { get; } = new int[PowerRules.Count];

    public List<CardDef> DrawPile { get; } = new();
    public List<CardDef> Hand { get; } = new();
    public List<CardDef> DiscardPile { get; } = new();
    public List<CardDef> ExhaustPile { get; } = new();
    public List<Enemy> Enemies { get; } = new();

    public CombatResult Result { get; private set; }
    public int HpLost { get; private set; }
    public int MaxHpGained { get; private set; }
    public int CardsPlayed { get; private set; }
    public int CardsPlayedThisTurn { get; private set; }

    public int AliveEnemies => Enemies.Count(e => e.Alive);

    /// <summary>
    /// Global multiplier on damage enemies deal. 1.0 is the game as written; below 1.0 stands in for player advantages the
    /// simulator does not model (potions, relics, better play), tuned so whole-act results match real players.
    /// </summary>
    public double EnemyDamageScale { get; }

    /// <summary>The potions the player carries into and out of this fight.</summary>
    public List<PotionDef> Potions { get; } = new();

    /// <summary>How much the fight matters (0 normal, 1 elite, 2 boss); the bot spends potions more freely when it is higher.</summary>
    public int Stakes { get; }

    /// <summary>A copy of the whole combat as it stands, including its random stream, for trying plays out. <paramref name="salt"/> (non-zero) makes the copy's random draws differ from the real ones.</summary>
    public Combat Clone(ulong salt = 0)
    {
        if (!_pilesScanned)
        {
            _pilesScanned = true;
            if (!_cardsChangeInPlace)
                _cardsChangeInPlace = DrawPile.Concat(Hand).Concat(DiscardPile).Concat(ExhaustPile).Any(ChangesInPlace);
        }
        return new Combat(this, salt);
    }

    private Combat(Combat src, ulong salt)
    {
        Rng = src.Rng.Clone();
        if (salt != 0) Rng.Perturb(salt);
        Ascension = src.Ascension;
        Character = src.Character;
        EnemyDamageScale = src.EnemyDamageScale;
        _services = src._services;
        MaxEnergy = src.MaxEnergy;
        Stakes = src.Stakes;
        HpScale = src.HpScale;
        CopyRelicState(src);
        Hp = src.Hp; MaxHp = src.MaxHp; Block = src.Block; Energy = src.Energy; Turn = src.Turn;
        Array.Copy(src.PlayerPowers, PlayerPowers, PlayerPowers.Length);
        // Cards in the hand are the ones a play can change in place; the other piles only ever have cards moved between them, so a
        // copy can share the card objects unless some card is changed in place wherever it lies (Stomp's cost reduction).
        _cardsChangeInPlace = src._cardsChangeInPlace;
        _pilesScanned = true;
        foreach (CardDef c in src.Hand) Hand.Add(c.Copy());
        if (_cardsChangeInPlace)
        {
            foreach (CardDef c in src.DrawPile) DrawPile.Add(c.Copy());
            foreach (CardDef c in src.DiscardPile) DiscardPile.Add(c.Copy());
            foreach (CardDef c in src.ExhaustPile) ExhaustPile.Add(c.Copy());
        }
        else
        {
            DrawPile.AddRange(src.DrawPile);
            DiscardPile.AddRange(src.DiscardPile);
            ExhaustPile.AddRange(src.ExhaustPile);
        }
        foreach (Enemy e in src.Enemies) Enemies.Add(e.Copy());
        Potions.AddRange(src.Potions);
        Result = src.Result; HpLost = src.HpLost; MaxHpGained = src.MaxHpGained; CardsPlayed = src.CardsPlayed; CardsPlayedThisTurn = src.CardsPlayedThisTurn;
        _playerTurn = src._playerTurn; _attacksPlayedThisTurn = src._attacksPlayedThisTurn; _cardBlockGainsThisTurn = src._cardBlockGainsThisTurn;
        _cardsExhaustedThisTurn = src._cardsExhaustedThisTurn; _lostHpThisTurn = src._lostHpThisTurn; _timesHurt = src._timesHurt;
        _cardsInPlay = src._cardsInPlay; _pendingRupture = src._pendingRupture;
    }

    public Combat(IEnumerable<CardDef> deck, int hp, int maxHp, IEnumerable<MonsterDef> monsters, int ascension, ulong seed,
        int maxEnergy = 3, IReadOnlyList<int>? altStarts = null, double enemyDamageScale = 1.0, CombatServices? services = null,
        string character = "ironclad", IEnumerable<PotionDef>? potions = null, IEnumerable<RelicKind>? relics = null, int stakes = 0, double hpScale = 1.0)
    {
        if (potions != null) Potions.AddRange(potions);
        SetRelics(relics);
        Stakes = stakes;
        HpScale = hpScale;
        Rng = new SimRng(seed);
        Ascension = ascension;
        Character = character;
        EnemyDamageScale = enemyDamageScale;
        _services = services;
        Hp = hp;
        MaxHp = maxHp;
        MaxEnergy = maxEnergy;

        var cards = deck.Select(c => c.Instantiate()).ToList();
        _cardsChangeInPlace = cards.Any(ChangesInPlace);
        Rng.Shuffle(cards);
        // Innate cards start on top of the draw pile.
        DrawPile.AddRange(cards.Where(c => !c.Innate));
        DrawPile.AddRange(cards.Where(c => c.Innate));

        foreach (MonsterDef def in monsters) Enemies.Add(CreateEnemy(def));

        // Monsters whose first move depends on a starter index (slugs, rats, ...) get consecutive indices from a random start.
        List<Enemy> starters = Enemies.Where(e => e.Def.StarterSwitch.Length > 0).ToList();
        if (starters.Count > 0)
        {
            int k = starters[0].Def.StarterSwitch.Length;
            int first = Rng.Next(k);
            for (int j = 0; j < starters.Count; j++) starters[j].StarterIndex = (first + j) % k;
        }
        if (altStarts != null)
            foreach (int index in altStarts)
                if (index >= 0 && index < Enemies.Count) Enemies[index].AltStart = true;
        foreach (Enemy e in Enemies) e.Start(this);
        foreach (Enemy e in Enemies.ToList()) MonsterBehaviors.For(e.Def.Id)?.OnStart(this, e);

        StartPlayerTurn();
    }

    private Enemy CreateEnemy(MonsterDef def)
    {
        (int lo, int hi) = ToughEnemies ? (def.HpMinTough, def.HpMaxTough) : (def.HpMin, def.HpMax);
        int enemyHp = Rng.NextInclusive(lo, Math.Max(lo, hi));
        var enemy = new Enemy { Def = def, Index = Enemies.Count, Hp = enemyHp, MaxHp = enemyHp };
        foreach (InnatePower innate in def.Innate)
        {
            enemy.Powers[(int)innate.Power] += innate.At(Ascension);
            if (innate.Power == PowerKind.Ritual) enemy.RitualSkip = true;
        }
        enemy.Block += def.InnateBlockAscension > 0 && Ascension >= def.InnateBlockAscension ? def.InnateBlockAlt : def.InnateBlock;
        return enemy;
    }

    // ---- queries the bot uses -------------------------------------------------------------------------------

    /// <summary>What playing the card would cost right now, after Corruption, free-this-turn, and Free Attack.</summary>
    public int EffectiveCost(CardDef card)
    {
        if (card.Cost == CardDef.Unplayable) return CardDef.Unplayable;
        if (card.Cost == CardDef.XCost) return CardDef.XCost;
        if (card.FreeThisTurn) return 0;
        if (PlayerPowers[(int)PowerKind.Corruption] > 0 && card.Kind == CardKind.Skill) return 0;
        if (PlayerPowers[(int)PowerKind.FreeAttack] > 0 && card.Kind == CardKind.Attack) return 0;
        return card.CurrentCost;
    }

    public bool CanPlay(CardDef card)
    {
        if (Result != CombatResult.Ongoing || card.Cost == CardDef.Unplayable) return false;
        if (PlayerPowers[(int)PowerKind.Ringing] > 0 && CardsPlayedThisTurn >= 1) return false;
        int cost = EffectiveCost(card);
        return cost == CardDef.XCost || cost <= Energy;
    }

    /// <summary>Damage one attack of <paramref name="baseDamage"/> would deal to <paramref name="target"/> right now (null: ignore the target's powers).</summary>
    public int PlayerAttackDamage(int baseDamage, Enemy? target, CardDef? card = null)
    {
        double d = Math.Max(0, baseDamage + PlayerPowers[(int)PowerKind.Strength] + RelicDamageBonus(card));
        if (PlayerPowers[(int)PowerKind.Weak] > 0) d *= 0.75;
        if (target != null)
        {
            if (target.Powers[(int)PowerKind.Vulnerable] > 0) d *= VulnerableMultiplier + PlayerPowers[(int)PowerKind.Cruelty] / 100.0;
            if (target.Powers[(int)PowerKind.Slow] > 0) d *= 1 + 0.1 * target.SlowCards;
        }
        if (_penNibActive) d *= 2;
        return (int)Math.Floor(d);
    }

    /// <summary>Block a card of <paramref name="baseBlock"/> would give right now, with Dexterity, Frail, and Unmovable.</summary>
    public int PlayerBlockGain(int baseBlock)
    {
        double b = Math.Max(0, baseBlock + PlayerPowers[(int)PowerKind.Dexterity]);
        if (PlayerPowers[(int)PowerKind.Frail] > 0) b *= 0.75;
        if (PlayerPowers[(int)PowerKind.Unmovable] > 0 && _cardBlockGainsThisTurn < PlayerPowers[(int)PowerKind.Unmovable]) b *= 2;
        return (int)Math.Floor(b);
    }

    /// <summary>Damage one hit of an enemy attack of <paramref name="baseDamage"/> would deal to the player right now.</summary>
    public int EnemyAttackDamage(int baseDamage, Enemy attacker)
    {
        double d = Math.Max(0, baseDamage + attacker.Powers[(int)PowerKind.Strength] + attacker.Powers[(int)PowerKind.Vigor]);
        if (attacker.Powers[(int)PowerKind.Weak] > 0) d *= 0.75;
        if (PlayerPowers[(int)PowerKind.Vulnerable] > 0) d *= 1.5;
        if (PlayerPowers[(int)PowerKind.Colossus] > 0 && attacker.Powers[(int)PowerKind.Vulnerable] > 0) d *= 0.5;
        return (int)Math.Floor(d * EnemyDamageScale);
    }

    /// <summary>Whether the enemy's visible move is an attack (including the Waterfall Giant's explosion).</summary>
    public bool IntendsAttack(Enemy e) => e.Move != null && (e.Move.IsAttack || (e.Move.Id == "EXPLODE" && e.ExplodeDamage > 0));

    /// <summary>Base damage of one hit of the enemy's visible move, before Strength, Weak, and Vulnerable.</summary>
    public int MoveBaseDamage(Enemy e)
    {
        MoveDef? move = e.Move;
        if (move == null) return 0;
        if (move.Id == "EXPLODE") return e.ExplodeDamage;
        int damage = move.DamageAt(Ascension);
        return move.Id == "PRESSURE_GUN" ? damage + e.GunBonus : damage;
    }

    /// <summary>Damage the enemy's visible move will do to the player if nothing is blocked.</summary>
    public int IntentDamage(Enemy e) => !IntendsAttack(e) ? 0 : (e.Move!.Id == "EXPLODE" ? 1 : e.Move.HitsAt(Ascension)) * EnemyAttackDamage(MoveBaseDamage(e), e);

    /// <summary>Total damage the enemies' visible intents will do to the player if nothing is blocked.</summary>
    public int IncomingDamage() => Enemies.Where(e => e.Alive).Sum(IntentDamage);

    // ---- turn flow ------------------------------------------------------------------------------------------

    public void EndPlayerTurn()
    {
        if (Result != CombatResult.Ongoing) return;

        // Powers that pay out as the turn ends.
        bool hadNoBlock = Block == 0;
        GainBlockRaw(PlayerPowers[(int)PowerKind.Plating] + PlayerPowers[(int)PowerKind.Metallicize]);
        RelicEndOfTurn(hadNoBlock);

        // After the player's last card: Stampede and Howl from Beyond play cards on their own.
        for (int i = 0; i < PlayerPowers[(int)PowerKind.Stampede] && Result == CombatResult.Ongoing; i++)
        {
            var attacks = Hand.Where(c => c.Kind == CardKind.Attack && c.Cost != CardDef.Unplayable).ToList();
            if (attacks.Count == 0) break;
            AutoPlay(attacks[Rng.Next(attacks.Count)], forceExhaust: false);
        }
        foreach (CardDef card in ExhaustPile.Where(c => c.PlaysFromExhaustPile).ToList())
        {
            if (Result != CombatResult.Ongoing) break;
            ExhaustPile.Remove(card);
            AutoPlay(card, forceExhaust: false, alreadyRemoved: true);
        }
        if (Result != CombatResult.Ongoing) return;

        // Status cards still in hand hurt (Burn, Infection, Toxic, ...).
        foreach (CardDef card in Hand.ToList())
        {
            if (card.EndTurnDamage > 0) HitPlayer(card.EndTurnDamage, null);
            if (card.EndTurnHpLoss > 0) LoseHp(card.EndTurnHpLoss);
        }
        if (Result != CombatResult.Ongoing) return;

        foreach (CardDef card in Hand.ToList())
        {
            if (card.Ethereal) ExhaustCard(card, causedByEthereal: true);
            else if (!card.Retain) DiscardPile.Add(card);
            else continue;
            Hand.Remove(card);
        }
        _playerTurn = false;

        // Powers that end with the player's turn.
        PlayerPowers[(int)PowerKind.Rage] = 0;
        PlayerPowers[(int)PowerKind.NoDraw] = 0;
        PlayerPowers[(int)PowerKind.OneTwoPunch] = 0;
        PlayerPowers[(int)PowerKind.Ringing] = 0;   // it only limits the one turn it was applied for
        PlayerPowers[(int)PowerKind.Strength] -= PlayerPowers[(int)PowerKind.TempStrength];
        PlayerPowers[(int)PowerKind.TempStrength] = 0;
        PlayerPowers[(int)PowerKind.Dexterity] -= PlayerPowers[(int)PowerKind.TempDexterity];
        PlayerPowers[(int)PowerKind.TempDexterity] = 0;
        int ethereal = PlayerPowers[(int)PowerKind.DarkEmbraceEthereal];
        PlayerPowers[(int)PowerKind.DarkEmbraceEthereal] = 0;
        if (ethereal > 0) DrawCards(PlayerPowers[(int)PowerKind.DarkEmbrace] * ethereal);

        RunEnemyTurn();
        CheckEnd();
        if (Result == CombatResult.Ongoing)
        {
            if (Turn >= TurnLimit) { Result = CombatResult.Lost; return; }
            StartPlayerTurn();
        }
    }

    private void StartPlayerTurn()
    {
        Turn++;
        _playerTurn = true;
        CardsPlayedThisTurn = 0;
        _attacksPlayedThisTurn = 0;
        _cardBlockGainsThisTurn = 0;
        _cardsExhaustedThisTurn = 0;
        _lostHpThisTurn = false;
        if (PlayerPowers[(int)PowerKind.Barricade] == 0) Block = Has(RelicKind.SturdyClamp) ? Math.Min(Block, 10) : 0;
        if (Turn > 1 && PlayerPowers[(int)PowerKind.Plating] > 0) PlayerPowers[(int)PowerKind.Plating]--;
        foreach (Enemy e in Enemies)
        {
            e.SkittishUsed = false;
            e.ShellDamage = 0;
        }
        foreach (var pile in new[] { DrawPile, Hand, DiscardPile, ExhaustPile })
            foreach (CardDef c in pile)
            {
                c.CostReductionThisTurn = 0;
                c.FreeThisTurn = false;
            }
        Energy = MaxEnergy + PlayerPowers[(int)PowerKind.Pyre];
        if (Turn == 1) RelicCombatStart();
        int extraDraw = RelicTurnStart();

        PullAttacksFromDiscard(PlayerPowers[(int)PowerKind.Aggression]);
        PlayerPowers[(int)PowerKind.Strength] += PlayerPowers[(int)PowerKind.DemonForm];
        DrawCards(HandSize + extraDraw, fromHandDraw: true);
        RelicAfterDraw();

        if (PlayerPowers[(int)PowerKind.CrimsonMantle] > 0)
        {
            LoseHp(PlayerPowers[(int)PowerKind.CrimsonSelfDamage]);
            GainBlockRaw(PlayerPowers[(int)PowerKind.CrimsonMantle]);
        }
        if (PlayerPowers[(int)PowerKind.Inferno] > 0) LoseHp(PlayerPowers[(int)PowerKind.InfernoSelfDamage]);
        CheckEnd();
    }

    // ---- HP, block, and damage ------------------------------------------------------------------------------

    private void HitPlayer(int damage, Enemy? attacker)
    {
        int absorbed = Math.Min(Block, damage);
        Block -= absorbed;
        LoseHp(damage - absorbed);
        if (attacker != null && PlayerPowers[(int)PowerKind.FlameBarrier] > 0 && attacker.Alive)
            DamageEnemy(attacker, PlayerPowers[(int)PowerKind.FlameBarrier], fromCard: false);
        if (attacker != null && PlayerPowers[(int)PowerKind.Thorns] > 0 && attacker.Alive)
            DamageEnemy(attacker, PlayerPowers[(int)PowerKind.Thorns], fromCard: false);
    }

    /// <summary>HP loss that block can't stop. On the player's own turn it feeds Rupture and Inferno.</summary>
    private void LoseHp(int amount)
    {
        if (amount <= 0) return;
        amount = RelicReduceHpLoss(amount);
        if (amount <= 0) return;
        Hp -= amount;
        HpLost += amount;
        _timesHurt++;
        if (Hp <= 0 && !TryRevive()) Result = CombatResult.Lost;
        RelicAfterHpLost(amount);
        if (!_playerTurn) return;

        _lostHpThisTurn = true;
        if (PlayerPowers[(int)PowerKind.Rupture] > 0)
        {
            // Hurt by a card you are playing: the Strength arrives once the card has finished.
            if (_cardsInPlay > 0) _pendingRupture += PlayerPowers[(int)PowerKind.Rupture];
            else PlayerPowers[(int)PowerKind.Strength] += PlayerPowers[(int)PowerKind.Rupture];
        }
        if (PlayerPowers[(int)PowerKind.Inferno] > 0 && Result == CombatResult.Ongoing)
            foreach (Enemy e in Enemies.ToList())
                if (Targetable(e)) DamageEnemy(e, PlayerPowers[(int)PowerKind.Inferno], fromCard: false);
    }

    private static bool Targetable(Enemy e) => e.Alive && !e.Dying;

    /// <summary>Block from a card (already scaled by Dexterity and Frail); Unmovable doubles the first few each turn.</summary>
    private void GainBlockFromCard(int baseBlock)
    {
        int amount = RelicBlockFromCard(PlayerBlockGain(baseBlock));
        _cardBlockGainsThisTurn++;
        GainBlockRaw(amount);
    }

    /// <summary>Block from a power or effect that Dexterity and Frail don't touch; Juggernaut reacts to every gain.</summary>
    private void GainBlockRaw(int amount)
    {
        if (amount <= 0) return;
        Block += amount;
        if (PlayerPowers[(int)PowerKind.Juggernaut] > 0)
        {
            var alive = Enemies.Where(Targetable).ToList();
            if (alive.Count > 0) DamageEnemy(alive[Rng.Next(alive.Count)], PlayerPowers[(int)PowerKind.Juggernaut], fromCard: false);
        }
    }

    private void Heal(int amount) => Hp = Math.Min(MaxHp, Hp + amount);

    private void GainMaxHp(int amount)
    {
        MaxHp += amount;
        Hp += amount;
        MaxHpGained += amount;
    }

    /// <summary>All damage the player deals to an enemy goes through here, so the enemy's defensive powers apply once. Returns true if it killed the enemy.</summary>
    private bool DamageEnemy(Enemy enemy, int damage, bool fromCard)
    {
        if (!enemy.Alive || enemy.Dying) return false;

        if (enemy.Powers[(int)PowerKind.Intangible] > 0) damage = Math.Min(damage, 1);
        int absorbed = Math.Min(enemy.Block, damage);
        enemy.Block -= absorbed;
        int lost = damage - absorbed;

        if (lost > 0 && enemy.Powers[(int)PowerKind.Slippery] > 0)
        {
            lost = 1;
            enemy.Powers[(int)PowerKind.Slippery]--;
        }
        if (lost > 0 && enemy.Powers[(int)PowerKind.HardenedShell] > 0)
        {
            lost = Math.Max(0, Math.Min(lost, enemy.Powers[(int)PowerKind.HardenedShell] - enemy.ShellDamage));
            enemy.ShellDamage += lost;
        }
        if (lost <= 0) return false;

        enemy.Hp -= lost;
        if (fromCard && enemy.Powers[(int)PowerKind.Skittish] > 0 && !enemy.SkittishUsed)
        {
            enemy.SkittishUsed = true;
            enemy.Block += enemy.Powers[(int)PowerKind.Skittish];
        }

        if (enemy.Hp <= 0)
        {
            OnEnemyDeath(enemy);
            return !enemy.Dying;
        }
        OnEnemyDamaged(enemy);
        MonsterBehaviors.For(enemy.Def.Id)?.OnDamaged(this, enemy, lost);
        return false;
    }

    /// <summary>Powers that react to an enemy losing HP without dying: waking up, phase changes.</summary>
    private void OnEnemyDamaged(Enemy e)
    {
        if (e.Powers[(int)PowerKind.Asleep] > 0)
        {
            // Woken by damage: loses the plating and its next move, then starts attacking.
            e.Powers[(int)PowerKind.Asleep] = 0;
            e.Powers[(int)PowerKind.Plating] = 0;
            e.Stun(FirstExisting(e, "SLASH_MOVE"));
        }
        if (e.Powers[(int)PowerKind.Plow] > 0 && e.Hp <= e.Powers[(int)PowerKind.Plow])
        {
            e.Powers[(int)PowerKind.Plow] = 0;
            e.Powers[(int)PowerKind.Strength] = 0;
            e.Stun(FirstExisting(e, "BEAST_CRY_MOVE"));
        }
        if (e.Powers[(int)PowerKind.Shriek] > 0 && e.Hp <= e.Powers[(int)PowerKind.Shriek])
        {
            e.Powers[(int)PowerKind.Shriek] = 0;
            e.Stun(FirstExisting(e, "TERROR_MOVE"));
        }
    }

    private static string FirstExisting(Enemy e, string stateId) =>
        e.Def.States.ContainsKey(stateId) ? stateId : e.Def.InitialState;

    private void OnEnemyDeath(Enemy e)
    {
        // Some monsters refuse to die (the Decimillipede's segments come back); their behavior keeps the enemy down instead.
        if (MonsterBehaviors.For(e.Def.Id)?.OnDeath(this, e) == true) return;

        if (e.Powers[(int)PowerKind.SteamEruption] > 0 && !e.Dying)
        {
            // The Waterfall Giant refuses to die: it becomes untouchable and explodes with all the pressure it built up.
            e.Dying = true;
            e.Hp = 999_999;
            e.Block = 0;
            e.SetMoveNow(FirstExisting(e, "ABOUT_TO_BLOW_MOVE"), this);
            return;
        }
        e.Hp = 0;
        RelicOnEnemyDeath();
        foreach (Enemy ally in Enemies.ToList())
            if (ally != e && ally.Alive) MonsterBehaviors.For(ally.Def.Id)?.OnAllyDeath(this, ally, e);
        if (e.Powers[(int)PowerKind.Infested] > 0 && _services?.Monster("WRIGGLER") is MonsterDef wriggler)
        {
            for (int i = 0; i < 4; i++)
            {
                Enemy spawned = CreateEnemy(wriggler);
                spawned.AltStart = true;    // they arrive stunned
                spawned.SlotName = $"wriggler{i + 1}";
                Enemies.Add(spawned);
                spawned.Start(this);
            }
        }
    }

    // ---- enemy turn -----------------------------------------------------------------------------------------

    private void AddCards(CardAdd add)
    {
        CardDef? template = _services?.Card(add.CardId);
        if (template == null) return;
        for (int i = 0; i < add.CountAt(Ascension); i++)
        {
            CardDef card = template.Instantiate();
            switch (add.Pile)
            {
                case AddPile.Discard: DiscardPile.Add(card); break;
                case AddPile.Draw: DrawPile.Insert(Rng.Next(DrawPile.Count + 1), card); break;
                case AddPile.Hand: if (Hand.Count < MaxHandSize) Hand.Add(card); else DiscardPile.Add(card); break;
            }
        }
    }

    private void RunEnemyTurn()
    {
        // Slow counts cards played since the enemy side's turn last began.
        foreach (Enemy e in Enemies) e.SlowCards = 0;

        foreach (Enemy e in Enemies.ToList())
        {
            if (!e.Alive && e.Reviving && Result == CombatResult.Ongoing) MonsterBehaviors.For(e.Def.Id)?.OnDeadTurn(this, e);
            if (!e.Alive || Result != CombatResult.Ongoing) continue;

            e.Block = 0;
            if (e.Powers[(int)PowerKind.Poison] > 0)
            {
                e.Hp -= e.Powers[(int)PowerKind.Poison];
                e.Powers[(int)PowerKind.Poison]--;
                if (!e.Alive) { OnEnemyDeath(e); continue; }
            }
            if (e.Powers[(int)PowerKind.Plating] > 0) e.Powers[(int)PowerKind.Plating]--;

            if (!e.Stunned && e.Move != null) ExecuteMove(e, e.Move);

            if (e.RitualSkip) e.RitualSkip = false;
            else e.Powers[(int)PowerKind.Strength] += e.Powers[(int)PowerKind.Ritual];
            e.Powers[(int)PowerKind.Strength] += e.Powers[(int)PowerKind.Territorial];
            e.Block += e.Powers[(int)PowerKind.Plating] + e.Powers[(int)PowerKind.Metallicize];
            e.Advance();
        }

        // End of the enemy side's turn: debuffs count down on everyone, and turn-long effects wear off.
        foreach (PowerKind kind in Enum.GetValues<PowerKind>())
        {
            if (!PowerRules.TicksDownAfterEnemyTurn(kind)) continue;
            if (PlayerPowers[(int)kind] > 0) PlayerPowers[(int)kind]--;
            foreach (Enemy e in Enemies)
                if (e.Powers[(int)kind] > 0) e.Powers[(int)kind]--;
        }
        PlayerPowers[(int)PowerKind.FlameBarrier] = 0;
        if (PlayerPowers[(int)PowerKind.Colossus] > 0) PlayerPowers[(int)PowerKind.Colossus]--;
        foreach (Enemy e in Enemies)
        {
            e.Powers[(int)PowerKind.Strength] += e.Powers[(int)PowerKind.TempStrengthDown];
            e.Powers[(int)PowerKind.TempStrengthDown] = 0;
        }

        // Sleepers count down; on their last sleeping turn they lose their plating, then wake.
        foreach (Enemy e in Enemies)
        {
            if (!e.Alive || e.Powers[(int)PowerKind.Asleep] <= 0) continue;
            if (e.Powers[(int)PowerKind.Asleep] <= 1) e.Powers[(int)PowerKind.Plating] = 0;
            e.Powers[(int)PowerKind.Asleep]--;
        }

        // Now that the round's effects are settled, every enemy picks its next move.
        foreach (Enemy e in Enemies.ToList())
            if (e.Alive) e.PlanNext(this);
    }

    private void ExecuteMove(Enemy e, MoveDef move)
    {
        // Waterfall Giant: build-up moves with a few bespoke effects (see WaterfallGiant.cs in the decompiled game).
        if (e.Def.Id == "WATERFALL_GIANT")
        {
            if (move.Id == "SIPHON") e.Hp = Math.Min(e.MaxHp, e.Hp + (ToughEnemies ? 15 : 10));
            if (move.Id == "ABOUT_TO_BLOW")
            {
                e.ExplodeDamage = e.Powers[(int)PowerKind.SteamEruption];
                e.Powers[(int)PowerKind.SteamEruption] = 0;
                return;
            }
        }

        int hits = move.Id == "EXPLODE" ? (e.ExplodeDamage > 0 ? 1 : 0) : (move.IsAttack ? move.HitsAt(Ascension) : 0);
        int baseDamage = MoveBaseDamage(e);
        for (int h = 0; h < hits && Result == CombatResult.Ongoing && e.Alive; h++)
            HitPlayer(EnemyAttackDamage(baseDamage, e), e);
        if (hits > 0) e.Powers[(int)PowerKind.Vigor] = 0;   // Vigor is used up by the attack it boosts
        if (move.Id == "PRESSURE_GUN") e.GunBonus += 5;
        if (!e.Alive && !e.Dying) return;

        e.Block += move.BlockAt(Ascension);
        foreach (MovePower p in move.Powers)
        {
            int amount = p.AmountAt(Ascension);
            if (p.OnPlayer) AddPower(PlayerPowers, p.Power, amount, debuff: true);
            else
            {
                AddPower(e.Powers, p.Power, amount, debuff: false);
                if (p.Power == PowerKind.Ritual) e.RitualSkip = true;
            }
        }
        foreach (CardAdd add in move.Adds) AddCards(add);
        MonsterBehaviors.For(e.Def.Id)?.OnMove(this, e, move);

        if (move.Id == "EXPLODE")
        {
            // The explosion is the Waterfall Giant's last act.
            e.Dying = false;
            e.Hp = 0;
        }
    }

    private static void AddPower(int[] powers, PowerKind kind, int amount, bool debuff)
    {
        if (kind == PowerKind.Unsupported) return;
        // Artifact absorbs one debuff application per stack.
        if (debuff && PowerRules.IsDebuff(kind) && powers[(int)PowerKind.Artifact] > 0)
        {
            powers[(int)PowerKind.Artifact]--;
            return;
        }
        powers[(int)kind] += amount;
    }

    private void CheckEnd()
    {
        if (Result != CombatResult.Ongoing) return;
        if (Hp <= 0) Result = CombatResult.Lost;
        else if (!Enemies.Any(e => e.Alive && e.Primary))
        {
            Result = CombatResult.Won;
            RelicAfterVictory();
        }
    }
}
