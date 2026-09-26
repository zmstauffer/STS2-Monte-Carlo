using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Sim;

namespace SpireMonteCarlo.Advisor;

/// <summary>
/// Lists everything the simulator still models only approximately, so completeness is measured rather than assumed:
/// cards without an exact recipe, monsters that ignore a power or use unverified data, and guessed encounter lineups.
/// </summary>
public static class AuditCommand
{
    public static int Run(string[] args)
    {
        var cache = new CodexCache();
        if (cache.ReadMeta() == null || cache.LoadCardDefs() == null)
        {
            Console.Error.WriteLine("Run 'advisor codex update' and 'advisor sim extract' first.");
            return 1;
        }
        var data = new SimData(cache);
        var defs = cache.LoadCardDefs()!;
        int problems = 0;

        Console.WriteLine("== Cards (exact recipe written from the game's class) ==");
        foreach (var group in cache.LoadCards().Values.GroupBy(c => c.Color).OrderBy(g => g.Key))
        {
            var cards = group.ToList();
            var missing = cards.Where(c => !(defs.TryGetValue(c.Id, out ExtractedCard? ex) && ex.MultiplayerOnly) && !data.Cards.HasRecipe(c.Id)).Select(c => c.Id).OrderBy(id => id).ToList();
            int multiplayer = cards.Count(c => defs.TryGetValue(c.Id, out ExtractedCard? ex) && ex.MultiplayerOnly);
            Console.WriteLine($"  {group.Key,-12} {cards.Count - missing.Count - multiplayer,3} exact, {multiplayer} multiplayer-only (skipped), {missing.Count} missing");
            if (args.Contains("--verbose") && missing.Count > 0) Console.WriteLine("      " + string.Join(", ", missing));
            if (group.Key is "ironclad" or "colorless" or "curse" or "status" or "token" or "event" or "quest") problems += missing.Count;
        }
        var codexIds = cache.LoadCards().Keys.ToHashSet();
        var newer = defs.Keys.Where(id => !codexIds.Contains(id)).OrderBy(id => id).ToList();
        Console.WriteLine($"  {newer.Count} cards exist in the game but not in Codex: {string.Join(", ", newer)}");

        Console.WriteLine();
        Console.WriteLine("== Monsters used by encounters ==");
        var used = data.Encounters.All.SelectMany(e => e.Variants.SelectMany(v => v).SelectMany(s => s.Options)).Distinct().OrderBy(id => id).ToList();
        int flagged = 0;
        foreach (string id in used)
        {
            if (!data.Monsters.Contains(id)) { Console.WriteLine($"  {id}: not in Codex"); flagged++; continue; }
            MonsterDef m = data.Monsters.Get(id);
            var notes = new List<string>();
            if (!m.ExactAi) notes.Add("AI from Codex, not the game's class");
            if (m.IgnoredPowers.Length > 0) notes.Add("ignored powers: " + string.Join(", ", m.IgnoredPowers));
            if (m.Approximate && notes.Count == 0) notes.Add("some move is unresolved");
            foreach (string note in m.Unmodeled.Where(n => !MonsterBehaviors.Handles(id, n))) notes.Add(note);
            var dynamicWeights = m.States.Values.SelectMany(s => s.Branches).Where(b => b.DynamicWeight).Select(b => b.StateId).Distinct().ToList();
            if (dynamicWeights.Count > 0 && !MonsterBehaviors.Handles(id, "dynamic weights")) notes.Add("branch weights computed at runtime: " + string.Join(", ", dynamicWeights));
            var unknown = m.States.Values.Where(s => s.Kind == StateKind.Conditional)
                .SelectMany(s => s.Branches).Select(b => b.Condition).Where(c => c != null && !Enemy.IsKnownCondition(id, c)).Distinct().ToList();
            if (unknown.Count > 0) notes.Add("unknown conditions: " + string.Join(" | ", unknown));
            if (notes.Count == 0) continue;
            flagged++;
            Console.WriteLine($"  {id}: {string.Join("; ", notes)}");
        }
        Console.WriteLine($"  {flagged} of {used.Count} monsters need work");
        problems += flagged;

        Console.WriteLine();
        Console.WriteLine("== Encounters ==");
        var guessed = data.Encounters.All.Where(e => e.Approximate).Select(e => e.Id).OrderBy(id => id).ToList();
        Console.WriteLine($"  {guessed.Count} with a guessed lineup: {string.Join(", ", guessed)}");
        problems += guessed.Count;

        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "Nothing approximate." : $"{problems} items still approximate.");
        return 0;
    }
}
