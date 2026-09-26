namespace SpireMonteCarlo.Sim;

/// <summary>
/// Monster powers that shape how enemies take hits and what hurting them does (rules read from the decompiled power classes,
/// v0.111): Hard to Kill, Thorns, Curl Up, Personal Hive, Flutter, Slumber, Burrowed, and the Tender and Imbalanced effects that
/// monsters put on the player or on themselves.
/// </summary>
public sealed partial class Combat
{
    private int _tenderCards;

    /// <summary>Ids of cards enemy thieves carried off in this fight; the ones held by a thief that escapes are gone for good.</summary>
    public IEnumerable<string> LostCardIds => Enemies.Where(e => e.Escaped).SelectMany(e => e.StolenCards);

    /// <summary>Enemy-side rules that change or answer a hit, applied before the damage: returns the damage that goes on to the enemy's block and HP.</summary>
    private int BeforeEnemyHit(Enemy e, int damage, bool fromCard)
    {
        // Hard to Kill: no single hit does more than the power's amount.
        int cap = e.Powers[(int)PowerKind.HardToKill];
        if (cap > 0) damage = Math.Min(damage, cap);

        if (fromCard)
        {
            // Thorns: a powered attack hurts the attacker (block still helps).
            int thorns = e.Powers[(int)PowerKind.Thorns];
            if (thorns > 0) HitPlayer(thorns, null);

            // Curl Up: the card that hit it makes it gain block once that card has finished.
            if (e.Powers[(int)PowerKind.CurlUp] > 0) e.CurlUpPending = true;

            // Personal Hive: each hit shuffles Dazed into the draw pile.
            int hive = e.Powers[(int)PowerKind.PersonalHive];
            if (hive > 0) AddCards(new CardAdd("DAZED", AddPile.Draw, hive));
        }
        return damage;
    }

    /// <summary>After an enemy that survived lost HP: Flutter wears down, Slumber wakes.</summary>
    private void AfterEnemyLostHp(Enemy e, bool fromCard)
    {
        if (fromCard && e.Powers[(int)PowerKind.Flutter] > 0)
        {
            e.Powers[(int)PowerKind.Flutter]--;
            if (e.Powers[(int)PowerKind.Flutter] <= 0) e.Stun();
        }
        if (e.Powers[(int)PowerKind.Slumber] > 0)
        {
            e.Powers[(int)PowerKind.Slumber]--;
            if (e.Powers[(int)PowerKind.Slumber] <= 0) WakeSlumberer(e);
        }
    }

    /// <summary>The Slumbering Beetle wakes: it loses its plating and its next move, then starts rolling.</summary>
    private void WakeSlumberer(Enemy e)
    {
        e.Powers[(int)PowerKind.Plating] = 0;
        e.Stun(FirstExisting(e, "ROLL_OUT_MOVE"));
    }

    /// <summary>A burrowed Tunneler whose block is broken is stunned, surfaces, and loses what is left of its block.</summary>
    private void OnEnemyBlockBroken(Enemy e)
    {
        if (e.Powers[(int)PowerKind.Burrowed] <= 0) return;
        e.Powers[(int)PowerKind.Burrowed] = 0;
        e.Block = 0;
        e.Stun(FirstExisting(e, "BITE_MOVE"));
    }

    /// <summary>Curl Up gives its block after the card that triggered it is done.</summary>
    private void ResolveCurlUps()
    {
        foreach (Enemy e in Enemies)
        {
            if (!e.CurlUpPending) continue;
            e.CurlUpPending = false;
            if (e.Alive && e.Powers[(int)PowerKind.CurlUp] > 0)
            {
                e.Block += e.Powers[(int)PowerKind.CurlUp];
                e.Powers[(int)PowerKind.CurlUp] = 0;
            }
        }
    }

