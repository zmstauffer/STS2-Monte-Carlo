namespace SpireMonteCarlo.Sim;

public enum CombatResult { Ongoing, Won, Lost }

/// <summary>
/// One combat between the player and a group of enemies. Rules follow the game's own code (checked against the
/// decompiled v0.111 power classes): Vulnerable +50% damage taken, Weak -25% damage dealt, Frail -25% block
/// gained, and those three tick down when the enemy side finishes its turn.
/// A combat owns all its state, so many can run in parallel.
/// </summary>
public sealed class Combat
{
    public const int HandSize = 5;
    public const int MaxHandSize = 10;
    public const int TurnLimit = 80;

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

    public int AliveEnemies => Enemies.Count(e => e.Alive);

    /// <summary>
    /// Global multiplier on damage enemies deal. 1.0 is the game as written; below 1.0 stands in for player advantages the
    /// simulator does not model (potions, relics, better play), tuned so whole-act results match real players.
    /// </summary>
    public double EnemyDamageScale { get; }

    public Combat(IEnumerable<CardDef> deck, int hp, int maxHp, IEnumerable<MonsterDef> monsters, int ascension, ulong seed, int maxEnergy = 3, IReadOnlyList<int>? altStarts = null, double enemyDamageScale = 1.0)
    {
        Rng = new SimRng(seed);
        Ascension = ascension;
        EnemyDamageScale = enemyDamageScale;
        Hp = hp;
        MaxHp = maxHp;
        MaxEnergy = maxEnergy;

        var cards = deck.ToList();
        Rng.Shuffle(cards);
        // Innate cards start on top of the draw pile.
        DrawPile.AddRange(cards.Where(c => !c.Innate));
        DrawPile.AddRange(cards.Where(c => c.Innate));

        foreach (MonsterDef def in monsters)
        {
            (int lo, int hi) = ToughEnemies ? (def.HpMinTough, def.HpMaxTough) : (def.HpMin, def.HpMax);
            int enemyHp = Rng.NextInclusive(lo, Math.Max(lo, hi));
            var enemy = new Enemy { Def = def, Index = Enemies.Count, Hp = enemyHp, MaxHp = enemyHp };
            foreach ((PowerKind power, int amount) in def.Innate)
            {
                enemy.Powers[(int)power] += amount;
                if (power == PowerKind.Ritual) enemy.RitualSkip = true;
            }
            Enemies.Add(enemy);
        }
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

    // ---- queries the bot uses -------------------------------------------------------------------------------

    public bool CanPlay(CardDef card)
    {
        if (Result != CombatResult.Ongoing || card.Cost == CardDef.Unplayable) return false;
        return card.Cost == CardDef.XCost || card.Cost <= Energy;
    }

    /// <summary>Damage one attack of <paramref name="baseDamage"/> would deal to <paramref name="target"/> right now.</summary>
    public int PlayerAttackDamage(int baseDamage, Enemy target)
    {
        double d = Math.Max(0, baseDamage + PlayerPowers[(int)PowerKind.Strength]);
        if (PlayerPowers[(int)PowerKind.Weak] > 0) d *= 0.75;
        if (target.Powers[(int)PowerKind.Vulnerable] > 0) d *= 1.5;
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
        double d = Math.Max(0, baseDamage + attacker.Powers[(int)PowerKind.Strength]);
        if (attacker.Powers[(int)PowerKind.Weak] > 0) d *= 0.75;
        if (PlayerPowers[(int)PowerKind.Vulnerable] > 0) d *= 1.5;
        return (int)Math.Floor(d * EnemyDamageScale);
    }

    /// <summary>Total damage the enemies' visible intents will do to the player if nothing is blocked.</summary>
    public int IncomingDamage()
    {
        int total = 0;
        foreach (Enemy e in Enemies)
        {
            if (!e.Alive || e.Move == null || !e.Move.IsAttack) continue;
            total += e.Move.Hits * EnemyAttackDamage(e.Move.DamagePerHit(DeadlyEnemies), e);
        }
        return total;
    }

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

        if (card.Kind == CardKind.Power) { /* stays in play for the rest of the combat */ }
        else if (card.Exhaust) ExhaustPile.Add(card);
        else DiscardPile.Add(card);

        CheckEnd();
    }

