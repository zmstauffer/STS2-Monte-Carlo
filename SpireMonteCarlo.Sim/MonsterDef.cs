using SpireMonteCarlo.Codex;

namespace SpireMonteCarlo.Sim;

public readonly record struct MovePower(PowerKind Power, bool OnPlayer, int Amount);

public sealed class MoveDef
{
    public required string Id { get; init; }
    public string Intent { get; init; } = "";
    /// <summary>Damage per hit at normal difficulty / at Deadly Enemies (A9+).</summary>
    public int Damage { get; init; }
    public int DamageDeadly { get; init; }
    public int Hits { get; init; } = 1;
    public int Block { get; init; }
    public MovePower[] Powers { get; init; } = Array.Empty<MovePower>();

    public bool IsAttack => Damage > 0 || DamageDeadly > 0;
    public int DamagePerHit(bool deadly) => deadly ? DamageDeadly : Damage;
}

public enum StateKind { Move, Random, Conditional }

public enum RepeatRule { Default, CannotRepeat, CanRepeatForever, UseOnlyOnce }

public sealed class BranchDef
{
    /// <summary>The state this branch leads to.</summary>
    public required string StateId { get; init; }
    public double Weight { get; init; } = 1;
    public RepeatRule Repeat { get; init; }
    public string? Condition { get; init; }
}

public sealed class StateDef
{
    public required string Id { get; init; }
    public StateKind Kind { get; init; }
    public string? MoveId { get; init; }
    public string? Next { get; init; }
    public BranchDef[] Branches { get; init; } = Array.Empty<BranchDef>();
}

public sealed class MonsterDef
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "Normal";
    public int HpMin { get; init; }
    public int HpMax { get; init; }
    /// <summary>Range at Tough Enemies (A8+).</summary>
    public int HpMinTough { get; init; }
    public int HpMaxTough { get; init; }
    public Dictionary<string, MoveDef> Moves { get; init; } = new();
    public Dictionary<string, StateDef> States { get; init; } = new();
    public string InitialState { get; init; } = "";

    /// <summary>State ids by starting index; non-empty when the first move depends on the monster's starter index.</summary>
    public string[] StarterSwitch { get; init; } = Array.Empty<string>();

    public (PowerKind Power, int Amount)[] Innate { get; init; } = Array.Empty<(PowerKind, int)>();

    /// <summary>True when the state machine came from the game's own code (exact links, weights, repeat rules).</summary>
    public bool ExactAi { get; init; }

    /// <summary>The data mentions powers or mechanics the engine ignores, so this monster is only roughly modelled.</summary>
    public bool Approximate { get; init; }

    /// <summary>Ids of unsupported powers seen on this monster, for reporting.</summary>
    public string[] IgnoredPowers { get; init; } = Array.Empty<string>();
}

/// <summary>Builds <see cref="MonsterDef"/>s from Codex monster data, preferring the state machines extracted from the game.</summary>
public sealed class MonsterLibrary
{
    private readonly IReadOnlyDictionary<string, CodexMonster> _codex;
    private readonly IReadOnlyDictionary<string, ExtractedMachine> _machines;
    private readonly Dictionary<string, MonsterDef> _cache = new();

    public MonsterLibrary(IReadOnlyDictionary<string, CodexMonster> codexMonsters, IReadOnlyDictionary<string, ExtractedMachine>? machines = null)
    {
        _codex = codexMonsters;
        _machines = machines ?? new Dictionary<string, ExtractedMachine>();
    }

    public bool Contains(string id) => _codex.ContainsKey(id);

