using System.Text.RegularExpressions;

namespace SpireMonteCarlo.Codex;

public sealed class ExtractedBranch
{
    public string StateId { get; set; } = "";
    public double Weight { get; set; } = 1;
    /// <summary>Default, CannotRepeat, CanRepeatForever, or UseOnlyOnce.</summary>
    public string Repeat { get; set; } = "Default";
    /// <summary>C# condition text for conditional branches (e.g. "!((Nibbit)Creature.Monster).IsFront").</summary>
    public string? Condition { get; set; }
    /// <summary>The game computes this branch's weight at runtime, so <see cref="Weight"/> is only a placeholder.</summary>
    public bool DynamicWeight { get; set; }
}

public sealed class ExtractedState
{
    public string Id { get; set; } = "";
    /// <summary>move, random, or conditional.</summary>
    public string Kind { get; set; } = "move";
    /// <summary>For move states, the Codex move id ("GLOMP_MOVE" is move "GLOMP").</summary>
    public string? MoveId { get; set; }
    public string? Next { get; set; }
    public List<ExtractedBranch> Branches { get; set; } = new();
}

public sealed class ExtractedMachine
{
    public string? Initial { get; set; }

    /// <summary>
    /// When the start is "flag ? a : b": <see cref="Initial"/> is b (flag off, the usual case) and this is a (flag on).
    /// The flag is set per encounter for one particular monster (e.g. the middle Inklet).
    /// </summary>
    public string? AltInitial { get; set; }

    /// <summary>
    /// When the starting state depends on the monster's StarterMoveIdx: the states in index order, where the index
    /// is taken modulo the list length. Empty when <see cref="Initial"/> is fixed.
    /// </summary>
    public List<string> StarterSwitch { get; set; } = new();

    public List<ExtractedState> States { get; set; } = new();
}

