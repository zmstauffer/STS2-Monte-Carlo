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

public sealed class BranchDef
{
    public string? MoveId { get; init; }
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
    public (PowerKind Power, int Amount)[] Innate { get; init; } = Array.Empty<(PowerKind, int)>();

    /// <summary>The data mentions powers or mechanics the engine ignores, so this monster is only roughly modelled.</summary>
    public bool Approximate { get; init; }

    /// <summary>Ids of unsupported powers seen on this monster, for reporting.</summary>
    public string[] IgnoredPowers { get; init; } = Array.Empty<string>();
}

/// <summary>Builds <see cref="MonsterDef"/>s from Codex monster data.</summary>
public sealed class MonsterLibrary
{
    private readonly IReadOnlyDictionary<string, CodexMonster> _codex;
    private readonly Dictionary<string, MonsterDef> _cache = new();

    public MonsterLibrary(IReadOnlyDictionary<string, CodexMonster> codexMonsters) => _codex = codexMonsters;

    public bool Contains(string id) => _codex.ContainsKey(id);

    public MonsterDef Get(string id)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(id, out MonsterDef? cached)) return cached;
            if (!_codex.TryGetValue(id, out CodexMonster? m))
                throw new KeyNotFoundException($"Codex has no monster '{id}'.");
            return _cache[id] = Build(m);
        }
    }

    private static MonsterDef Build(CodexMonster m)
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

        var states = new Dictionary<string, StateDef>();
        foreach (CodexState s in m.AttackPattern?.States ?? new())
        {
            states[s.Id] = new StateDef
            {
                Id = s.Id,
                Kind = s.Type switch { "random" => StateKind.Random, "conditional" => StateKind.Conditional, _ => StateKind.Move },
                MoveId = s.MoveId,
                Next = s.Next,
                Branches = (s.Branches ?? new()).Select(b => new BranchDef { MoveId = b.MoveId, Condition = b.Condition }).ToArray(),
            };
        }

        string initial = m.AttackPattern?.InitialMove ?? "";
        if (!states.ContainsKey(initial))
            initial = states.Values.FirstOrDefault(s => s.Kind == StateKind.Move && s.MoveId == initial)?.Id
                      ?? states.Values.FirstOrDefault(s => s.Kind == StateKind.Move)?.Id
                      ?? "";

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
            Innate = innate.ToArray(),
            Approximate = ignored.Count > 0 || states.Count == 0 || m.AttackPattern?.Type == "mixed",
            IgnoredPowers = ignored.Distinct().ToArray(),
        };
    }
}
