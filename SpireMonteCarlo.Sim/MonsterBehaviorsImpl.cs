namespace SpireMonteCarlo.Sim;

/// <summary>
/// The Decimillipede's three segments (rules from DecimillipedeSegment and ReattachPower in the decompiled game): a segment that is
/// killed while another one is still up doesn't die. It goes down (untargetable), does nothing on the next enemy phase, and on the
/// one after that reattaches with 25 HP and picks a fresh move. The fight ends when the last standing segment is killed while the
/// others are down.
/// </summary>
public sealed class DecimillipedeBehavior : MonsterBehavior
{
    private const int ReattachHeal = 25;
    private const string DeadTurns = "deadTurns";

    public override IReadOnlyCollection<string> Handled => new[] { "SetMaxAndCurrentHp", "Reattach", "DEAD_MOVE" };

    public override void OnStart(Combat combat, Enemy self) => self.Powers[(int)PowerKind.Reattach] = ReattachHeal;

    private static bool AnotherSegmentIsUp(Combat combat, Enemy self) =>
        combat.Enemies.Any(e => e != self && e.Powers[(int)PowerKind.Reattach] > 0 && e.Alive);

    public override bool OnDeath(Combat combat, Enemy self)
    {
        if (!AnotherSegmentIsUp(combat, self)) return false;
        self.Hp = 0;
        self.Block = 0;
        self.Reviving = true;
        self.State[DeadTurns] = 0;
        return true;
    }

    public override void OnDeadTurn(Combat combat, Enemy self)
    {
        int turns = self.State.GetValueOrDefault(DeadTurns) + 1;
        self.State[DeadTurns] = turns;
        if (turns < 2) return;   // the first phase is the segment's "dead" move
        self.Reviving = false;
        if (!AnotherSegmentIsUp(combat, self)) return;
        self.Hp = Math.Min(self.MaxHp, ReattachHeal);
        self.SetMoveNow(self.Def.States.ContainsKey("RAND") ? "RAND" : self.Def.InitialState, combat);
    }
}

/// <summary>
/// The Bowlbug Rock (BowlbugRock and ImbalancedPower): when the player's block soaks all of its headbutt it is knocked off balance
/// and loses its next turn (its state machine branches on IsOffBalance); the dizzy turn puts it right.
/// </summary>
public sealed class BowlbugRockBehavior : MonsterBehavior
{
    private const string OffBalance = "offBalance";

    public override IReadOnlyCollection<string> Handled => new[] { "IsOffBalance" };

    public override bool? EvaluateCondition(Combat combat, Enemy self, string condition)
    {
        bool off = self.State.GetValueOrDefault(OffBalance) > 0;
        string text = condition.Trim();
        if (text == "IsOffBalance") return off;
        if (text == "!IsOffBalance") return !off;
        return null;
    }

    public override void OnMove(Combat combat, Enemy self, MoveDef move)
    {
        if (move.Id == "DIZZY") self.State[OffBalance] = 0;
    }
}

/// <summary>
/// The Thieving Hopper (ThievingHopper and SwipePower): Thievery carries a card off from the draw or discard pile (it comes back if the
/// hopper is killed and is lost for good if it escapes), and its last move is to flee, which ends its part in the fight.
/// </summary>
public sealed class ThievingHopperBehavior : MonsterBehavior
{
    public override IReadOnlyCollection<string> Handled => new[] { "THIEVERY", "ESCAPE" };

    public override void OnMove(Combat combat, Enemy self, MoveDef move)
    {
        if (move.Id == "THIEVERY")
        {
            var pool = combat.DrawPile.Concat(combat.DiscardPile).Where(c => c.Kind is not (CardKind.Status or CardKind.Curse)).ToList();
            // It likes uncommon and other good cards; the basic ones are what it takes last.
            var better = pool.Where(c => !c.Id.StartsWith("STRIKE_") && !c.Id.StartsWith("DEFEND_")).ToList();
            List<CardDef> from = better.Count > 0 ? better : pool;
            if (from.Count == 0) return;
            CardDef stolen = from[combat.Rng.Next(from.Count)];
            if (!combat.DrawPile.Remove(stolen)) combat.DiscardPile.Remove(stolen);
            self.StolenCards.Add(stolen.Id);
        }
        else if (move.Id == "ESCAPE")
        {
            self.Escaped = true;
            self.Hp = 0;
        }
    }
}

