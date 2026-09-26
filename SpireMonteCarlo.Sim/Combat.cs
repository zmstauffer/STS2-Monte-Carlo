namespace SpireMonteCarlo.Sim;

public enum CombatResult { Ongoing, Won, Lost }

/// <summary>Lookups the combat needs to create things mid-fight: status cards handed out by monsters, and monsters they spawn.</summary>
public sealed record CombatServices(Func<string, CardDef?> Card, Func<string, MonsterDef?> Monster);

/// <summary>
/// One combat between the player and a group of enemies. Rules follow the game's own code (checked against the
/// decompiled v0.111 power classes): Vulnerable +50% damage taken, Weak -25% damage dealt, Frail -25% block
/// gained, and those three tick down when the enemy side finishes its turn. Monster mechanics (Slow, Asleep,
/// Skittish, Hardened Shell, Slippery, Intangible, Shriek, Plow, Ringing, Infested, Steam Eruption, ...) follow their
/// power classes too. A combat owns all its state, so many can run in parallel.
/// </summary>
public sealed class Combat
{
    public const int HandSize = 5;
    public const int MaxHandSize = 10;
    public const int TurnLimit = 80;

    private readonly CombatServices? _services;

    public SimRng Rng { get; }
    public int Ascension { get; }
    public bool ToughEnemies => Ascension >= 8;
    public bool DeadlyEnemies => Ascension >= 9;

    public int Hp { get; private set; }
    public int MaxHp { get; }
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
    public int CardsPlayed { get; private set; }
    public int CardsPlayedThisTurn { get; private set; }

    public int AliveEnemies => Enemies.Count(e => e.Alive);

    /// <summary>
    /// Global multiplier on damage enemies deal. 1.0 is the game as written; below 1.0 stands in for player advantages the
    /// simulator does not model (potions, relics, better play), tuned so whole-act results match real players.
    /// </summary>
    public double EnemyDamageScale { get; }

    public Combat(IEnumerable<CardDef> deck, int hp, int maxHp, IEnumerable<MonsterDef> monsters, int ascension, ulong seed,
        int maxEnergy = 3, IReadOnlyList<int>? altStarts = null, double enemyDamageScale = 1.0, CombatServices? services = null)
    {
        Rng = new SimRng(seed);
        Ascension = ascension;
        EnemyDamageScale = enemyDamageScale;
        _services = services;
        Hp = hp;
        MaxHp = maxHp;
        MaxEnergy = maxEnergy;

        var cards = deck.ToList();
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

        StartPlayerTurn();
    }

    private Enemy CreateEnemy(MonsterDef def)
    {
        (int lo, int hi) = ToughEnemies ? (def.HpMinTough, def.HpMaxTough) : (def.HpMin, def.HpMax);
        int enemyHp = Rng.NextInclusive(lo, Math.Max(lo, hi));
        var enemy = new Enemy { Def = def, Index = Enemies.Count, Hp = enemyHp, MaxHp = enemyHp };
        foreach ((PowerKind power, int amount) in def.Innate)
        {
            enemy.Powers[(int)power] += amount + (power == PowerKind.Shriek && ToughEnemies ? 5 : 0);   // Terror Eel: 75 at A8+
            if (power == PowerKind.Ritual) enemy.RitualSkip = true;
        }
        return enemy;
    }

    // ---- queries the bot uses -------------------------------------------------------------------------------

    public bool CanPlay(CardDef card)
    {
        if (Result != CombatResult.Ongoing || card.Cost == CardDef.Unplayable) return false;
        if (PlayerPowers[(int)PowerKind.Ringing] > 0 && CardsPlayedThisTurn >= 1) return false;
        return card.Cost == CardDef.XCost || card.Cost <= Energy;
    }

