namespace SpireMonteCarlo.Sim;

/// <summary>
/// Rules that belong to one kind of monster and can't be expressed as data: what its moves do beyond damage, block, and powers
/// (summoning, healing, escaping), branch weights the game computes at runtime, and how it reacts to fight events.
/// Each behavior is written from the monster's decompiled class.
/// </summary>
public abstract class MonsterBehavior
{
    /// <summary>Substrings of the audit's "unmodeled" notes for this monster that this behavior implements.</summary>
    public virtual IReadOnlyCollection<string> Handled => Array.Empty<string>();

    /// <summary>Runs once when the fight starts (the game's AfterAddedToRoom for things that aren't plain powers).</summary>
    public virtual void OnStart(Combat combat, Enemy self) { }

    /// <summary>Evaluates a conditional branch's whole C# condition text (including any negation) when the engine can't; null if unknown.</summary>
    public virtual bool? EvaluateCondition(Combat combat, Enemy self, string condition) => null;

    /// <summary>Weight of a random branch that the game computes at runtime, or null to use the extracted weight.</summary>
    public virtual double? BranchWeight(Combat combat, Enemy self, BranchDef branch) => null;

    /// <summary>After the standard effects of one of this monster's moves.</summary>
    public virtual void OnMove(Combat combat, Enemy self, MoveDef move) { }

    /// <summary>When another creature on this monster's side dies.</summary>
    public virtual void OnAllyDeath(Combat combat, Enemy self, Enemy dead) { }

    /// <summary>When this monster is killed. Return true to stop it from dying (it revives, splits, or waits to explode).</summary>
    public virtual bool OnDeath(Combat combat, Enemy self) => false;

    /// <summary>After the monster loses HP without dying.</summary>
    public virtual void OnDamaged(Combat combat, Enemy self, int hpLost) { }

    /// <summary>Each enemy phase while the monster is down but due to come back (<see cref="Enemy.Reviving"/>).</summary>
    public virtual void OnDeadTurn(Combat combat, Enemy self) { }
}

public static class MonsterBehaviors
{
    private static readonly Dictionary<string, MonsterBehavior> Registry = new();

    static MonsterBehaviors()
    {
        Register("AXEBOT", new AxebotBehavior());
        Register("OWL_MAGISTRATE", new OwlMagistrateBehavior());
        Register("GREMLIN_MERC", new GremlinMercBehavior());
        Register("ENTOMANCER", new EntomancerBehavior());
        Register("KNOWLEDGE_DEMON", new KnowledgeDemonBehavior());
        Register("CRUSHER", new CrabRageBehavior());
        Register("ROCKET", new CrabRageBehavior());
        Register("BOWLBUG_ROCK", new BowlbugRockBehavior());
        Register("THIEVING_HOPPER", new ThievingHopperBehavior());
        Register("TEST_SUBJECT", new TestSubjectBehavior());
        Register("QUEEN", new QueenBehavior());
        Register("AEONGLASS", new AeonglassBehavior());
        Register("MAGI_KNIGHT", new MagiKnightBehavior());
        Register("THE_LOST", new PossessBehavior(PowerKind.Strength, "DEBILITATING_SMOG"));
        Register("THE_FORGOTTEN", new PossessBehavior(PowerKind.Dexterity, "MIASMA"));
        var segment = new DecimillipedeBehavior();
        foreach (string id in new[] { "DECIMILLIPEDE_SEGMENT_FRONT", "DECIMILLIPEDE_SEGMENT_MIDDLE", "DECIMILLIPEDE_SEGMENT_BACK" })
            Register(id, segment);
    }

    public static MonsterBehavior? For(string monsterId) => Registry.GetValueOrDefault(monsterId);

    /// <summary>Whether a behavior registered for the monster implements the thing an audit note describes.</summary>
    public static bool Handles(string monsterId, string note) =>
        Registry.TryGetValue(monsterId, out MonsterBehavior? b) && b.Handled.Any(h => note.Contains(h, StringComparison.OrdinalIgnoreCase));

    internal static void Register(string monsterId, MonsterBehavior behavior) => Registry[monsterId] = behavior;
}