/// <summary>Reads each monster's move state machine out of its decompiled class (Codex leaves out links and weights).</summary>
public static class MonsterAiExtractor
{
    private static readonly Regex NewState = new(@"new\s+(?<kind>MoveState|RandomBranchState|ConditionalBranchState)\(\s*""(?<id>[A-Za-z0-9_]+)""", RegexOptions.Compiled);
    private static readonly Regex InitializerFollowUp = new(@"\{\s*FollowUpState\s*=\s*(?<to>\w+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex Assigned = new(@"^\s*(?:(?:MoveState|RandomBranchState|ConditionalBranchState)\s+)?(?<v>\w+)\s*=", RegexOptions.Compiled);
    private static readonly Regex InlineFollowUp = new(@"(?<prev>\w+)\.FollowUpState\s*=", RegexOptions.Compiled);
    private static readonly Regex FollowUp = new(@"(?<from>\w+)\.FollowUpState\s*=\s*(?<to>\w+)\s*;", RegexOptions.Compiled);
    private static readonly Regex AddBranch = new(@"(?<rb>\w+)\.AddBranch\(\s*(?<state>\w+)(?<args>[^;]*)\)\s*;", RegexOptions.Compiled);
    private static readonly Regex AddState = new(@"(?<cb>\w+)\.AddState\(\s*(?<state>\w+)\s*,\s*\(\)\s*=>\s*(?<cond>[^;]*?)\)\s*;", RegexOptions.Compiled);
    private static readonly Regex Repeat = new(@"MoveRepeatType\.(?<r>\w+)", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"(?<![\w.])(?<n>\d+(?:\.\d+)?)f?(?![\w.])", RegexOptions.Compiled);
    private static readonly Regex SwitchArm = new(@"=>\s*(?:\(MonsterState\)\s*)?(?<var>\w+)\s*,", RegexOptions.Compiled);

    /// <summary>"GLOMP_MOVE" and "ZOOM_MOVE_2" are the moves GLOMP and ZOOM; states without a suffix ("SWING_1") are named after their move.</summary>
    public static string MoveIdFromStateId(string stateId) => Regex.Replace(stateId, @"_MOVE(_\d+)?$", "");

    /// <returns>Machine per monster id (UPPER_SNAKE_CASE), for the monsters whose class defines its own machine.</returns>
    public static Dictionary<string, ExtractedMachine> ExtractAll(string monstersDir)
    {
        var result = new Dictionary<string, ExtractedMachine>();
        foreach (string file in Directory.GetFiles(monstersDir, "*.cs"))
        {
            string? body = DecompiledExtractor.MethodBody(File.ReadAllText(file), "GenerateMoveStateMachine()");
            if (body == null) continue;
            ExtractedMachine? machine = Parse(body);
            if (machine != null) result[DecompiledExtractor.ToSnakeCase(Path.GetFileNameWithoutExtension(file))] = machine;
        }
        return result;
    }

    /// <summary>Index of the ')' closing a call whose '(' and first argument have already been consumed (depth 1 at <paramref name="from"/>).</summary>
    private static int MatchingParen(string text, int from)
    {
        int depth = 1;
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    public static ExtractedMachine? Parse(string body)
    {
        var byVar = new Dictionary<string, ExtractedState>();
        var chained = new List<(string prev, ExtractedState state)>();
        var initializers = new List<(ExtractedState state, string to)>();
        foreach (Match m in NewState.Matches(body))
        {
            string kind = m.Groups["kind"].Value;
            string id = m.Groups["id"].Value;
            var state = new ExtractedState
            {
                Id = id,
                Kind = kind == "MoveState" ? "move" : kind == "RandomBranchState" ? "random" : "conditional",
                MoveId = kind == "MoveState" ? MoveIdFromStateId(id) : null,
            };

            // The statement so far can name the variable being assigned and any states whose follow-up this one is
            // (e.g. "MoveState b = (MoveState)(a.FollowUpState = new MoveState(...))").
            int statementStart = body.LastIndexOf(';', m.Index) + 1;
            string prefix = body[statementStart..m.Index];
            Match declared = Assigned.Match(prefix);
            if (declared.Success) byVar[declared.Groups["v"].Value] = state;
            else byVar[$"#{byVar.Count}"] = state;
            foreach (Match prev in InlineFollowUp.Matches(prefix)) chained.Add((prev.Groups["prev"].Value, state));

            // "new MoveState(...) { FollowUpState = other }" sets this state's own follow-up in an object initializer.
            int close = MatchingParen(body, m.Index + m.Length);
            if (close >= 0)
            {
                Match init = InitializerFollowUp.Match(body, close + 1);
                if (init.Success && string.IsNullOrWhiteSpace(body[(close + 1)..init.Index])) initializers.Add((state, init.Groups["to"].Value));
            }
        }
        if (byVar.Count == 0) return null;
        foreach ((string prev, ExtractedState state) in chained)
            if (byVar.TryGetValue(prev, out ExtractedState? from)) from.Next = state.Id;
        foreach ((ExtractedState state, string to) in initializers)
            if (byVar.TryGetValue(to, out ExtractedState? target)) state.Next = target.Id;

        foreach (Match m in FollowUp.Matches(body))
            if (byVar.TryGetValue(m.Groups["from"].Value, out ExtractedState? from) && byVar.TryGetValue(m.Groups["to"].Value, out ExtractedState? to))
                from.Next = to.Id;

        foreach (Match m in AddBranch.Matches(body))
        {
            if (!byVar.TryGetValue(m.Groups["rb"].Value, out ExtractedState? rb) || !byVar.TryGetValue(m.Groups["state"].Value, out ExtractedState? target)) continue;
            string args = m.Groups["args"].Value;
            bool dynamic = args.Contains("=>");
            string plain = dynamic ? args[..args.IndexOf("()", StringComparison.Ordinal)] : args;
            Match repeat = Repeat.Match(plain);
            Match number = Number.Match(Repeat.Replace(plain, ""));
            rb.Branches.Add(new ExtractedBranch
            {
                StateId = target.Id,
                Weight = number.Success ? double.Parse(number.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1,
                Repeat = repeat.Success ? repeat.Groups["r"].Value : "Default",
                DynamicWeight = dynamic,
            });
        }

        foreach (Match m in AddState.Matches(body))
            if (byVar.TryGetValue(m.Groups["cb"].Value, out ExtractedState? cb) && byVar.TryGetValue(m.Groups["state"].Value, out ExtractedState? target))
                cb.Branches.Add(new ExtractedBranch { StateId = target.Id, Condition = m.Groups["cond"].Value.Trim() });

        var machine = new ExtractedMachine { States = byVar.Values.ToList() };
        int ret = body.IndexOf("return new MonsterMoveStateMachine(", StringComparison.Ordinal);
        if (ret >= 0)
        {
            int open = ret + "return new MonsterMoveStateMachine(".Length;
            int close = MatchingParen(body, open);
            string args = close > open ? body[open..close] : "";
            string init = SecondArgument(args);
            if (init.Contains("switch"))
                machine.StarterSwitch = SwitchArm.Matches(init).Select(a => a.Groups["var"].Value).Where(byVar.ContainsKey).Select(v => byVar[v].Id).ToList();
            else
            {
                (string? initial, string? alt) = ResolveInitial(init, body, byVar, 0);
                machine.Initial = initial;
                machine.AltInitial = alt;
            }
        }
        return machine;
    }

    /// <summary>The text after the first top-level comma of a call's argument list.</summary>
    private static string SecondArgument(string args)
    {
        int depth = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == '(' || args[i] == '<') depth++;
            else if (args[i] == ')' || args[i] == '>') depth--;
            else if (args[i] == ',' && depth == 0) return args[(i + 1)..].Trim();
        }
        return "";
    }

    private static readonly Regex StateCast = new(@"\(\s*(?:MonsterState|MoveState|RandomBranchState|ConditionalBranchState)\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// Resolves a starting-state expression: a variable, an alias declared earlier ("MoveState initialState = ..."),
    /// or a ternary "flag ? a : b", where b is the default and a the alternative used when the flag is set.
    /// </summary>
    private static (string? Initial, string? Alt) ResolveInitial(string expr, string body, Dictionary<string, ExtractedState> byVar, int depth)
    {
        expr = StateCast.Replace(expr, "").Trim();
        while (expr.StartsWith('(') && expr.EndsWith(')') && MatchingParen(expr, 1) == expr.Length - 1) expr = expr[1..^1].Trim();

        int q = expr.IndexOf('?');
        if (q >= 0)
        {
            int colon = expr.IndexOf(':', q);
            if (colon < 0) return (null, null);
            string? whenSet = ResolveInitial(expr[(q + 1)..colon], body, byVar, depth + 1).Initial;
            string? byDefault = ResolveInitial(expr[(colon + 1)..], body, byVar, depth + 1).Initial;
            return (byDefault, whenSet);
        }
        if (byVar.TryGetValue(expr, out ExtractedState? direct)) return (direct.Id, null);
        if (depth < 3)
        {
            Match alias = Regex.Match(body, @"\b" + Regex.Escape(expr) + @"\s*=\s*(?<rhs>[^;]+);");
            if (alias.Success) return ResolveInitial(alias.Groups["rhs"].Value, body, byVar, depth + 1);
        }
        return (null, null);
    }
}