    /// <summary>Damage one attack of <paramref name="baseDamage"/> would deal to <paramref name="target"/> right now.</summary>
    public int PlayerAttackDamage(int baseDamage, Enemy target)
    {
        double d = Math.Max(0, baseDamage + PlayerPowers[(int)PowerKind.Strength]);
        if (PlayerPowers[(int)PowerKind.Weak] > 0) d *= 0.75;
        if (target.Powers[(int)PowerKind.Vulnerable] > 0) d *= 1.5;
        if (target.Powers[(int)PowerKind.Slow] > 0) d *= 1 + 0.1 * target.SlowCards;
        return (int)Math.Floor(d);
    }

    public int PlayerBlockGain(int baseBlock)
    {
        double b = Math.Max(0, baseBlock + PlayerPowers[(int)PowerKind.Dexterity]);
        if (PlayerPowers[(int)PowerKind.Frail] > 0) b *= 0.75;
        return (int)Math.Floor(b);
    }

    /// <summary>Damage one hit of an enemy attack of <paramref name="baseDamage"/> would deal to the player right now.</summary>
    public int EnemyAttackDamage(int baseDamage, Enemy attacker)
    {
        double d = Math.Max(0, baseDamage + attacker.Powers[(int)PowerKind.Strength] + attacker.Powers[(int)PowerKind.Vigor]);
        if (attacker.Powers[(int)PowerKind.Weak] > 0) d *= 0.75;
        if (PlayerPowers[(int)PowerKind.Vulnerable] > 0) d *= 1.5;
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
        int damage = move.DamagePerHit(DeadlyEnemies);
        return move.Id == "PRESSURE_GUN" ? damage + e.GunBonus : damage;
    }

    /// <summary>Damage the enemy's visible move will do to the player if nothing is blocked.</summary>
    public int IntentDamage(Enemy e) => !IntendsAttack(e) ? 0 : (e.Move!.Id == "EXPLODE" ? 1 : e.Move.Hits) * EnemyAttackDamage(MoveBaseDamage(e), e);

    /// <summary>Total damage the enemies' visible intents will do to the player if nothing is blocked.</summary>
    public int IncomingDamage() => Enemies.Where(e => e.Alive).Sum(IntentDamage);

    // ---- player actions -------------------------------------------------------------------------------------

    /// <summary>Plays the card in <paramref name="handIndex"/>. <paramref name="targetIndex"/> is an index into <see cref="Enemies"/>.</summary>
    public void Play(int handIndex, int targetIndex)
    {
        CardDef card = Hand[handIndex];
        if (!CanPlay(card)) throw new InvalidOperationException($"Cannot play {card} with {Energy} energy.");
        Hand.RemoveAt(handIndex);

        int x = 0;
        if (card.Cost == CardDef.XCost) { x = Energy; Energy = 0; }
        else Energy -= card.Cost;

        int target = ResolveTarget(targetIndex);
        foreach (Effect effect in card.Effects)
        {
            if (Result != CombatResult.Ongoing) break;
            Apply(effect, target, x);
        }
        CardsPlayed++;
        CardsPlayedThisTurn++;
        foreach (Enemy e in Enemies)
            if (e.Powers[(int)PowerKind.Slow] > 0) e.SlowCards++;

        if (card.Kind == CardKind.Power) { /* stays in play for the rest of the combat */ }
        else if (card.Exhaust) ExhaustPile.Add(card);
        else DiscardPile.Add(card);

        CheckEnd();
    }