    public MonsterDef Get(string id)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(id, out MonsterDef? cached)) return cached;
            if (!_codex.TryGetValue(id, out CodexMonster? m))
                throw new KeyNotFoundException($"Codex has no monster '{id}'.");
            return _cache[id] = Build(m, _machines.GetValueOrDefault(id));
        }
    }

    private static MonsterDef Build(CodexMonster m, ExtractedMachine? machine)
    {
        var ignored = new List<string>();
        var moves = new Dictionary<string, MoveDef>();
        foreach (CodexMove move in m.Moves)
        {
            var powers = new List<MovePower>();
            foreach (CodexMonsterPower p in move.Powers ?? new())
            {
                PowerKind kind = PowerRules.Parse(p.PowerId);
                if (kind == PowerKind.Unsupported) { ignored.Add(p.PowerId); continue; }
                powers.Add(new MovePower(kind, OnPlayer: string.Equals(p.Target, "player", StringComparison.OrdinalIgnoreCase), p.Amount ?? 0));
            }
            int normal = move.Damage?.Normal ?? 0;
            moves[move.Id] = new MoveDef
            {
                Id = move.Id,
                Intent = move.Intent,
                Damage = normal,
                DamageDeadly = move.Damage?.Ascension ?? normal,
                Hits = Math.Max(1, move.Damage?.HitCount ?? 1),
                Block = move.Block ?? 0,
                Powers = powers.ToArray(),
            };
        }

        Dictionary<string, StateDef> states;
        string initial;
        string[] starterSwitch = Array.Empty<string>();
        bool exact = false;
        bool movesResolved = true;

        if (machine != null)
        {
            exact = true;
            states = machine.States.ToDictionary(s => s.Id, s => new StateDef
            {
                Id = s.Id,
                Kind = s.Kind switch { "random" => StateKind.Random, "conditional" => StateKind.Conditional, _ => StateKind.Move },
                MoveId = s.MoveId,
                Next = s.Next,
                Branches = s.Branches.Select(b => new BranchDef
                {
                    StateId = b.StateId,
                    Weight = b.Weight,
                    Repeat = Enum.TryParse(b.Repeat, out RepeatRule r) ? r : RepeatRule.Default,
                    Condition = b.Condition,
                }).ToArray(),
            });
            movesResolved = states.Values.Where(s => s.Kind == StateKind.Move).All(s => s.MoveId != null && moves.ContainsKey(s.MoveId));
            starterSwitch = machine.StarterSwitch.ToArray();
            initial = machine.Initial ?? starterSwitch.FirstOrDefault() ?? "";
        }
        else
        {
            // Codex fallback. Its links can be missing, so an unlinked state follows the next one in the list.
            var list = m.AttackPattern?.States ?? new();
            states = new Dictionary<string, StateDef>();
            for (int i = 0; i < list.Count; i++)
            {
                CodexState s = list[i];
                string? next = string.IsNullOrEmpty(s.Next) ? list[Math.Min(i + 1, list.Count - 1)].Id : s.Next;
                states[s.Id] = new StateDef
                {
                    Id = s.Id,
                    Kind = s.Type switch { "random" => StateKind.Random, "conditional" => StateKind.Conditional, _ => StateKind.Move },
                    MoveId = s.MoveId,
                    Next = next,
                    Branches = (s.Branches ?? new())
                        .Select(b => new BranchDef { StateId = list.FirstOrDefault(x => x.MoveId == b.MoveId)?.Id ?? "", Condition = b.Condition })
                        .Where(b => b.StateId != "").ToArray(),
                };
            }
            initial = m.AttackPattern?.InitialMove ?? "";
            if (!states.ContainsKey(initial)) initial = states.Values.FirstOrDefault(s => s.Kind == StateKind.Move)?.Id ?? "";
        }

        var innate = new List<(PowerKind, int)>();
        foreach (CodexInnatePower p in m.InnatePowers ?? new())
        {
            PowerKind kind = PowerRules.Parse(p.PowerId);
            if (kind == PowerKind.Unsupported) ignored.Add(p.PowerId);
            else innate.Add((kind, p.Amount));
        }

        int hpMin = m.MinHp ?? 1;
        int hpMax = m.MaxHp ?? hpMin;
        int toughMin = m.MinHpAscension ?? hpMin;
        int toughMax = m.MaxHpAscension ?? (m.MinHpAscension != null ? toughMin : hpMax);
        return new MonsterDef
        {
            Id = m.Id,
            Name = m.Name,
            Type = m.Type,
            HpMin = hpMin,
            HpMax = hpMax,
            HpMinTough = toughMin,
            HpMaxTough = toughMax,
            Moves = moves,
            States = states,
            InitialState = initial,
            StarterSwitch = starterSwitch,
            Innate = innate.ToArray(),
            ExactAi = exact,
            Approximate = ignored.Count > 0 || !exact || !movesResolved,
            IgnoredPowers = ignored.Distinct().ToArray(),
        };
    }
}
