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

    /// <summary>The move it will make on its next turn (its visible intent). Null if the data had none.</summary>
    public MoveDef? Move { get; private set; }

    public string? LastMoveId { get; private set; }
    private string _stateId = "";
    private StateDef? _currentMoveState;

    public bool Alive => Hp > 0;

    public void Start(Combat combat)
    {
        _stateId = Def.InitialState;
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
        for (int guard = 0; guard < 8 && Move == null; guard++)
        {
            if (!Def.States.TryGetValue(id, out StateDef? state)) break;
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
        _currentMoveState = null;
    }

    private string MoveStateFor(string? moveId) =>
        Def.States.Values.FirstOrDefault(s => s.Kind == StateKind.Move && s.MoveId == moveId)?.Id ?? "";

    private string ChooseRandom(StateDef state, Combat combat)
    {
        // Codex gives no weights for random states, so pick uniformly and don't repeat the last move immediately.
        List<string> options = state.Branches.Length > 0
            ? state.Branches.Select(b => MoveStateFor(b.MoveId)).Where(s => s != "").ToList()
            : Def.States.Values.Where(s => s.Kind == StateKind.Move).Select(s => s.Id).ToList();
        if (options.Count > 1 && LastMoveId != null)
        {
            var fresh = options.Where(o => Def.States[o].MoveId != LastMoveId).ToList();
            if (fresh.Count > 0) options = fresh;
        }
        return options.Count == 0 ? "" : options[combat.Rng.Next(options.Count)];
    }

    private string ChooseConditional(StateDef state, Combat combat)
    {
        foreach (BranchDef branch in state.Branches)
        {
            bool? holds = Evaluate(branch.Condition, combat);
            if (holds == true) return MoveStateFor(branch.MoveId);
        }
        return state.Branches.Length > 0 ? MoveStateFor(state.Branches[0].MoveId) : "";
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