    /// <summary>Tender (put on the player by the Hunter Killer): every card played costs a point of Strength and Dexterity until the turn ends.</summary>
    private void AfterCardTender()
    {
        if (PlayerPowers[(int)PowerKind.Tender] <= 0) return;
        PlayerPowers[(int)PowerKind.Strength]--;
        PlayerPowers[(int)PowerKind.Dexterity]--;
        _tenderCards++;
    }

    private void RefundTender()
    {
        if (_tenderCards == 0) return;
        PlayerPowers[(int)PowerKind.Strength] += _tenderCards;
        PlayerPowers[(int)PowerKind.Dexterity] += _tenderCards;
        _tenderCards = 0;
    }

    /// <summary>Infested Prism's Vital Spark taints the player's skills: each one played makes attacks hit harder until the enemy turn ends.</summary>
    private void AfterCardTainted(CardDef card)
    {
        if (card.Kind != CardKind.Skill) return;
        int spark = 0;
        foreach (Enemy e in Enemies) if (e.Alive) spark = Math.Max(spark, e.Powers[(int)PowerKind.VitalSpark]);
        if (spark > 0) PlayerPowers[(int)PowerKind.Tainted] += spark;
    }

    /// <summary>The Insatiable's sandpit counts down as each enemy turn starts; at 0 the player is swallowed.</summary>
    private void SandpitCountdown()
    {
        if (PlayerPowers[(int)PowerKind.Sandpit] <= 0) return;
        PlayerPowers[(int)PowerKind.Sandpit]--;
        if (PlayerPowers[(int)PowerKind.Sandpit] <= 0)
        {
            Hp = 0;
            Result = CombatResult.Lost;
        }
    }

    /// <summary>Adds a monster to the fight (a summon or a split); it may arrive stunned, as the Wrigglers from an infested monster do.</summary>
    public Enemy? Spawn(string monsterId, bool arrivesStunned = false, string? slot = null)
    {
        MonsterDef? def = _services?.Monster(monsterId);
        if (def == null) return null;
        Enemy spawned = CreateEnemy(def);
        spawned.AltStart = arrivesStunned;
        spawned.SlotName = slot;
        Enemies.Add(spawned);
        spawned.Start(this);
        MonsterBehaviors.For(def.Id)?.OnStart(this, spawned);
        return spawned;
    }

    /// <summary>Ravenous (Corpse Slug): when another monster on its side dies it loses its next move and gains Strength.</summary>
    private void OnAllyDied(Enemy dead)
    {
        foreach (Enemy ally in Enemies)
        {
            if (ally == dead || !ally.Alive || ally.Powers[(int)PowerKind.Ravenous] <= 0) continue;
            ally.Stun();
            ally.Powers[(int)PowerKind.Strength] += ally.Powers[(int)PowerKind.Ravenous];
        }
        // Constrict lasts only while the monster that applied it lives (the Slithering Strangler is the only one that does).
        if (dead.Def.Id == "SLITHERING_STRANGLER") PlayerPowers[(int)PowerKind.Constrict] = 0;
    }

    /// <summary>Suck (Fossil Stalker): every attack hit that gets through block feeds it Strength.</summary>
    private void AfterEnemyAttackHits(Enemy e, int hitsThroughBlock)
    {
        int suck = e.Powers[(int)PowerKind.Suck];
        if (suck > 0 && hitsThroughBlock > 0) e.Powers[(int)PowerKind.Strength] += suck * hitsThroughBlock;
    }

    /// <summary>Constrict hurts at the end of each of the player's turns; Tangled and its extra attack cost last only through this turn.</summary>
    private void EndOfTurnMonsterPowers()
    {
        int constrict = PlayerPowers[(int)PowerKind.Constrict];
        if (constrict > 0) HitPlayer(constrict, null);
        PlayerPowers[(int)PowerKind.Tangled] = 0;
    }

    /// <summary>An attack that did no HP damage because the player's block soaked all of it: what Imbalanced watches for.</summary>
    private void AfterEnemyAttackFullyBlocked(Enemy e)
    {
        if (e.Powers[(int)PowerKind.Imbalanced] > 0) e.State["offBalance"] = 1;
    }
}
