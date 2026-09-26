using System.Text.RegularExpressions;

namespace SpireMonteCarlo.Sim;

/// <summary>A live enemy: its stats plus its position in the move state machine.</summary>
public sealed class Enemy
{
    private static readonly Regex SlotCondition = new(@"SlotName\s*==\s*""(?<slot>\w+)""", RegexOptions.Compiled);
    private static readonly Regex HasPowerCondition = new(@"HasPower<(?<power>\w+?)(Power)?>", RegexOptions.Compiled);

    public required MonsterDef Def { get; init; }
    public int Index { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; init; }
    public int Block { get; set; }
    public int[] Powers { get; } = new int[PowerRules.Count];

    /// <summary>For monsters whose first move depends on a starter index (assigned per encounter).</summary>
    public int StarterIndex { get; set; }

    /// <summary>Start in the monster's alternative first state (an encounter flag such as StartsWithDance).</summary>
    public bool AltStart { get; set; }

    /// <summary>Ritual was just applied, so its first end-of-turn Strength gain is skipped (as in the game's RitualPower).</summary>
    public bool RitualSkip { get; set; }

    /// <summary>Cards the player has played since this enemy's last turn (drives Slow).</summary>
    public int SlowCards { get; set; }

    /// <summary>Skittish already gave block this player turn.</summary>
    public bool SkittishUsed { get; set; }

    /// <summary>HP lost so far this player turn (drives Hardened Shell's cap).</summary>
    public int ShellDamage { get; set; }

    /// <summary>Killed while holding Steam Eruption: it can't be hurt, and explodes for <see cref="ExplodeDamage"/> next.</summary>
    public bool Dying { get; set; }

    public int ExplodeDamage { get; set; }

    /// <summary>Extra damage the Waterfall Giant's Pressure Gun has built up.</summary>
    public int GunBonus { get; set; }

    /// <summary>The slot the game placed this monster in (e.g. "wriggler2"); a few monsters choose their first move by it.</summary>
    public string? SlotName { get; set; }

    public bool Alive => Hp > 0;

    /// <summary>Minions don't count towards ending the fight.</summary>
    public bool Primary => Powers[(int)PowerKind.Minion] == 0;

    /// <summary>The move it will make on its next turn (its visible intent). Null if stunned or the data had none.</summary>
    public MoveDef? Move { get; private set; }

    public bool Stunned { get; private set; }

    public string? LastMoveId { get; private set; }
    private string _stateId = "";
    private StateDef? _currentMoveState;
    private HashSet<string>? _usedOnce;

    public void Start(Combat combat)
    {
        _stateId = AltStart && Def.AltInitialState != null ? Def.AltInitialState
            : Def.StarterSwitch.Length > 0 ? Def.StarterSwitch[StarterIndex % Def.StarterSwitch.Length] : Def.InitialState;
        PlanNext(combat);
    }

    /// <summary>Call right after the enemy has acted: remember what it did and where the state machine goes next.</summary>
    public void Advance()
    {
        LastMoveId = Move?.Id;
        if (_currentMoveState != null) _stateId = _currentMoveState.Next ?? _currentMoveState.Id;
        _currentMoveState = null;
        Move = null;
        Stunned = false;
    }

    /// <summary>
    /// The enemy loses its next move (a stun). After it, the state machine continues from <paramref name="nextStateId"/>.
    /// </summary>
    public void Stun(string nextStateId)
    {
        Move = null;
        _currentMoveState = null;
        _stateId = nextStateId;
        Stunned = true;
    }

    /// <summary>Forces the next move to a specific state right now (used when the game itself sets the move immediately).</summary>
    public void SetMoveNow(string stateId, Combat combat)
    {
        _stateId = stateId;
        Stunned = false;
        PlanNext(combat);
    }

    /// <summary>Picks the next move from the current state. Called after the end-of-round effects so conditions see the new state.</summary>
    public void PlanNext(Combat combat)
    {
        if (Stunned) return;   // the stun is this round's "move"
        string id = _stateId;
        Move = null;
        _currentMoveState = null;
        for (int guard = 0; guard < 8; guard++)
        {
            if (!Def.States.TryGetValue(id, out StateDef? state)) return;
            switch (state.Kind)
            {
                case StateKind.Move:
                    _currentMoveState = state;
                    Move = state.MoveId != null && Def.Moves.TryGetValue(state.MoveId, out MoveDef? m) ? m : null;
                    return;
                case StateKind.Random:
                    id = ChooseRandom(state, combat);
                    break;
                case StateKind.Conditional:
                    id = ChooseConditional(state, combat);
                    break;
            }
        }
    }

    private bool IsLastMove(string stateId) => Def.States.TryGetValue(stateId, out StateDef? s) && s.MoveId != null && s.MoveId == LastMoveId;

    private string ChooseRandom(StateDef state, Combat combat)
    {
        List<BranchDef> options = state.Branches.ToList();
        if (options.Count == 0)
            // No branch data: uniform over all moves, not repeating the last one.
            options = Def.States.Values.Where(s => s.Kind == StateKind.Move).Select(s => new BranchDef { StateId = s.Id, Repeat = RepeatRule.CannotRepeat }).ToList();

        var allowed = options.Where(b =>
            !(b.Repeat == RepeatRule.CannotRepeat && IsLastMove(b.StateId)) &&
            !(b.Repeat == RepeatRule.UseOnlyOnce && _usedOnce?.Contains(b.StateId) == true)).ToList();
        if (allowed.Count == 0) allowed = options;
        if (allowed.Count == 0) return "";

        double total = allowed.Sum(b => b.Weight);
        double roll = combat.Rng.NextDouble() * total;
        BranchDef chosen = allowed[^1];
        foreach (BranchDef b in allowed)
        {
            roll -= b.Weight;
            if (roll < 0) { chosen = b; break; }
        }
        if (chosen.Repeat == RepeatRule.UseOnlyOnce) (_usedOnce ??= new()).Add(chosen.StateId);
        return chosen.StateId;
    }

    private string ChooseConditional(StateDef state, Combat combat)
    {
        foreach (BranchDef branch in state.Branches)
            if (Evaluate(branch.Condition, combat) == true) return branch.StateId;
        return state.Branches.Length > 0 ? state.Branches[0].StateId : "";
    }

    /// <summary>Understands the conditions seen so far: IsAlone, IsFront, and HasPower&lt;X&gt;; anything else is unknown.</summary>
    private bool? Evaluate(string? condition, Combat combat)
    {
        if (condition == null) return null;
        bool? value = null;
        Match power = HasPowerCondition.Match(condition);
        if (power.Success)
        {
            PowerKind kind = PowerRules.Parse(power.Groups["power"].Value);
            if (kind != PowerKind.Unsupported) value = Powers[(int)kind] > 0;
        }
        else if (SlotCondition.Match(condition) is { Success: true } slot) value = SlotName == slot.Groups["slot"].Value;
        else if (condition.Contains("IsAlone")) value = combat.AliveEnemies == 1;
        else if (condition.Contains("IsFront")) value = combat.Enemies.First(e => e.Alive) == this;
        if (value == null) return null;
        return condition.TrimStart().StartsWith('!') ? !value : value;
    }
}
