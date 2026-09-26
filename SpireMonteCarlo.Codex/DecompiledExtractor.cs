using System.Text.RegularExpressions;

namespace SpireMonteCarlo.Codex;

/// <summary>
/// Reads facts the Codex export lacks straight from the decompiled game source (the ilspycmd output). The results are
/// written to the local cache only: the source and anything derived from it must never be committed or redistributed.
/// </summary>
public static class DecompiledExtractor
{
    private static readonly Regex MonsterRef = new(@"ModelDb\.Monster<(\w+)>\(\)", RegexOptions.Compiled);
    private static readonly Regex Randomness = new(@"Rng\.Next|\bfor \(|foreach|while \(|switch \(", RegexOptions.Compiled);

    public sealed class EncounterExtraction
    {
        /// <summary>Encounters whose lineup is a fixed, ordered list of monsters: encounter id to monster ids.</summary>
        public Dictionary<string, List<string>> Fixed { get; set; } = new();

        /// <summary>Encounters whose lineup involves random choices; their lineup needs a hand-written definition.</summary>
        public List<string> Random { get; set; } = new();
    }

    /// <param name="encountersDir">MegaCrit.Sts2.Core.Models.Encounters inside the decompiled output.</param>
    public static EncounterExtraction ExtractEncounters(string encountersDir)
    {
        var result = new EncounterExtraction();
        foreach (string file in Directory.GetFiles(encountersDir, "*.cs"))
        {
            string? body = MethodBody(File.ReadAllText(file), "GenerateMonsters()");
            if (body == null) continue;
            string id = ToSnakeCase(Path.GetFileNameWithoutExtension(file));
            if (Randomness.IsMatch(body)) { result.Random.Add(id); continue; }
            var monsters = MonsterRef.Matches(body).Select(m => ToSnakeCase(m.Groups[1].Value)).ToList();
            if (monsters.Count > 0) result.Fixed[id] = monsters;
        }
        result.Random.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>"CorpseSlugsNormal" to "CORPSE_SLUGS_NORMAL" (the ids the Codex export and the mod use).</summary>
    public static string ToSnakeCase(string pascal) =>
        Regex.Replace(pascal, "(?<=[a-z0-9])(?=[A-Z])", "_").ToUpperInvariant();

    /// <summary>Text of the method whose declaration contains <paramref name="signature"/>, up to its closing brace.</summary>
    internal static string? MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;
        // Decompiled code puts a member's closing brace on its own line at member indentation.
        int end = source.IndexOf("\n\t}", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }
}
