namespace SpireMonteCarlo.Sim;

/// <summary>A live enemy: its stats plus its position in the move state machine.</summary>
public sealed class Enemy
{
    public required MonsterDef Def { get; init; }
    public int Index { get; init; }
    public int Hp { get; set; }
    public int MaxHp { get; init; }
    public int Block { get; set; }
    public int[] Powers { get; } = new int[PowerRules.Count];

    /// <summary>For monsters whose first move depends on a starter index (assigned per encounter).</summary>
    public int StarterIndex { get; set; }

    /// <summary>The move it will make on its next turn (its visible intent). Null if the data had none.</summary>
    public MoveDef? Move { get; private set; }

    public string? LastMoveId { get; private set; }
    private string _stateId = "";
    private StateDef? _currentMoveState;
    private HashSet<string>? _usedOnce;

    public bool Alive => Hp > 0;

    public void Start(Combat combat)
    {
        _stateId = Def.StarterSwitch.Length > 0 ? Def.StarterSwitch[StarterIndex % Def.StarterSwitch.Length] : Def.InitialState;
        Plan(combat);
    }

    /// <summary>Call after the enemy has acted: advance the state machine and pick the next move.</summary>
    public void AdvanceAndPlan(Combat combat)
    {
        LastMoveId = Move?.Id;
        if (_currentMoveState != null) _stateId = _currentMoveState.Next ?? _currentMoveState.Id;
        Plan(combat);
    }

    private void Plan(Combat combat)
    {
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

    /// <summary>Only understands the position conditions seen so far (IsAlone / IsFront); anything else is unknown.</summary>
    private bool? Evaluate(string? condition, Combat combat)
    {
        if (condition == null) return null;
        bool? value = null;
        if (condition.Contains("IsAlone")) value = combat.AliveEnemies == 1;
        else if (condition.Contains("IsFront")) value = combat.Enemies.First(e => e.Alive) == this;
        if (value == null) return null;
        return condition.TrimStart().StartsWith('!') ? !value : value;
    }
}
