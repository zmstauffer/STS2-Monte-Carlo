using SpireMonteCarlo.Codex;

namespace SpireMonteCarlo.Sim;

/// <summary>A power a move applies. <see cref="Target"/> is empty for the monster itself (or the player when <see cref="OnPlayer"/>), else Allies or Opponents.</summary>
public readonly record struct MovePower(PowerKind Power, bool OnPlayer, int Amount, int AmountAlt = 0, int AmountAscension = 0, string Target = "")
{
    public int AmountAt(int ascension) => AmountAscension > 0 && ascension >= AmountAscension ? AmountAlt : Amount;
}

/// <summary>A power a monster starts the fight with; the amount can change with the ascension level.</summary>
public readonly record struct InnatePower(PowerKind Power, int Amount, int Alt = 0, int Ascension = 0)
{
    public int At(int ascension) => Ascension > 0 && ascension >= Ascension ? Alt : Amount;
}

public enum AddPile { Discard, Draw, Hand }

/// <summary>A move that puts status cards into the player's piles (Wounds, Dazed, Slimed, ...).</summary>
public sealed record CardAdd(string CardId, AddPile Pile, int Count, int CountAlt = 0, int AltAscension = 0)
{
    /// <summary>How many cards at this ascension (the game raises some counts at Deadly/Tough Enemies).</summary>
    public int CountAt(int ascension) => AltAscension > 0 && ascension >= AltAscension ? CountAlt : Count;
}

public sealed record MoveDef
{
    public required string Id { get; init; }
    public string Intent { get; init; } = "";
    /// <summary>Damage per hit at normal difficulty, and <see cref="DamageDeadly"/> from <see cref="DamageAscension"/> (Deadly Enemies, A9, unless the game says otherwise).</summary>
    public int Damage { get; init; }
    public int DamageDeadly { get; init; }
    public int DamageAscension { get; init; } = 9;
    public int Hits { get; init; } = 1;
    public int HitsAlt { get; init; }
    public int HitsAscension { get; init; }
    public int Block { get; init; }
    public int BlockAlt { get; init; }
    public int BlockAscension { get; init; }
    public MovePower[] Powers { get; init; } = Array.Empty<MovePower>();
    public CardAdd[] Adds { get; init; } = Array.Empty<CardAdd>();

    /// <summary>Monsters this move adds to the fight (ids like GAS_BOMB).</summary>
    public string[] Spawns { get; init; } = Array.Empty<string>();

    public bool IsAttack => Damage > 0 || DamageDeadly > 0;
    public int DamageAt(int ascension) => ascension >= DamageAscension ? DamageDeadly : Damage;
    public int HitsAt(int ascension) => HitsAscension > 0 && ascension >= HitsAscension ? HitsAlt : Hits;
    public int BlockAt(int ascension) => BlockAscension > 0 && ascension >= BlockAscension ? BlockAlt : Block;
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

    /// <summary>The game computes this branch's weight at runtime; a monster behavior has to supply it.</summary>
    public bool DynamicWeight { get; init; }
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

    /// <summary>Starting state used instead of <see cref="InitialState"/> for the one monster an encounter flags (e.g. the middle Inklet).</summary>
    public string? AltInitialState { get; init; }

    /// <summary>State ids by starting index; non-empty when the first move depends on the monster's starter index.</summary>
    public string[] StarterSwitch { get; init; } = Array.Empty<string>();

    public InnatePower[] Innate { get; init; } = Array.Empty<InnatePower>();

    /// <summary>Block the monster starts the fight with (Cubex Construct), at normal difficulty and from <see cref="InnateBlockAscension"/>.</summary>
    public int InnateBlock { get; init; }
    public int InnateBlockAlt { get; init; }
    public int InnateBlockAscension { get; init; }

    /// <summary>True when the numbers and effects come from the game's own class rather than Codex.</summary>
    public bool ExactNumbers { get; init; }

    /// <summary>Things this monster does that the extractor saw but the engine has no rule for (audit output).</summary>
    public string[] Unmodeled { get; init; } = Array.Empty<string>();

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
    private readonly IReadOnlyDictionary<string, ExtractedMonster> _classes;
    private readonly Dictionary<string, MonsterDef> _cache = new();

