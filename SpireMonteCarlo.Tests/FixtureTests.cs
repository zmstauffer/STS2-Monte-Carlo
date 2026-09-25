using SpireMonteCarlo.Advisor;
using SpireMonteCarlo.Contracts;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>Snapshots captured from real runs on game v0.111.0.</summary>
public class FixtureTests
{
    private static RunSnapshot Load(string name) =>
        SnapshotSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    public static TheoryData<string> AllFixtures => new()
    {
        "card_reward_act1.json", "rest_site_act1.json", "map_act2_start.json", "event_ancient_act2.json", "shop_act1.json",
    };

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void EveryFixtureLoadsAndHasABasicRunState(string name)
    {
        RunSnapshot s = Load(name);
        Assert.Equal(RunSnapshot.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Equal("v0.111.0", s.GameVersion);
        Assert.Equal("regent", s.Run.Character);
        Assert.NotEmpty(s.Deck);
        Assert.NotEmpty(s.Relics);
        Assert.NotNull(s.Map);
        Assert.NotEmpty(s.Map!.Points);
        Assert.False(string.IsNullOrWhiteSpace(SnapshotSummary.Render(s)));
    }

    [Fact]
    public void CardRewardListsTheOfferedCards()
    {
        RunSnapshot s = Load("card_reward_act1.json");
        Assert.Equal(DecisionType.CardReward, s.Decision);
        Assert.Equal(new[] { "SOLAR_STRIKE", "PROPHESIZE", "GUIDING_STAR" }, s.Offer.Cards.Select(c => c.Id));
    }

    [Fact]
    public void ShopHasPricesForEverythingOnSale()
    {
        RunSnapshot s = Load("shop_act1.json");
        Assert.Equal(DecisionType.Shop, s.Decision);
        Assert.Equal(7, s.Offer.Cards.Count);
        Assert.All(s.Offer.Cards, c => Assert.True(c.Price > 0));
        Assert.All(s.Offer.Relics, r => Assert.True(r.Price > 0));
        Assert.All(s.Offer.Potions, p => Assert.True(p.Price > 0));
    }

    [Fact]
    public void MapIsAConnectedGraphFromStartToBoss()
    {
        MapSnapshot map = Load("shop_act1.json").Map!;
        var byCoord = map.Points.ToDictionary(p => new MapCoordinate(p.Col, p.Row));
        Assert.Contains(map.Boss!.Value, byCoord.Keys);

        // Every edge must land on a known node, and the boss must be reachable from the start (row 0).
        Assert.All(map.Points.SelectMany(p => p.Children), child => Assert.Contains(child, byCoord.Keys));
        var seen = new HashSet<MapCoordinate>();
        var queue = new Queue<MapCoordinate>(map.Points.Where(p => p.Row == 0).Select(p => new MapCoordinate(p.Col, p.Row)));
        while (queue.Count > 0)
        {
            MapCoordinate at = queue.Dequeue();
            if (!seen.Add(at)) continue;
            foreach (MapCoordinate child in byCoord[at].Children) queue.Enqueue(child);
        }
        Assert.Contains(map.Boss.Value, seen);
    }

    [Fact]
    public void FreshActHasNoCurrentNode()
    {
        MapSnapshot map = Load("map_act2_start.json").Map!;
        Assert.Null(map.Current);
        Assert.Empty(map.Visited);
    }
}
