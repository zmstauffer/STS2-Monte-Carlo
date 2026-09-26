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