    public MonsterLibrary(IReadOnlyDictionary<string, CodexMonster> codexMonsters, IReadOnlyDictionary<string, ExtractedMachine>? machines = null,
        IReadOnlyDictionary<string, ExtractedMonster>? classes = null)
    {
        _codex = codexMonsters;
        _machines = machines ?? new Dictionary<string, ExtractedMachine>();
        _classes = classes ?? new Dictionary<string, ExtractedMonster>();
    }

    public bool Contains(string id) => _codex.ContainsKey(id) || _classes.ContainsKey(id);

    public MonsterDef Get(string id)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(id, out MonsterDef? cached)) return cached;
            if (!_codex.TryGetValue(id, out CodexMonster? m))
            {
                if (!_classes.ContainsKey(id)) throw new KeyNotFoundException($"Neither Codex nor the game data has monster '{id}'.");
                m = new CodexMonster { Id = id, Name = id, Type = "Normal" };
            }
            return _cache[id] = Build(m, _machines.GetValueOrDefault(id), _classes.GetValueOrDefault(id));
        }
    }

    private static MovePower? ToMovePower(ExtractedPowerApply p, List<string> ignored, List<string> unmodeled)
    {
        PowerKind kind = PowerRules.Parse(p.Power);
        if (kind == PowerKind.Unsupported) { ignored.Add(p.Power); return null; }
        ScaledValue amount = p.Amount ?? new ScaledValue { Base = 1 };
        if (p.Amount == null) unmodeled.Add($"amount of {p.Power} not resolved");
        return p.Target switch
        {
            "Self" => new MovePower(kind, false, amount.Base, amount.Alt, amount.Ascension),
            "Player" => new MovePower(kind, true, amount.Base, amount.Alt, amount.Ascension),
            "Allies" or "Opponents" => new MovePower(kind, false, amount.Base, amount.Alt, amount.Ascension, p.Target),
            _ => Unresolved(p, unmodeled),
        };
    }

    private static MovePower? Unresolved(ExtractedPowerApply p, List<string> unmodeled)
    {
        unmodeled.Add($"{p.Power} applied to {p.Target}");
        return null;
    }

    /// <summary>The move exactly as the game's move method does it.</summary>
    private static MoveDef FromClass(string moveId, ExtractedMove x, string intent, CardAdd[] adds, List<string> ignored, List<string> unmodeled)
    {
        ExtractedAttack? attack = x.Attacks.FirstOrDefault();
        if (x.Attacks.Count > 1) unmodeled.Add($"{moveId}: {x.Attacks.Count} attacks in one move");
        ExtractedBlock? block = x.Blocks.FirstOrDefault(b => b.Target == "Self");
        foreach (ExtractedBlock other in x.Blocks.Where(b => b.Target != "Self")) unmodeled.Add($"{moveId}: block for {other.Target}");
        if (attack != null && attack.Damage == null) unmodeled.Add($"{moveId}: attack damage not resolved");

        var powers = new List<MovePower>();
        foreach (ExtractedPowerApply p in x.Powers)
            if (ToMovePower(p, ignored, unmodeled) is { } mp) powers.Add(mp);
        foreach (string note in x.Other) unmodeled.Add($"{moveId}: {note}");
        foreach (string heal in x.Heals) unmodeled.Add($"{moveId}: heal {heal}");

        ScaledValue? dmg = attack?.Damage, hits = attack?.Hits;
        return new MoveDef
        {
            Id = moveId,
            Intent = intent,
            Damage = dmg?.Base ?? 0,
            DamageDeadly = dmg == null ? 0 : dmg.Ascension > 0 ? dmg.Alt : dmg.Base,
            DamageAscension = dmg is { Ascension: > 0 } ? dmg.Ascension : 9,
            Hits = Math.Max(1, hits?.Base ?? 1),
            HitsAlt = hits?.Alt ?? 0,
            HitsAscension = hits?.Ascension ?? 0,
            Block = block?.Amount?.Base ?? 0,
            BlockAlt = block?.Amount?.Alt ?? 0,
            BlockAscension = block?.Amount?.Ascension ?? 0,
            Powers = powers.ToArray(),
            Adds = adds,
            Spawns = x.Spawns.ToArray(),
        };
    }

    private static MonsterDef Build(CodexMonster m, ExtractedMachine? machine, ExtractedMonster? cls)
    {
        var ignored = new List<string>();
        var unmodeled = new List<string>();
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
                    DynamicWeight = b.DynamicWeight,
                }).ToArray(),
            });
            // Status cards a move hands out (Wounds, Dazed, ...) come from the game's move methods.
            foreach (ExtractedState s in machine.States.Where(x => x.Adds.Count > 0 && x.MoveId != null && moves.ContainsKey(x.MoveId)))
                moves[s.MoveId!] = moves[s.MoveId!] with
                {
                    Adds = s.Adds.Select(a => new CardAdd(a.Card, Enum.TryParse(a.Pile, out AddPile pile) ? pile : AddPile.Discard, a.Count, a.CountAlt, a.AltAscension)).ToArray(),
                };

            // The game's own move methods override Codex's numbers and powers.
            if (cls != null)
            {
                ignored.Clear();
                foreach (ExtractedState s in machine.States.Where(x => x.Kind == "move" && x.MoveId != null && x.Method != null))
                {
                    if (!cls.Moves.TryGetValue(s.Method!, out ExtractedMove? x)) continue;
                    CardAdd[] adds = s.Adds.Select(a => new CardAdd(a.Card, Enum.TryParse(a.Pile, out AddPile pile) ? pile : AddPile.Discard, a.Count, a.CountAlt, a.AltAscension)).ToArray();
                    string intent = moves.TryGetValue(s.MoveId!, out MoveDef? codexMove) ? codexMove.Intent : "";
                    moves[s.MoveId!] = FromClass(s.MoveId!, x, intent, adds, ignored, unmodeled);
                    if (x.HasLogic) unmodeled.Add($"{s.MoveId}: has logic around its effects");
                }
            }
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

        var innate = new List<InnatePower>();
        int innateBlock = 0, innateBlockAlt = 0, innateBlockAscension = 0;
        if (cls != null && machine != null)
        {
            foreach (ExtractedPowerApply p in cls.Innate)
            {
                PowerKind kind = PowerRules.Parse(p.Power);
                if (kind == PowerKind.Unsupported) { ignored.Add(p.Power); continue; }
                if (p.Target != "Self") { unmodeled.Add($"innate {p.Power} applied to {p.Target}"); continue; }
                if (p.Amount == null) { unmodeled.Add($"innate {p.Power} amount not resolved"); continue; }
                innate.Add(new InnatePower(kind, p.Amount.Base, p.Amount.Alt, p.Amount.Ascension));
            }
            if (cls.InnateBlock.FirstOrDefault(b => b.Target == "Self")?.Amount is { } ib)
                (innateBlock, innateBlockAlt, innateBlockAscension) = (ib.Base, ib.Alt, ib.Ascension);
            foreach (string note in cls.InnateOther) unmodeled.Add("innate: " + note);
        }
        else
        {
            foreach (CodexInnatePower p in m.InnatePowers ?? new())
            {
                PowerKind kind = PowerRules.Parse(p.PowerId);
                if (kind == PowerKind.Unsupported) ignored.Add(p.PowerId);
                else innate.Add(new InnatePower(kind, p.Amount));
            }
        }

        int hpMin = m.MinHp ?? 1;
        int hpMax = m.MaxHp ?? hpMin;
        int toughMin = m.MinHpAscension ?? hpMin;
        int toughMax = m.MaxHpAscension ?? (m.MinHpAscension != null ? toughMin : hpMax);
        bool exactNumbers = false;
        if (cls?.MinHp != null && cls.MaxHp != null)
        {
            exactNumbers = machine != null;
            hpMin = cls.MinHp.Base;
            hpMax = cls.MaxHp.Base;
            toughMin = cls.MinHp.Ascension > 0 ? cls.MinHp.Alt : hpMin;
            toughMax = cls.MaxHp.Ascension > 0 ? cls.MaxHp.Alt : hpMax;
            if (cls.MinHp.Ascension > 0 && cls.MinHp.Ascension != 8) unmodeled.Add($"hit points scale at ascension {cls.MinHp.Ascension}");
        }
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
            AltInitialState = machine?.AltInitial,
            StarterSwitch = starterSwitch,
            Innate = innate.ToArray(),
            InnateBlock = innateBlock,
            InnateBlockAlt = innateBlockAlt,
            InnateBlockAscension = innateBlockAscension,
            ExactAi = exact,
            ExactNumbers = exactNumbers,
            Unmodeled = unmodeled.Distinct().ToArray(),
            Approximate = ignored.Count > 0 || !exact || !movesResolved,
            IgnoredPowers = ignored.Distinct().ToArray(),
        };
    }
}
