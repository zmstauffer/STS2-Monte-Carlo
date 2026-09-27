using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>
/// The rollout's default player heals at a rest site when the rooms up to the next rest site would leave it low (ActRollout.WouldRestAt),
/// not below a flat 60% HP. Map from the fourth playtest (Act 1 floor 10, 49/80 HP): the right-hand rest site (column 6) is followed by
/// three fights and a "?" room before the next rest site; the one in column 4 by a single elite. Skipped when the local Codex cache isn't built.
/// </summary>
public class RestLookAheadTests
{
    private static RunSnapshot Load() =>
        SnapshotSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "map_act1_rest_or_unknown.json")));

    [Fact]
    public void ARestSiteHealsWhenTheRoomsBeforeTheNextOneWouldLeaveThePlayerLow()
    {
        if (CombatKit.Data == null) return;
        var rollout = new ActRollout(CombatKit.Data, Load());
        var longStretch = new MapCoordinate(6, 10);
        var oneElite = new MapCoordinate(4, 10);
        Assert.True(rollout.WouldRestAt(longStretch, 49, 80));    // 61% HP: the old rule upgraded here
        Assert.True(rollout.WouldRestAt(longStretch, 60, 80));
        Assert.False(rollout.WouldRestAt(oneElite, 60, 80));      // one elite, then another rest site
        Assert.False(rollout.WouldRestAt(longStretch, 76, 80));   // the heal would give only 4 HP
    }
}