    public void EndPlayerTurn()
    {
        if (Result != CombatResult.Ongoing) return;

        Block += PlayerPowers[(int)PowerKind.Plating] + PlayerPowers[(int)PowerKind.Metallicize];

        // Status cards still in hand hurt (Burn, Infection, Toxic, ...).
        foreach (CardDef card in Hand.ToList())
        {
            if (card.EndTurnDamage > 0) HitPlayer(card.EndTurnDamage);
            if (card.EndTurnHpLoss > 0) LoseHp(card.EndTurnHpLoss);
        }
        if (Result != CombatResult.Ongoing) return;

        foreach (CardDef card in Hand)
        {
            if (card.Ethereal) ExhaustPile.Add(card);
            else if (!card.Retain) DiscardPile.Add(card);
        }
        Hand.RemoveAll(c => c.Ethereal || !c.Retain);
        PlayerPowers[(int)PowerKind.Ringing] = 0;   // it only limits the one turn it was applied for

        RunEnemyTurn();
        CheckEnd();
        if (Result == CombatResult.Ongoing)
        {
            if (Turn >= TurnLimit) { Result = CombatResult.Lost; return; }
            StartPlayerTurn();
        }
    }

    // ---- internals ------------------------------------------------------------------------------------------

    private void StartPlayerTurn()
    {
        Turn++;
        CardsPlayedThisTurn = 0;
        if (PlayerPowers[(int)PowerKind.Barricade] == 0) Block = 0;
        if (PlayerPowers[(int)PowerKind.Plating] > 0) PlayerPowers[(int)PowerKind.Plating]--;
        foreach (Enemy e in Enemies)
        {
            e.SkittishUsed = false;
            e.ShellDamage = 0;
        }
        Energy = MaxEnergy;
        DrawCards(HandSize);
    }

    private int ResolveTarget(int targetIndex)
    {
        if (targetIndex >= 0 && targetIndex < Enemies.Count && Targetable(Enemies[targetIndex])) return targetIndex;
        return Enemies.FindIndex(Targetable);
    }

    private static bool Targetable(Enemy e) => e.Alive && !e.Dying;