/// <summary>The Entomancer's Pheromone Spit (Entomancer class): with fewer than 3 Personal Hive it adds one Hive and 1 Strength, otherwise it just gains 2 Strength.</summary>
public sealed class EntomancerBehavior : MonsterBehavior
{
    public override IReadOnlyCollection<string> Handled => new[] { "PHEROMONE_SPIT" };

    public override void OnMove(Combat combat, Enemy self, MoveDef move)
    {
        if (move.Id != "PHEROMONE_SPIT") return;
        if (self.Powers[(int)PowerKind.PersonalHive] >= 3)
        {
            self.Powers[(int)PowerKind.Strength] += 2;
            return;
        }
        self.Powers[(int)PowerKind.PersonalHive] += 1;
        self.Powers[(int)PowerKind.Strength] += 1;
    }
}

/// <summary>
/// The Knowledge Demon (KnowledgeDemon class): three times in the fight it curses the player, who picks one of two curses (the modelled
/// player always takes the Disintegration, which hurts 6, 7, then 8 more at the end of every turn); Ponder heals it for 30.
/// </summary>
public sealed class KnowledgeDemonBehavior : MonsterBehavior
{
    private const string Curses = "curses";
    private static readonly int[] DisintegrationDamage = { 6, 7, 8 };

    public override IReadOnlyCollection<string> Handled => new[] { "CURSE_OF_KNOWLEDGE", "PONDER", "_curseOfKnowledgeCounter" };

    public override bool? EvaluateCondition(Combat combat, Enemy self, string condition)
    {
        int count = self.State.GetValueOrDefault(Curses);
        string text = condition.Replace(" ", "");
        if (text == "_curseOfKnowledgeCounter<3") return count < 3;
        if (text == "_curseOfKnowledgeCounter>=3") return count >= 3;
        return null;
    }

    public override void OnMove(Combat combat, Enemy self, MoveDef move)
    {
        if (move.Id == "CURSE_OF_KNOWLEDGE")
        {
            int count = self.State.GetValueOrDefault(Curses);
            if (count < DisintegrationDamage.Length) combat.PlayerPowers[(int)PowerKind.Disintegration] += DisintegrationDamage[count];
            self.State[Curses] = count + 1;
        }
        else if (move.Id == "PONDER")
        {
            self.Hp = Math.Min(self.MaxHp, self.Hp + 30);
        }
    }
}

/// <summary>The Kaiser Crab's claws (CrabRagePower): when the other claw dies, this one gains 6 Strength and a great deal of block, once.</summary>
public sealed class CrabRageBehavior : MonsterBehavior
{
    public override IReadOnlyCollection<string> Handled => new[] { "CrabRage" };

    public override void OnAllyDeath(Combat combat, Enemy self, Enemy dead)
    {
        if (self.Powers[(int)PowerKind.CrabRage] <= 0) return;
        self.Powers[(int)PowerKind.Strength] += 6;
        self.Block += 99;
        self.Powers[(int)PowerKind.CrabRage] = 0;
    }
}

/// <summary>The Gremlin Merc's Surprise (SurprisePower): when it dies a Fat Gremlin and a Sneaky Gremlin turn up, and the fight goes on.</summary>
public sealed class GremlinMercBehavior : MonsterBehavior
{
    public override IReadOnlyCollection<string> Handled => new[] { "Surprise" };

    public override bool OnDeath(Combat combat, Enemy self)
    {
        if (self.Powers[(int)PowerKind.Surprise] > 0 || self.Def.Innate.Any(p => p.Power == PowerKind.Surprise))
        {
            combat.Spawn("FAT_GREMLIN", slot: "fat");
            combat.Spawn("SNEAKY_GREMLIN", slot: "sneaky");
        }
        return false;
    }
}
