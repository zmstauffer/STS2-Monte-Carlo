using SpireMonteCarlo.Contracts;
using Xunit;

namespace SpireMonteCarlo.Tests;

public class SnapshotSerializerTests
{
    [Fact]
    public void RoundTripPreservesEverything()
    {
        var original = new RunSnapshot
        {
            GameVersion = "v0.111.0",
            ModVersion = "0.1.0",
            CapturedAt = new DateTimeOffset(2026, 9, 25, 17, 30, 0, TimeSpan.FromHours(-7)),
            Decision = DecisionType.CardReward,
            Run = new RunInfo { Character = "regent", Ascension = 10, Seed = "ABC123", Act = 1, TotalFloor = 3, CurrentHp = 60, MaxHp = 75, Gold = 99 },
            Deck = { new CardSnapshot { Id = "BODY_SLAM", Upgraded = true }, new CardSnapshot { Id = "STRIKE_REGENT" } },
            Relics = { "BURNING_BLOOD" },
            Potions = { new PotionSnapshot { Id = "FIRE_POTION" } },
            Offer = { Cards = { new CardSnapshot { Id = "SHRUG_IT_OFF" } } },
            Map = new MapSnapshot
            {
                Columns = 7, Rows = 15,
                Points =
                {
                    new MapPointSnapshot { Col = 3, Row = 0, Type = "Monster", Children = { new MapCoordinate(2, 1), new MapCoordinate(3, 1) } },
                },
                Current = new MapCoordinate(3, 0),
                Visited = { new MapCoordinate(3, 0) },
                Boss = new MapCoordinate(3, 15),
            },
        };

        string json = SnapshotSerializer.Serialize(original);
        RunSnapshot copy = SnapshotSerializer.Deserialize(json);

        Assert.Equal(json, SnapshotSerializer.Serialize(copy));
        Assert.Equal(new MapCoordinate(3, 15), copy.Map!.Boss);
        Assert.Equal(new MapCoordinate(3, 1), copy.Map.Points[0].Children[1]);
        Assert.Equal("BODY_SLAM", copy.Deck[0].Id);
    }

    [Fact]
    public void JsonIsSnakeCase()
    {
        string json = SnapshotSerializer.Serialize(new RunSnapshot { Run = new RunInfo { CurrentHp = 5 } });
        Assert.Contains("\"schema_version\"", json);
        Assert.Contains("\"current_hp\": 5", json);
    }
}