    private void Apply(Effect effect, int target, int x)
    {
        switch (effect.Op)
        {
            case EffectOp.Damage:
                for (int h = effect.Hits == -1 ? x : effect.Hits; h > 0; h--)
                {
                    int t = ResolveTarget(target);
                    if (t < 0) break;
                    DamageEnemy(Enemies[t], PlayerAttackDamage(effect.Amount, Enemies[t]), fromCard: true);
                }
                break;
            case EffectOp.DamageAll:
                for (int h = effect.Hits == -1 ? x : effect.Hits; h > 0; h--)
                    foreach (Enemy e in Enemies.ToList())
                        if (Targetable(e)) DamageEnemy(e, PlayerAttackDamage(effect.Amount, e), fromCard: true);
                break;
            case EffectOp.DamageRandom:
                for (int h = effect.Hits == -1 ? x : effect.Hits; h > 0; h--)
                {
                    var alive = Enemies.Where(Targetable).ToList();
                    if (alive.Count == 0) break;
                    Enemy e = alive[Rng.Next(alive.Count)];
                    DamageEnemy(e, PlayerAttackDamage(effect.Amount, e), fromCard: true);
                }
                break;
            case EffectOp.DamageEqualBlock:
                {
                    int t = ResolveTarget(target);
                    if (t >= 0) DamageEnemy(Enemies[t], PlayerAttackDamage(Block, Enemies[t]), fromCard: true);
                    break;
                }
            case EffectOp.Block: Block += PlayerBlockGain(effect.Amount); break;
            case EffectOp.Draw: DrawCards(effect.Amount); break;
            case EffectOp.Energy: Energy += effect.Amount; break;
            case EffectOp.LoseHp: LoseHp(effect.Amount); break;
            case EffectOp.DebuffEnemy:
                {
                    int t = ResolveTarget(target);
                    if (t >= 0) AddPower(Enemies[t].Powers, effect.Power, effect.Amount, debuff: true);
                    break;
                }
            case EffectOp.DebuffAll:
                foreach (Enemy e in Enemies)
                    if (Targetable(e)) AddPower(e.Powers, effect.Power, effect.Amount, debuff: true);
                break;
            case EffectOp.DebuffSelf: AddPower(PlayerPowers, effect.Power, effect.Amount, debuff: true); break;
            case EffectOp.BuffSelf: AddPower(PlayerPowers, effect.Power, effect.Amount, debuff: false); break;
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

    /// <summary>All damage the player deals to an enemy goes through here, so the enemy's defensive powers apply once.</summary>
    private void DamageEnemy(Enemy enemy, int damage, bool fromCard)
    {
        if (!enemy.Alive || enemy.Dying) return;

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
        if (lost <= 0) return;

        enemy.Hp -= lost;
        if (fromCard && enemy.Powers[(int)PowerKind.Skittish] > 0 && !enemy.SkittishUsed)
        {
            enemy.SkittishUsed = true;
            enemy.Block += enemy.Powers[(int)PowerKind.Skittish];
        }

        if (enemy.Hp <= 0) OnEnemyDeath(enemy);
        else OnEnemyDamaged(enemy);
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

    private void HitPlayer(int damage)
    {
        int absorbed = Math.Min(Block, damage);
        Block -= absorbed;
        LoseHp(damage - absorbed);
    }

    private void LoseHp(int amount)
    {
        if (amount <= 0) return;
        Hp -= amount;
        HpLost += amount;
        if (Hp <= 0) Result = CombatResult.Lost;
    }

    private void DrawCards(int count)
    {
        for (int i = 0; i < count && Hand.Count < MaxHandSize; i++)
        {
            if (DrawPile.Count == 0)
            {
                if (DiscardPile.Count == 0) return;
                DrawPile.AddRange(DiscardPile);
                DiscardPile.Clear();
                Rng.Shuffle(DrawPile);
            }
            Hand.Add(DrawPile[^1]);
            DrawPile.RemoveAt(DrawPile.Count - 1);
        }
    }

    private void AddCards(CardAdd add)
    {
        CardDef? card = _services?.Card(add.CardId);
        if (card == null) return;
        for (int i = 0; i < add.CountAt(Ascension); i++)
        {
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

        // End of the enemy side's turn: debuffs count down on everyone.
        foreach (PowerKind kind in Enum.GetValues<PowerKind>())
        {
            if (!PowerRules.TicksDownAfterEnemyTurn(kind)) continue;
            if (PlayerPowers[(int)kind] > 0) PlayerPowers[(int)kind]--;
            foreach (Enemy e in Enemies)
                if (e.Powers[(int)kind] > 0) e.Powers[(int)kind]--;
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

        int hits = move.Id == "EXPLODE" ? (e.ExplodeDamage > 0 ? 1 : 0) : (move.IsAttack ? move.Hits : 0);
        int baseDamage = MoveBaseDamage(e);
        for (int h = 0; h < hits && Result == CombatResult.Ongoing; h++)
            HitPlayer(EnemyAttackDamage(baseDamage, e));
        if (hits > 0) e.Powers[(int)PowerKind.Vigor] = 0;   // Vigor is used up by the attack it boosts
        if (move.Id == "PRESSURE_GUN") e.GunBonus += 5;

        if (move.Block > 0) e.Block += move.Block;
        foreach (MovePower p in move.Powers)
        {
            if (p.OnPlayer) AddPower(PlayerPowers, p.Power, p.Amount, debuff: true);
            else
            {
                AddPower(e.Powers, p.Power, p.Amount + (p.Power == PowerKind.Plow && DeadlyEnemies ? 10 : 0), debuff: false);   // Ceremonial Beast: 160 at A9+
                if (p.Power == PowerKind.Ritual) e.RitualSkip = true;
            }
        }
        foreach (CardAdd add in move.Adds) AddCards(add);

        if (move.Id == "EXPLODE")
        {
            // The explosion is the Waterfall Giant's last act.
            e.Dying = false;
            e.Hp = 0;
        }
    }

    private void CheckEnd()
    {
        if (Result != CombatResult.Ongoing) return;
        if (Hp <= 0) Result = CombatResult.Lost;
        else if (!Enemies.Any(e => e.Alive && e.Primary)) Result = CombatResult.Won;
    }
}
