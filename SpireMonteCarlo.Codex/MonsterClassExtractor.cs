using System.Text.RegularExpressions;

namespace SpireMonteCarlo.Codex;

/// <summary>A number that changes at an ascension level: <see cref="Alt"/> from <see cref="Ascension"/> upwards, else <see cref="Base"/>.</summary>
public sealed class ScaledValue
{
    public int Base { get; set; }
    public int Alt { get; set; }
    /// <summary>0 when the value never changes.</summary>
    public int Ascension { get; set; }

    public int At(int ascension) => Ascension > 0 && ascension >= Ascension ? Alt : Base;
}

public sealed class ExtractedPowerApply
{
    /// <summary>The power's class name without the "Power" suffix, e.g. Strength.</summary>
    public string Power { get; set; } = "";
    public ScaledValue? Amount { get; set; }
    /// <summary>Self (the monster), Player, Allies, Opponents, or the raw argument text for anything else.</summary>
    public string Target { get; set; } = "";
}

public sealed class ExtractedAttack
{
    public ScaledValue? Damage { get; set; }
    public ScaledValue? Hits { get; set; }
}

public sealed class ExtractedBlock
{
    public ScaledValue? Amount { get; set; }
    public string Target { get; set; } = "Self";
}

public sealed class ExtractedMove
{
    public string Method { get; set; } = "";
    public List<ExtractedAttack> Attacks { get; set; } = new();
    public List<ExtractedBlock> Blocks { get; set; } = new();
    public List<ExtractedPowerApply> Powers { get; set; } = new();
    public List<string> Spawns { get; set; } = new();
    public List<string> Heals { get; set; } = new();

    /// <summary>Calls the parser does not model (Kill, Escape, Remove, ...), so the audit can list them.</summary>
    public List<string> Other { get; set; } = new();

    /// <summary>The method contains conditions or loops around its effects.</summary>
    public bool HasLogic { get; set; }
}

public sealed class ExtractedMonster
{
    public string Id { get; set; } = "";
    public string Class { get; set; } = "";
    public ScaledValue? MinHp { get; set; }
    public ScaledValue? MaxHp { get; set; }
    public List<ExtractedPowerApply> Innate { get; set; } = new();
    public List<ExtractedBlock> InnateBlock { get; set; } = new();
    public List<string> InnateOther { get; set; } = new();
    /// <summary>Move methods by method name.</summary>
    public Dictionary<string, ExtractedMove> Moves { get; set; } = new();
    /// <summary>Method name of each move state.</summary>
    public Dictionary<string, string> StateMethods { get; set; } = new();
}

/// <summary>
/// Reads each monster's numbers and what its moves do straight out of the decompiled class: hit points, damage, hit counts,
/// block, powers applied, and cards and monsters added. Codex has these too but lags the game and omits powers.
/// </summary>
public static class MonsterClassExtractor
{
    private static readonly Dictionary<string, int> Levels = new()
    {
        ["SwarmingElites"] = 1, ["WearyTraveler"] = 2, ["Poverty"] = 3, ["TightBelt"] = 4, ["AscendersBane"] = 5,
        ["Inflation"] = 6, ["Scarcity"] = 7, ["ToughEnemies"] = 8, ["DeadlyEnemies"] = 9, ["DoubleBoss"] = 10,
    };