    public void EndPlayerTurn()
    {
        if (Result != CombatResult.Ongoing) return;

        Block += PlayerPowers[(int)PowerKind.Plating] + PlayerPowers[(int)PowerKind.Metallicize];
        foreach (CardDef card in Hand)
        {
            if (card.Ethereal) ExhaustPile.Add(card);
            else if (!card.Retain) DiscardPile.Add(card);
        }
        Hand.RemoveAll(c => c.Ethereal || !c.Retain);

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
        if (PlayerPowers[(int)PowerKind.Barricade] == 0) Block = 0;
        if (PlayerPowers[(int)PowerKind.Plating] > 0) PlayerPowers[(int)PowerKind.Plating]--;
        Energy = MaxEnergy;
        DrawCards(HandSize);
    }

    private int ResolveTarget(int targetIndex)
    {
        if (targetIndex >= 0 && targetIndex < Enemies.Count && Enemies[targetIndex].Alive) return targetIndex;
        return Enemies.FindIndex(e => e.Alive);
    }

    private void Apply(Effect effect, int target, int x)
    {
        switch (effect.Op)
        {
            case EffectOp.Damage:
                for (int h = effect.Hits == -1 ? x : effect.Hits; h > 0; h--)
                {
                    int t = ResolveTarget(target);
                    if (t < 0) break;
                    HitEnemy(Enemies[t], PlayerAttackDamage(effect.Amount, Enemies[t]));
                }
                break;
            case EffectOp.DamageAll:
                for (int h = effect.Hits == -1 ? x : effect.Hits; h > 0; h--)
                    foreach (Enemy e in Enemies.ToList())
                        if (e.Alive) HitEnemy(e, PlayerAttackDamage(effect.Amount, e));
                break;
            case EffectOp.DamageRandom:
                for (int h = effect.Hits == -1 ? x : effect.Hits; h > 0; h--)
                {
                    var alive = Enemies.Where(e => e.Alive).ToList();
                    if (alive.Count == 0) break;
                    Enemy e = alive[Rng.Next(alive.Count)];
                    HitEnemy(e, PlayerAttackDamage(effect.Amount, e));
                }
                break;
            case EffectOp.DamageEqualBlock:
                {
                    int t = ResolveTarget(target);
                    if (t >= 0) HitEnemy(Enemies[t], PlayerAttackDamage(Block, Enemies[t]));
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
                    if (e.Alive) AddPower(e.Powers, effect.Power, effect.Amount, debuff: true);
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

    private void HitEnemy(Enemy enemy, int damage)
    {
        int absorbed = Math.Min(enemy.Block, damage);
        enemy.Block -= absorbed;
        enemy.Hp -= damage - absorbed;
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

    private void RunEnemyTurn()
    {
        foreach (Enemy e in Enemies)
        {
            if (!e.Alive || Result != CombatResult.Ongoing) continue;

            e.Block = 0;
            if (e.Powers[(int)PowerKind.Poison] > 0)
            {
                e.Hp -= e.Powers[(int)PowerKind.Poison];
                e.Powers[(int)PowerKind.Poison]--;
                if (!e.Alive) continue;
            }
            if (e.Powers[(int)PowerKind.Plating] > 0) e.Powers[(int)PowerKind.Plating]--;

            MoveDef? move = e.Move;
            if (move != null)
            {
                if (move.IsAttack)
                    for (int h = 0; h < move.Hits && Result == CombatResult.Ongoing; h++)
                        HitPlayer(EnemyAttackDamage(move.DamagePerHit(DeadlyEnemies), e));
                if (move.Block > 0) e.Block += move.Block;
                foreach (MovePower p in move.Powers)
                {
                    if (p.OnPlayer) AddPower(PlayerPowers, p.Power, p.Amount, debuff: true);
                    else
                    {
                        AddPower(e.Powers, p.Power, p.Amount, debuff: false);
                        if (p.Power == PowerKind.Ritual) e.RitualSkip = true;
                    }
                }
            }

            if (e.RitualSkip) e.RitualSkip = false;
            else e.Powers[(int)PowerKind.Strength] += e.Powers[(int)PowerKind.Ritual];
            e.Block += e.Powers[(int)PowerKind.Plating] + e.Powers[(int)PowerKind.Metallicize];
            e.AdvanceAndPlan(this);
        }

        // End of the enemy side's turn: debuffs count down on everyone.
        foreach (PowerKind kind in Enum.GetValues<PowerKind>())
        {
            if (!PowerRules.TicksDownAfterEnemyTurn(kind)) continue;
            if (PlayerPowers[(int)kind] > 0) PlayerPowers[(int)kind]--;
            foreach (Enemy e in Enemies)
                if (e.Powers[(int)kind] > 0) e.Powers[(int)kind]--;
        }
    }

    private void CheckEnd()
    {
        if (Result != CombatResult.Ongoing) return;
        if (Hp <= 0) Result = CombatResult.Lost;
        else if (Enemies.All(e => !e.Alive)) Result = CombatResult.Won;
    }
}
