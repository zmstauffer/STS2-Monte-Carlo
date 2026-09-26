using SpireMonteCarlo.Codex;

namespace SpireMonteCarlo.Sim;

/// <summary>One monster position in an encounter: it is filled with one of <see cref="Options"/> chosen uniformly.</summary>
public sealed record Slot(params string[] Options);

/// <summary>An encounter and how its monster lineup is generated (some encounters are random).</summary>
public sealed class EncounterDef
{
    public required string Id { get; init; }
    public string RoomType { get; init; } = "Monster";
    public string? Act { get; init; }
    public bool IsWeak { get; init; }

    /// <summary>One of these lineups is chosen uniformly each time the encounter is generated.</summary>
    public required Slot[][] Variants { get; init; }

    /// <summary>When set, no monster id repeats within a lineup (e.g. three different Ruby Raiders).</summary>
    public bool Distinct { get; init; }

    /// <summary>Lineup positions whose monster starts with its alternative first move (the game sets a flag on that one monster).</summary>
    public int[] AltStarts { get; init; } = Array.Empty<int>();

    /// <summary>The exact lineup wasn't available, so it was guessed from the Codex monster pool.</summary>
    public bool Approximate { get; init; }

    public string[] Generate(SimRng rng)
    {
        Slot[] variant = Variants[rng.Next(Variants.Length)];
        var chosen = new List<string>(variant.Length);
        foreach (Slot slot in variant)
        {
            string[] options = Distinct ? slot.Options.Where(o => !chosen.Contains(o)).ToArray() : slot.Options;
            if (options.Length == 0) options = slot.Options;
            chosen.Add(options[rng.Next(options.Length)]);
        }
        return chosen.ToArray();
    }
}

/// <summary>
/// Encounter lineups. Fixed lineups come from the decompiled game (via <see cref="DecompiledExtractor"/>); the
/// random ones needed so far are written out by hand below, each read from the game's own encounter class.
/// </summary>
public sealed class EncounterLibrary
{
    private static readonly string[] SmallSlimes = { "LEAF_SLIME_S", "TWIG_SLIME_S" };
    private static readonly string[] MediumSlimes = { "LEAF_SLIME_M", "TWIG_SLIME_M" };

    private static readonly Dictionary<string, (Slot[][] Variants, bool Distinct)> RandomLineups = new()
    {
        // Two different small slimes around one random medium slime.
        ["SLIMES_WEAK"] = (new[] { new[] { new Slot(SmallSlimes), new Slot(MediumSlimes), new Slot(SmallSlimes) } }, true),
        ["FLYCONID_NORMAL"] = (new[] { new[] { new Slot(MediumSlimes), new Slot("FLYCONID") } }, false),
        // The strangler always appears, with one of three secondary groups.
        ["SLITHERING_STRANGLER_NORMAL"] = (new[]
        {
            new[] { new Slot("SNAPPING_JAXFRUIT"), new Slot("SLITHERING_STRANGLER") },
            new[] { new Slot(MediumSlimes), new Slot("SLITHERING_STRANGLER") },
            new[] { new Slot(SmallSlimes), new Slot(SmallSlimes), new Slot("SLITHERING_STRANGLER") },
        }, false),
        ["SLIMES_NORMAL"] = (new[] { new[] { new Slot("TWIG_SLIME_M"), new Slot("LEAF_SLIME_M"), new Slot("LEAF_SLIME_S"), new Slot("TWIG_SLIME_S") } }, false),
        ["TWO_TAILED_RATS_NORMAL"] = (new[] { new[] { new Slot("TWO_TAILED_RAT"), new Slot("TWO_TAILED_RAT"), new Slot("TWO_TAILED_RAT") } }, false),
        // Three different raiders out of five.
        ["RUBY_RAIDERS_NORMAL"] = (new[]
        {
            Enumerable.Repeat(new Slot("AXE_RUBY_RAIDER", "ASSASSIN_RUBY_RAIDER", "BRUTE_RUBY_RAIDER", "CROSSBOW_RUBY_RAIDER", "TRACKER_RUBY_RAIDER"), 3).ToArray(),
        }, true),
    };

    /// <summary>Encounters that flag one monster to start on its alternative move, and which lineup position it is.</summary>
    private static readonly Dictionary<string, int[]> AltStartSlots = new()
    {
        ["INKLETS_NORMAL"] = new[] { 1 },      // the middle Inklet
        ["THE_KIN_BOSS"] = new[] { 0 },        // the first Kin Follower starts with its dance
    };

    private readonly Dictionary<string, EncounterDef> _encounters = new();

    public EncounterLibrary(IEnumerable<CodexEncounter> codexEncounters, DecompiledExtractor.EncounterExtraction? lineups)
    {
        foreach (CodexEncounter e in codexEncounters)
        {
            Slot[][] variants;
            bool distinct = false, approximate = false;
            if (RandomLineups.TryGetValue(e.Id, out var random))
                (variants, distinct) = random;
            else if (lineups != null && lineups.Fixed.TryGetValue(e.Id, out List<string>? fixedLineup))
                variants = new[] { fixedLineup.Select(m => new Slot(m)).ToArray() };
            else
            {
                // Unknown lineup: assume one of each monster in the Codex pool.
                variants = new[] { e.Monsters.Select(m => new Slot(m.Id)).ToArray() };
                approximate = true;
            }
            _encounters[e.Id] = new EncounterDef
            {
                Id = e.Id, RoomType = e.RoomType, Act = e.Act, IsWeak = e.IsWeak,
                Variants = variants, Distinct = distinct, Approximate = approximate,
                AltStarts = AltStartSlots.GetValueOrDefault(e.Id) ?? Array.Empty<int>(),
            };
        }
    }

    public IEnumerable<EncounterDef> All => _encounters.Values;

    public EncounterDef Get(string id) =>
        _encounters.TryGetValue(id, out EncounterDef? e) ? e : throw new KeyNotFoundException($"Unknown encounter '{id}'.");

    public bool Contains(string id) => _encounters.ContainsKey(id);
}