    private static readonly Regex Ascension = new(@"^AscensionHelper\.GetValueIfAscension\(\s*AscensionLevel\.(?<level>\w+)\s*,\s*(?<a>.+?)\s*,\s*(?<b>[^,]+?)\s*\)$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex MoveStateCtor = new(@"new\s+MoveState\(\s*""(?<id>\w+)""\s*,\s*(?<method>\w+)", RegexOptions.Compiled);

    public static Dictionary<string, ExtractedMonster> ExtractAll(string monstersDir)
    {
        var result = new Dictionary<string, ExtractedMonster>();
        Dictionary<string, string> sources = DecompiledExtractor.MonsterSourcesWithBases(monstersDir);
        foreach (string file in Directory.GetFiles(monstersDir, "*.cs"))
        {
            string text = sources.GetValueOrDefault(Path.GetFileNameWithoutExtension(file)) ?? File.ReadAllText(file);
            Match cls = Regex.Match(text, @"public (?:sealed |abstract )?class (?<c>\w+) : (?:MonsterModel|\w+Segment)");
            if (!cls.Success) continue;
            ExtractedMonster monster = Parse(text);
            monster.Class = cls.Groups["c"].Value;
            monster.Id = DecompiledExtractor.ToSnakeCase(monster.Class);
            result[monster.Id] = monster;
        }
        return result;
    }

    public static ExtractedMonster Parse(string text)
    {
        var monster = new ExtractedMonster
        {
            MinHp = Resolve(text, PropertyExpression(text, "MinInitialHp"), 0),
        };
        string? maxExpr = PropertyExpression(text, "MaxInitialHp");
        monster.MaxHp = maxExpr == "MinInitialHp" ? monster.MinHp : Resolve(text, maxExpr, 0);

        string? machine = DecompiledExtractor.MethodBody(text, "GenerateMoveStateMachine()");
        if (machine != null)
            foreach (Match m in MoveStateCtor.Matches(machine))
                monster.StateMethods[m.Groups["id"].Value] = m.Groups["method"].Value;

        foreach (string method in monster.StateMethods.Values.Distinct())
        {
            string? body = DecompiledExtractor.MethodBody(text, $"Task {method}(");
            if (body == null) continue;
            var move = new ExtractedMove { Method = method };
            ParseBody(text, body, move.Attacks, move.Blocks, move.Powers, move.Spawns, move.Heals, move.Other);
            move.HasLogic = Regex.IsMatch(WithoutPresentation(body), @"\b(foreach|for|while)\b|\bif\s*\((?!\s*(CombatState\.IsLiveCombat|!CombatState\.IsLiveCombat))");
            monster.Moves[method] = move;
        }

        string? added = DecompiledExtractor.MethodBody(text, "Task AfterAddedToRoom()");
        if (added != null)
        {
            var attacks = new List<ExtractedAttack>();
            var heals = new List<string>();
            var spawns = new List<string>();
            ParseBody(text, added, attacks, monster.InnateBlock, monster.Innate, spawns, heals, monster.InnateOther);
            monster.InnateOther.AddRange(spawns.Select(s => "spawns " + s));
        }
        return monster;
    }

    private static void ParseBody(string fileText, string body, List<ExtractedAttack> attacks, List<ExtractedBlock> blocks,
        List<ExtractedPowerApply> powers, List<string> spawns, List<string> heals, List<string> other)
    {
        foreach (Call call in Calls(body, "DamageCmd.Attack("))
        {
            string statement = body[call.Start..Math.Max(call.Start, body.IndexOf(';', call.End) is var e and >= 0 ? e : body.Length)];
            var attack = new ExtractedAttack { Damage = Resolve(fileText, call.Args[0], 0) };
            Call? hitCount = Calls(statement, ".WithHitCount(").FirstOrDefault();
            attack.Hits = hitCount == null ? new ScaledValue { Base = 1 } : Resolve(fileText, hitCount.Args[0], 0);
            attacks.Add(attack);
        }

        foreach (Match m in Regex.Matches(body, @"PowerCmd\.Apply<(?<p>\w+?)(?:Power)?>\("))
        {
            Call call = ReadCall(body, m.Index + m.Length - 1, m.Index);
            // (context, target, amount, applier, cardSource[, silent])
            if (call.Args.Count < 3) { other.Add(m.Value + "..."); continue; }
            powers.Add(new ExtractedPowerApply
            {
                Power = m.Groups["p"].Value,
                Target = ClassifyTarget(call.Args[1]),
                Amount = Resolve(fileText, call.Args[2], 0),
            });
        }

        foreach (Call call in Calls(body, "CreatureCmd.GainBlock("))
            blocks.Add(new ExtractedBlock { Target = ClassifyTarget(call.Args[0]), Amount = Resolve(fileText, call.Args.Count > 1 ? call.Args[1] : "", 0) });

        foreach (Match m in Regex.Matches(body, @"CreatureCmd\.Add<(?<m>\w+)>")) spawns.Add(DecompiledExtractor.ToSnakeCase(m.Groups["m"].Value));
        if (Regex.IsMatch(body, @"CreatureCmd\.Add\((?!<)")) spawns.Add("(computed)");
        foreach (Call call in Calls(body, "CreatureCmd.Heal(")) heals.Add(string.Join(", ", call.Args));
        foreach (Match m in Regex.Matches(body, @"CreatureCmd\.(?<c>Kill|Escape|SetMaxAndCurrentHp|LoseBlock|GainMaxHp|LoseMaxHp|Stun|Damage)\b")) other.Add("CreatureCmd." + m.Groups["c"].Value);
        foreach (Match m in Regex.Matches(body, @"PowerCmd\.Remove(?:<(?<p>\w+)>)?\(")) other.Add("PowerCmd.Remove" + (m.Groups["p"].Success ? "<" + m.Groups["p"].Value + ">" : ""));
        foreach (Match m in Regex.Matches(body, @"PowerCmd\.Apply\((?!<)")) other.Add("PowerCmd.Apply(instance)");
        foreach (Match m in Regex.Matches(body, @"(?:PlayerCmd|OstyCmd|CardCmd)\.(?<c>\w+)"))
            if (m.Groups["c"].Value != "PreviewCardPileAdd") other.Add(m.Value);
        if (Regex.IsMatch(body, @"CardPileCmd\.(?!AddToCombatAndPreview|AddGeneratedCardToCombat)\w+")) other.Add("CardPileCmd.other");
    }

    /// <summary>The body with the parts that only drive animation removed: "if (TestMode.IsOff)" blocks and loops that run no command.</summary>
    private static string WithoutPresentation(string body)
    {
        var spans = new List<(int Start, int End)>();
        foreach (Match header in Regex.Matches(body, @"(?:if\s*\(\s*TestMode\.IsOff\s*\)|foreach\s*\([^)]*\)|using\s*\([^)]*\)|if\s*\(\s*LocalContext\.\w+\([^)]*\)\s*\)|if\s*\(\s*\w+\s*!=\s*null\s*\)|if\s*\(\s*TestMode\.IsOff\s*&&[^)]*\))\s*\{"))
        {
            int open = header.Index + header.Length - 1;
            int depth = 0, close = -1;
            for (int i = open; i < body.Length; i++)
            {
                if (body[i] == '{') depth++;
                else if (body[i] == '}' && --depth == 0) { close = i; break; }
            }
            if (close < 0) continue;
            string inner = body[open..(close + 1)];
            bool isTestMode = header.Value.Contains("TestMode");
            if (isTestMode || !Regex.IsMatch(inner, @"\b(PowerCmd|CreatureCmd|CardPileCmd|DamageCmd|PlayerCmd|CardCmd)\."))
                spans.Add((header.Index, close + 1));
        }
        var sb = new System.Text.StringBuilder();
        int at = 0;
        foreach ((int start, int end) in spans.OrderBy(s => s.Start))
        {
            if (start < at) { at = Math.Max(at, end); continue; }
            sb.Append(body, at, start - at);
            at = end;
        }
        sb.Append(body, at, body.Length - at);
        return sb.ToString();
    }

    private static string ClassifyTarget(string arg)
    {
        arg = arg.Trim();
        if (arg == "Creature") return "Self";
        if (arg is "targets" or "targets[0]" or "target") return "Player";
        if (arg.Contains("GetTeammatesOf")) return "Allies";
        if (arg.Contains("GetOpponentsOf")) return "Opponents";
        return arg;
    }

    // ---- expression resolution ----------------------------------------------------------------------------

    /// <summary>The right-hand side of "X =&gt; expr;" or a const/readonly field named <paramref name="name"/>.</summary>
    private static string? PropertyExpression(string text, string name)
    {
        Match arrow = Regex.Match(text, @"\b(?:int|decimal|float|double)\s+" + Regex.Escape(name) + @"\s*=>\s*(?<e>[^;]+);");
        if (arrow.Success) return arrow.Groups["e"].Value.Trim();
        Match arrowAny = Regex.Match(text, @"\b" + Regex.Escape(name) + @"\s*=>\s*(?<e>[^;]+);");
        if (arrowAny.Success) return arrowAny.Groups["e"].Value.Trim();
        Match field = Regex.Match(text, @"\b(?:const|static readonly|readonly)\s+(?:int|decimal|float|double)\s+" + Regex.Escape(name) + @"\s*=\s*(?<e>[^;]+);");
        if (field.Success) return field.Groups["e"].Value.Trim();
        Match local = Regex.Match(text, @"\b(?:int|decimal|float|double|var)\s+" + Regex.Escape(name) + @"\s*=\s*(?<e>[^;]+);");
        return local.Success ? local.Groups["e"].Value.Trim() : null;
    }

    public static ScaledValue? Resolve(string fileText, string? expr, int depth)
    {
        if (expr == null || depth > 4) return null;
        expr = expr.Trim();
        while (expr.StartsWith('(') && expr.EndsWith(')') && MatchingParen(expr, 1) == expr.Length - 1) expr = expr[1..^1].Trim();
        expr = Regex.Replace(expr, @"^\(\s*(?:int|decimal)\s*\)\s*", "").Trim();

        if (expr.StartsWith('-') && !Regex.IsMatch(expr, @"^-\d"))
        {
            ScaledValue? inner = Resolve(fileText, expr[1..], depth + 1);
            return inner == null ? null : new ScaledValue { Base = -inner.Base, Alt = -inner.Alt, Ascension = inner.Ascension };
        }
        Match number = Regex.Match(expr, @"^(?<n>-?\d+(?:\.\d+)?)[mfMF]?$");
        if (number.Success) return new ScaledValue { Base = (int)Math.Round(double.Parse(number.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)) };

        Match asc = Ascension.Match(expr);
        if (asc.Success && Levels.TryGetValue(asc.Groups["level"].Value, out int level))
        {
            ScaledValue? alt = Resolve(fileText, asc.Groups["a"].Value, depth + 1);
            ScaledValue? baseValue = Resolve(fileText, asc.Groups["b"].Value, depth + 1);
            if (alt == null || baseValue == null) return null;
            return new ScaledValue { Base = baseValue.Base, Alt = alt.Base, Ascension = level };
        }

        if (Regex.IsMatch(expr, @"^\w+$"))
            return Resolve(fileText, PropertyExpression(fileText, expr), depth + 1);
        return null;
    }

    // ---- small parsing helpers ----------------------------------------------------------------------------

    private sealed record Call(int Start, int End, List<string> Args);

    private static IEnumerable<Call> Calls(string text, string prefix)
    {
        int at = 0;
        while ((at = text.IndexOf(prefix, at, StringComparison.Ordinal)) >= 0)
        {
            int open = at + prefix.Length - 1;
            yield return ReadCall(text, open, at);
            at = open + 1;
        }
    }

    /// <summary>Reads the call whose '(' is at <paramref name="open"/>, splitting its arguments at top-level commas.</summary>
    private static Call ReadCall(string text, int open, int start)
    {
        int depth = 0, argStart = open + 1;
        var args = new List<string>();
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            if (depth == 0)
            {
                string last = text[argStart..i].Trim();
                if (last.Length > 0) args.Add(last);
                return new Call(start, i + 1, args);
            }
            if (c == ',' && depth == 1)
            {
                args.Add(text[argStart..i].Trim());
                argStart = i + 1;
            }
        }
        return new Call(start, text.Length, args);
    }

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
}
