using System.Text.RegularExpressions;

namespace SpireMonteCarlo.Codex;

/// <summary>
/// What a card's class in the decompiled game declares: cost, type, keywords, tags, its numeric variables, and what
/// upgrading changes. These numbers are the game's own, so the simulator uses them instead of Codex's (which lag patches).
/// What a card does when played is code, so the simulator implements that per card.
/// </summary>
public sealed class ExtractedCard
{
    public string Id { get; set; } = "";
    public string Class { get; set; } = "";
    public int Cost { get; set; }
    public string Type { get; set; } = "";
    public string Rarity { get; set; } = "";
    public string Target { get; set; } = "";
    public bool XCost { get; set; }
    public bool CanBeGeneratedInCombat { get; set; } = true;
    public bool MultiplayerOnly { get; set; }
    public List<string> Keywords { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, decimal> Vars { get; set; } = new();
    public Dictionary<string, decimal> UpgradeVars { get; set; } = new();
    public int UpgradeCost { get; set; }
    public List<string> UpgradeAddKeywords { get; set; } = new();
    public List<string> UpgradeRemoveKeywords { get; set; } = new();

    /// <summary>Statements in OnUpgrade the parser did not understand; the audit reports these.</summary>
    public List<string> UnparsedUpgrade { get; set; } = new();
}

public static class CardSourceExtractor
{
    private static readonly Regex Ctor = new(
        @"base\(\s*(?<cost>-?\d+)\s*,\s*CardType\.(?<type>\w+)\s*,\s*CardRarity\.(?<rarity>\w+)\s*,\s*TargetType\.(?<target>\w+)",
        RegexOptions.Compiled);

    private static readonly Regex TypedVar = new(
        @"new (?<kind>\w+?)Var(?:<(?<power>\w+)>)?\((?:""(?<name>\w+)"",\s*)?(?<value>-?[\d.]+)m?",
        RegexOptions.Compiled);

    private static readonly Regex NamedVar = new(
        @"new DynamicVar\(""(?<name>\w+)"",\s*(?<value>-?[\d.]+)m?\)",
        RegexOptions.Compiled);

    private static readonly Regex UpgradeByProperty = new(
        @"DynamicVars\.(?<name>\w+)\.UpgradeValueBy\((?<delta>-?[\d.]+)m\)", RegexOptions.Compiled);

    private static readonly Regex UpgradeByIndexer = new(
        @"DynamicVars\[""(?<name>\w+)""\]\.UpgradeValueBy\((?<delta>-?[\d.]+)m\)", RegexOptions.Compiled);

    private static readonly Regex CostUpgrade = new(@"EnergyCost\.UpgradeBy\((?<delta>-?\d+)\)", RegexOptions.Compiled);
    private static readonly Regex AddKeyword = new(@"AddKeyword\(CardKeyword\.(?<k>\w+)\)", RegexOptions.Compiled);
    private static readonly Regex RemoveKeyword = new(@"RemoveKeyword\(CardKeyword\.(?<k>\w+)\)", RegexOptions.Compiled);

    public static Dictionary<string, ExtractedCard> ExtractAll(string cardsDir)
    {
        var result = new Dictionary<string, ExtractedCard>();
        foreach (string file in Directory.EnumerateFiles(cardsDir, "*.cs"))
        {
            ExtractedCard? card = Parse(File.ReadAllText(file));
            if (card == null) continue;
            card.Id = ToId(card.Class);
            result[card.Id] = card;
        }
        return result;
    }

