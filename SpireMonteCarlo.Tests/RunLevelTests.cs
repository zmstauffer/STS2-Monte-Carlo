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

    private static RunSnapshot IroncladReward(int act)
    {
        RunSnapshot snapshot = Fixture("shop_act1.json");
        snapshot.Decision = DecisionType.CardReward;
        snapshot.Run.Character = "ironclad";
        snapshot.Run.Act = act;
        snapshot.Run.CurrentHp = snapshot.Run.MaxHp = 80;
        snapshot.Deck = Enumerable.Repeat("STRIKE_IRONCLAD", 5).Concat(Enumerable.Repeat("DEFEND_IRONCLAD", 4)).Append("BASH")
            .Select(id => new CardSnapshot { Id = id }).ToList();
        snapshot.Offer.Cards = new List<CardSnapshot> { new() { Id = "POMMEL_STRIKE" } };
        snapshot.Relics = new List<string> { "BURNING_BLOOD" };
        return snapshot;
    }

    [Fact]
    public void TheNextActProbeIsReportedBeforeAct3AndNotIn3()
    {
        SimData? data = Data();
        if (data == null) return;
        AdviceReport act1 = AdviceEngine.EvaluateCardReward(data, IroncladReward(1), rollouts: 300, seed: 3);
        Assert.Contains(act1.Options, o => !double.IsNaN(o.ProbeWinRate));
        Assert.All(act1.Options.Where(o => !double.IsNaN(o.ProbeWinRate)), o => Assert.InRange(o.ProbeWinRate, 0, 1));
        AdviceReport act3 = AdviceEngine.EvaluateCardReward(data, IroncladReward(3), rollouts: 100, seed: 3);
        Assert.All(act3.Options, o => Assert.True(double.IsNaN(o.ProbeWinRate)));
    }

    [Fact]
    public void ARolloutValuesAStrongerDeckTestAboveAWeakerOne()
    {
        var log = new List<FightLogEntry>();
        RolloutResult Result(double loss) => new(true, 40, 80, 8, 8, null, 0, new List<string>(), log, ProbeFights: 4, ProbeWins: 4, ProbeLoss: loss);
        Assert.True(AdviceEngine.ValueOf(Result(1.0)) > AdviceEngine.ValueOf(Result(2.0)));
        Assert.Equal(0.75, Result(1.0).DeckStrength, 6);
        Assert.Equal(0.0, AdviceEngine.ValueOf(new RolloutResult(false, 0, 80, 3, 4, "X", 0, new List<string>(), log, 0, 0)));
    }

    [Fact]
    public void TheShareOfTheRunAfterThisActGrowsThroughTheActAndIsZeroInActThree()
    {
        Assert.Equal(34.0 / 50, AdviceEngine.ShareAfterThisAct(new SpireMonteCarlo.Contracts.RunInfo { Act = 1, TotalFloor = 1 }), 6);
        Assert.Equal(1.0, AdviceEngine.ShareAfterThisAct(new SpireMonteCarlo.Contracts.RunInfo { Act = 1, TotalFloor = 17 }), 6);
        Assert.Equal(17.0 / 33, AdviceEngine.ShareAfterThisAct(new SpireMonteCarlo.Contracts.RunInfo { Act = 2, TotalFloor = 18 }), 6);
        Assert.Equal(0.0, AdviceEngine.ShareAfterThisAct(new SpireMonteCarlo.Contracts.RunInfo { Act = 3, TotalFloor = 40 }), 6);
    }

    [Fact]
    public void ARolloutIsWorthMoreWithAStrongerDeckAndMoreHpAndNothingIfItDied()
    {
        var log = new List<FightLogEntry>();
        RolloutResult Result(double loss, double hp) => new(true, (int)(80 * hp), 80, 8, 8, null, 0, new List<string>(), log, ProbeFights: 4, ProbeWins: 4,
            End: new RolloutStart { Deck = new List<CardDef>(), Hp = 80 * hp, MaxHp = 80 }, ProbeLoss: loss);
        foreach (int act in new[] { 1, 2 })
        {
            Assert.True(AdviceEngine.ValueOf(Result(1.0, 0.8), act) > AdviceEngine.ValueOf(Result(2.0, 0.8), act));
            Assert.True(AdviceEngine.ValueOf(Result(2.0, 0.9), act) > AdviceEngine.ValueOf(Result(2.0, 0.5), act));
            Assert.InRange(AdviceEngine.ValueOf(Result(2.0, 0.8), act), 0.0, 1.0);
        }
    }

    [Fact]
    public void AnUpgradesLaterWorthGrowsWithTheHpItSavesAndThroughTheActAndIsZeroInActThree()
    {
        var early = new SpireMonteCarlo.Contracts.RunSnapshot { Run = new SpireMonteCarlo.Contracts.RunInfo { Act = 1, TotalFloor = 2 } };
        var late = new SpireMonteCarlo.Contracts.RunSnapshot { Run = new SpireMonteCarlo.Contracts.RunInfo { Act = 1, TotalFloor = 16 } };
        var last = new SpireMonteCarlo.Contracts.RunSnapshot { Run = new SpireMonteCarlo.Contracts.RunInfo { Act = 3, TotalFloor = 40 } };
        Assert.True(AdviceEngine.UpgradeLaterPoints(late, 2, 80) > AdviceEngine.UpgradeLaterPoints(late, 1, 80));
        Assert.True(AdviceEngine.UpgradeLaterPoints(late, 2, 80) > AdviceEngine.UpgradeLaterPoints(early, 2, 80));
        Assert.Equal(0, AdviceEngine.UpgradeLaterPoints(last, 2, 80));
        Assert.Equal(0, AdviceEngine.UpgradeLaterPoints(late, double.NaN, 80));
    }
}
