using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Sim;

namespace SpireMonteCarlo.Tests;

/// <summary>Shared setup for the relic, potion, and card tests: one Codex-backed <see cref="SimData"/> and a helper that builds a small combat against a harmless dummy.</summary>
internal static class CombatKit
{
    private static readonly Lazy<SimData?> Shared = new(() =>
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadCardDefs() == null || cache.LoadMonsterClasses() == null ? null : new SimData(cache);
    });

    public static SimData? Data => Shared.Value;

    public static MonsterDef Dummy(int hp = 500, int damage = 0, int hits = 1) => new()
    {
        Id = "DUMMY", HpMin = hp, HpMax = hp, HpMinTough = hp, HpMaxTough = hp,
        Moves = { ["HIT"] = new MoveDef { Id = "HIT", Intent = damage > 0 ? "Attack" : "Buff", Damage = damage, DamageDeadly = damage, Hits = hits } },
        States = { ["HIT_MOVE"] = new StateDef { Id = "HIT_MOVE", Kind = StateKind.Move, MoveId = "HIT", Next = "HIT_MOVE" } },
        InitialState = "HIT_MOVE",
    };

    /// <summary>A combat whose draw pile is <paramref name="deck"/> (drawn into hand as usual) with the given relics.</summary>
    public static Combat Fight(string deck, RelicKind[]? relics = null, int stakes = 0, int hp = 80, MonsterDef[]? monsters = null, string potions = "", int seed = 5, int maxHp = 0)
    {
        SimData data = Data!;
        return new Combat(data.ParseDeck(deck), hp, maxHp > 0 ? maxHp : hp, monsters ?? new[] { Dummy() }, 10, (ulong)seed, services: data.Services, relics: relics, stakes: stakes,
            potions: potions.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => PotionLibrary.Find(id)!));
    }

    /// <summary>Replaces the hand (and empties the piles it came from) so a test controls exactly what it can play.</summary>
    public static Combat WithHand(Combat c, string hand)
    {
        c.Hand.Clear();
        c.Hand.AddRange(Data!.ParseDeck(hand).Select(x => x.Instantiate()));
        return c;
    }

    public static void Play(Combat c, string id, int target = 0) => c.Play(c.Hand.FindIndex(x => x.Id == id), target);
}
