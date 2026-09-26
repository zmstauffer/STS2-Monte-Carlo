using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

public class RarityOddsTests
{
    [Fact]
    public void RareChanceGrowsUntilARareIsRolledThenResets()
    {
        var odds = new RarityOdds(ascension: 0);
        Assert.Equal(-0.05f, odds.Offset);
        var rng = new SimRng(1);
        // Roll until a rare shows up: the offset climbs by 0.01 per card until then.
        float last = odds.Offset;
        for (int i = 0; i < 2000; i++)
        {
            CardRarity r = odds.Roll(RewardKind.Normal, rng);
            if (r == CardRarity.Rare) { Assert.Equal(-0.05f, odds.Offset); return; }
            Assert.True(odds.Offset >= last);
            last = odds.Offset;
        }
        Assert.Fail("never rolled a rare");
    }

    [Fact]
    public void ScarcityAscensionMakesRaresRarer()
    {
        int RareCount(int ascension)
        {
            var rng = new SimRng(5);
            int rares = 0;
            for (int i = 0; i < 20000; i++)
                if (new RarityOdds(ascension, offset: 0).Roll(RewardKind.Normal, rng) == CardRarity.Rare) rares++;
            return rares;
        }
        Assert.True(RareCount(7) < RareCount(0));
    }

    [Fact]
    public void ElitesOfferMoreRaresThanNormalFights()
    {
        int Rares(RewardKind kind)
        {
            var rng = new SimRng(9);
            return Enumerable.Range(0, 20000).Count(_ => new RarityOdds(0, 0).Roll(kind, rng) == CardRarity.Rare);
        }
        Assert.True(Rares(RewardKind.Elite) > Rares(RewardKind.Normal));
    }
}

public class PickPolicyTests
{
    [Fact]
    public void SkipEloRisesWithDeckSize()
    {
        Assert.True(PickPolicy.SkipElo(25) > PickPolicy.SkipElo(15));
        Assert.True(PickPolicy.SkipElo(15) > PickPolicy.SkipElo(10));
    }

    [Fact]
    public void SkipBecomesLikelierAsTheDeckFillsAndAsOffersGetWeaker()
    {
        var cards = new Dictionary<string, CodexCard>();
        var metrics = new Dictionary<string, CodexMetricRow>
        {
            ["GREAT"] = new() { Id = "GREAT", Elo = 1850 },
            ["MEH"] = new() { Id = "MEH", Elo = 1300 },
        };
        var pool = new RewardPool(cards, metrics, "ironclad");

        double early = PickPolicy.SkipProbability(new[] { "GREAT", "MEH", "MEH" }, pool, deckSize: 10);
        double late = PickPolicy.SkipProbability(new[] { "GREAT", "MEH", "MEH" }, pool, deckSize: 25);
        double weakOffer = PickPolicy.SkipProbability(new[] { "MEH", "MEH", "MEH" }, pool, deckSize: 10);

        Assert.True(late > early);
        Assert.True(weakOffer > early);
    }
}

/// <summary>Runs against the real Codex cache; does nothing if it hasn't been built.</summary>
public class AdviceEngineTests
{
    private static SimData? Data()
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadMonsterAi() == null ? null : new SimData(cache);
    }

    private static RunSnapshot Fixture(string name) =>
        SnapshotSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void SkipAgainstItselfIsExactlyZeroAndResultsAreDeterministic()
    {
        SimData? data = Data();
        if (data == null) return;

        RunSnapshot snapshot = Fixture("shop_act1.json");
        snapshot.Decision = DecisionType.CardReward;
        snapshot.Run.Character = "ironclad";
        snapshot.Deck = new List<CardSnapshot>
        {
            new() { Id = "STRIKE_IRONCLAD" }, new() { Id = "STRIKE_IRONCLAD" }, new() { Id = "DEFEND_IRONCLAD" },
            new() { Id = "DEFEND_IRONCLAD" }, new() { Id = "BASH" },
        };
        snapshot.Offer.Cards = new List<CardSnapshot> { new() { Id = "POMMEL_STRIKE" } };
        snapshot.Relics = new List<string> { "BURNING_BLOOD" };

        AdviceReport a = AdviceEngine.EvaluateCardReward(data, snapshot, rollouts: 200, seed: 7);
        AdviceReport b = AdviceEngine.EvaluateCardReward(data, snapshot, rollouts: 200, seed: 7);

        OptionReport skip = a.Options.Single(o => o.CardId == null);
        Assert.Equal(0, skip.DeltaValue);
        Assert.Equal(a.Options.Select(o => o.Value), b.Options.Select(o => o.Value));
        Assert.All(a.Options, o => Assert.InRange(o.SurvivalRate, 0, 1));
    }
}
