using SpireMonteCarlo.Advisor;
using SpireMonteCarlo.Contracts;
using Xunit;

namespace SpireMonteCarlo.Tests;

public class DecisionLogTests
{
    private static RunSnapshot Snap(string decision, int floor, string deck, string offer = "", int hp = 50, string relics = "", MapCoordinate? at = null) => new()
    {
        Decision = decision,
        Run = new RunInfo { Act = 1, TotalFloor = floor, CurrentHp = hp, MaxHp = 80, Seed = "S" },
        Deck = deck.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => new CardSnapshot { Id = id.TrimEnd('+'), Upgraded = id.EndsWith('+') }).ToList(),
        Offer = new Offer { Cards = offer.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => new CardSnapshot { Id = id }).ToList() },
        Relics = relics.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
        Map = at == null ? null : new MapSnapshot { Current = at },
    };

    private static string? Chosen(params RunSnapshot[] snaps) =>
        DecisionLog.Chosen(snaps.Select((s, i) => ($"{i}.json", s, (AdviceResult?)null)).ToList(), 0);

    [Fact]
    public void ACardRewardIsTheOfferedCardThatJoinedTheDeckOrSkip()
    {
        Assert.Equal("ANGER", Chosen(Snap(DecisionType.CardReward, 3, "STRIKE_IRONCLAD,BASH", "ANGER,TAUNT"), Snap(DecisionType.Map, 3, "STRIKE_IRONCLAD,BASH,ANGER")));
        Assert.Equal("Skip", Chosen(Snap(DecisionType.CardReward, 3, "STRIKE_IRONCLAD,BASH", "ANGER,TAUNT"), Snap(DecisionType.Map, 3, "STRIKE_IRONCLAD,BASH")));
    }

    [Fact]
    public void AMapChoiceIsTheNodeTheNextSnapshotStandsOn()
    {
        Assert.Equal("column 4, row 6", Chosen(Snap(DecisionType.Map, 5, "BASH"), Snap(DecisionType.CardReward, 6, "BASH", at: new MapCoordinate(4, 6))));
    }

    [Fact]
    public void RestSitesAndUpgradesAreReadFromHpAndUpgradedCards()
    {
        Assert.Equal("Rest", Chosen(Snap(DecisionType.RestSite, 7, "BASH", hp: 30), Snap(DecisionType.Map, 7, "BASH", hp: 54)));
        Assert.Equal("Upgrade", Chosen(Snap(DecisionType.RestSite, 7, "BASH"), Snap(DecisionType.CardUpgrade, 7, "BASH")));
        Assert.Equal("Upgrade BASH", Chosen(Snap(DecisionType.CardUpgrade, 7, "BASH,STRIKE_IRONCLAD"), Snap(DecisionType.Map, 7, "BASH+,STRIKE_IRONCLAD")));
    }

    [Fact]
    public void AShopVisitIsWhatItBoughtAndRemovedBeforeTheNextRoom()
    {
        RunSnapshot shop = Snap(DecisionType.Shop, 9, "STRIKE_IRONCLAD,DEFEND_IRONCLAD");
        RunSnapshot again = Snap(DecisionType.Shop, 9, "STRIKE_IRONCLAD,DEFEND_IRONCLAD,BATTLE_TRANCE");
        RunSnapshot after = Snap(DecisionType.Map, 9, "STRIKE_IRONCLAD,BATTLE_TRANCE", relics: "GHOST_SEED");
        string? chosen = Chosen(shop, again, after);
        Assert.Equal("card BATTLE_TRANCE + relic GHOST_SEED + remove DEFEND_IRONCLAD", chosen);
        Assert.Equal(chosen, DecisionLog.ShopItems("relic GHOST_SEED (177g) + card BATTLE_TRANCE (74g) + remove DEFEND_IRONCLAD (100g)  [351g]"));
        Assert.Equal("Buy nothing", Chosen(shop, Snap(DecisionType.Map, 9, "STRIKE_IRONCLAD,DEFEND_IRONCLAD")));
    }
}