    /// <summary>"ExpectAFight" to "EXPECT_A_FIGHT": also splits an acronym-like single capital from the word after it.</summary>
    public static string ToId(string className) =>
        Regex.Replace(className, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", "_").ToUpperInvariant();

    public static ExtractedCard? Parse(string source)
    {
        Match cls = Regex.Match(source, @"public (?:sealed |abstract )?class (?<c>\w+) : CardModel");
        Match ctor = Ctor.Match(source);
        if (!cls.Success || !ctor.Success) return null;

        var card = new ExtractedCard
        {
            Class = cls.Groups["c"].Value,
            Cost = int.Parse(ctor.Groups["cost"].Value),
            Type = ctor.Groups["type"].Value,
            Rarity = ctor.Groups["rarity"].Value,
            Target = ctor.Groups["target"].Value,
            XCost = Regex.IsMatch(source, @"HasEnergyCostX\s*=>\s*true"),
            CanBeGeneratedInCombat = !Regex.IsMatch(source, @"CanBeGeneratedInCombat\s*=>\s*false"),
            MultiplayerOnly = source.Contains("CardMultiplayerConstraint.MultiplayerOnly"),
        };

        card.Keywords.AddRange(Members(source, "CanonicalKeywords", @"CardKeyword\.(\w+)").Distinct());
        card.Tags.AddRange(Members(source, "CanonicalTags", @"CardTag\.(\w+)").Distinct());

        string vars = Statement(source, "CanonicalVars");
        foreach (Match m in NamedVar.Matches(vars))
            card.Vars[m.Groups["name"].Value] = Number(m.Groups["value"].Value);
        foreach (Match m in TypedVar.Matches(vars))
        {
            string kind = m.Groups["kind"].Value;
            if (kind == "Dynamic") continue;
            string name = m.Groups["name"].Success ? m.Groups["name"].Value
                : m.Groups["power"].Success ? m.Groups["power"].Value
                : kind;
            card.Vars[name] = Number(m.Groups["value"].Value);
        }

        string? upgrade = DecompiledExtractor.MethodBody(source, "void OnUpgrade()");
        if (upgrade != null) ParseUpgrade(card, upgrade);
        return card;
    }

    private static void ParseUpgrade(ExtractedCard card, string body)
    {
        // Drop the signature and the braces, then look at each statement.
        int open = body.IndexOf('{');
        string inner = open < 0 ? body : body[(open + 1)..];
        foreach (string raw in inner.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string s = raw.Trim().TrimStart('{', '}').Trim();
            if (s.Length == 0 || s == "}") continue;

            Match m;
            if ((m = UpgradeByProperty.Match(s)).Success)
                AddDelta(card, ResolveVarName(card, m.Groups["name"].Value), Number(m.Groups["delta"].Value));
            else if ((m = UpgradeByIndexer.Match(s)).Success)
                AddDelta(card, m.Groups["name"].Value, Number(m.Groups["delta"].Value));
            else if ((m = CostUpgrade.Match(s)).Success)
                card.UpgradeCost += int.Parse(m.Groups["delta"].Value);
            else if ((m = AddKeyword.Match(s)).Success)
                card.UpgradeAddKeywords.Add(m.Groups["k"].Value);
            else if ((m = RemoveKeyword.Match(s)).Success)
                card.UpgradeRemoveKeywords.Add(m.Groups["k"].Value);
            else
                card.UnparsedUpgrade.Add(s);
        }
    }

    private static void AddDelta(ExtractedCard card, string name, decimal delta) =>
        card.UpgradeVars[name] = card.UpgradeVars.GetValueOrDefault(name) + delta;

    /// <summary>"Vulnerable" in DynamicVars.Vulnerable means the PowerVar named "VulnerablePower".</summary>
    private static string ResolveVarName(ExtractedCard card, string accessor) =>
        !card.Vars.ContainsKey(accessor) && card.Vars.ContainsKey(accessor + "Power") ? accessor + "Power" : accessor;

    private static decimal Number(string text) => decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The text of one member expression (from its name to the terminating semicolon).</summary>
    private static string Statement(string source, string member)
    {
        int start = source.IndexOf(member + " =>", StringComparison.Ordinal);
        if (start < 0) return "";
        int end = source.IndexOf(";\n", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static IEnumerable<string> Members(string source, string member, string pattern) =>
        Regex.Matches(Statement(source, member), pattern).Select(m => m.Groups[1].Value);
}
