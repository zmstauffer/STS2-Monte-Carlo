using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>Every supported decision type produces sensible options on the captured snapshots. Skipped when the local Codex cache isn't built.</summary>
public class DecisionAdvisorTests
{
    private static SimData? Data()
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadMonsterAi() == null ? null : new SimData(cache);
    }

    private static RunSnapshot Fixture(string name) =>
        SnapshotSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    /// <summary>A fixture turned into an Ironclad with the starter deck, so every card has a hand-checked recipe.</summary>
    private static RunSnapshot Ironclad(string fixture, string decision)
    {
        RunSnapshot s = Fixture(fixture);
        s.Decision = decision;
        s.Run.Character = "ironclad";
        s.Run.CurrentHp = 60; s.Run.MaxHp = 80;
        s.Deck = Enumerable.Repeat("STRIKE_IRONCLAD", 5).Concat(Enumerable.Repeat("DEFEND_IRONCLAD", 4)).Append("BASH")
            .Select(id => new CardSnapshot { Id = id }).ToList();
        s.Relics = new List<string> { "BURNING_BLOOD" };
        s.Potions = new List<PotionSnapshot>();
        return s;
    }

    [Fact]
    public void OnlyTheDecisionTypesWithEvaluatorsAreSupported()
    {
        Assert.True(DecisionAdvisor.Supports(new RunSnapshot { Decision = DecisionType.RestSite }));
        Assert.True(DecisionAdvisor.Supports(new RunSnapshot { Decision = DecisionType.Shop }));
        Assert.False(DecisionAdvisor.Supports(new RunSnapshot { Decision = DecisionType.Combat }));
        Assert.False(DecisionAdvisor.Supports(new RunSnapshot { Decision = DecisionType.Event }));
        Assert.True(DecisionAdvisor.Supports(new RunSnapshot { Decision = DecisionType.Event, EventOptions = { new EventOptionSnapshot { Relic = "VAJRA" } } }));
    }

    [Fact]
    public void TheUpgradeScreenAfterARestSiteReusesTheRestSitesFutures()
    {
        SimData? data = Data();
        if (data == null) return;
        AdviceReport rest = DecisionAdvisor.Evaluate(data, Ironclad("rest_site_act1.json", DecisionType.RestSite), 60, 7);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        AdviceReport upgrade = DecisionAdvisor.Evaluate(data, Ironclad("rest_site_act1.json", DecisionType.CardUpgrade), 60, 7);
        sw.Stop();
        foreach (OptionReport o in upgrade.Options.Where(o => o.Label.StartsWith("Upgrade ") && o.Label != "Upgrade nothing"))
        {
            OptionReport same = rest.Options.Single(r => r.Label == o.Label);
            Assert.Equal(same.SurvivalRate, o.SurvivalRate);
            Assert.Equal(same.MeanFutureIfSurvived, o.MeanFutureIfSurvived);
        }
        Assert.Contains(upgrade.Options, o => o.Label == "Upgrade nothing");
    }

    [Fact]
    public void ARestSiteComparesRestingAgainstUpgradingEachDistinctCard()
    {
        SimData? data = Data();
        if (data == null) return;
        AdviceReport report = DecisionAdvisor.Evaluate(data, Ironclad("rest_site_act1.json", DecisionType.RestSite), 60, 1);
        Assert.StartsWith("Rest", report.BaselineLabel);
        Assert.Contains(report.Options, o => o.IsBaseline);
        Assert.Contains(report.Options, o => o.Label == "Upgrade BASH");
        Assert.Contains(report.Options, o => o.Label == "Upgrade STRIKE_IRONCLAD");
        Assert.Equal(1, report.Options.Count(o => o.Label == "Upgrade STRIKE_IRONCLAD"));   // five Strikes, one option
        Assert.Equal(4, report.Options.Count);   // Rest, plus one option for each of Strike, Defend, Bash
    }

    [Fact]
    public void AMapDecisionHasOneOptionPerNodeYouCanReach()
    {
        SimData? data = Data();
        if (data == null) return;
        RunSnapshot snapshot = Ironclad("map_act2_start.json", DecisionType.Map);
        var rollout = new ActRollout(data, snapshot);
        AdviceReport report = DecisionAdvisor.Evaluate(data, snapshot, 60, 1);
        Assert.Equal(rollout.NextNodes.Count, report.Options.Count);
        Assert.All(report.Options, o => Assert.Contains("column", o.Label));
    }

    [Fact]
    public void AShopComparesBuyingNothingWithEachAffordableItemAndBundles()
    {
        SimData? data = Data();
        if (data == null) return;
        RunSnapshot snapshot = Ironclad("shop_act1.json", DecisionType.Shop);
        snapshot.Run.Gold = 400;
        snapshot.Offer.CardRemovalPrice = 75;
        AdviceReport report = DecisionAdvisor.Evaluate(data, snapshot, 80, 1);
        Assert.Equal("Buy nothing", report.BaselineLabel);
        Assert.Contains(report.Options, o => o.Label.StartsWith("remove STRIKE_IRONCLAD"));
        Assert.Contains(report.Options, o => o.Label.Contains(" + "));   // at least one bundle
        Assert.All(report.Options, o => Assert.InRange(o.SurvivalRate, 0, 1));
    }

    [Fact]
    public void AShopDoesNotOfferWhatYouCannotAfford()
    {
        SimData? data = Data();
        if (data == null) return;
        RunSnapshot snapshot = Ironclad("shop_act1.json", DecisionType.Shop);
        snapshot.Run.Gold = 10;
        AdviceReport report = DecisionAdvisor.Evaluate(data, snapshot, 40, 1);
        Assert.Single(report.Options);
    }

    [Fact]
    public void AnAncientEventComparesTheRelicsItOffers()
    {
        SimData? data = Data();
        if (data == null) return;
        RunSnapshot snapshot = Ironclad("event_ancient_act2.json", DecisionType.Event);
        snapshot.EventOptions = new List<EventOptionSnapshot>
        {
            new() { TextKey = "A", Relic = "VAJRA" },
            new() { TextKey = "B", Relic = "ANCHOR" },
            new() { TextKey = "C", Relic = "SOME_UNMODELLED_RELIC" },
            new() { TextKey = "D", Relic = "LANTERN", IsLocked = true },
        };
        AdviceReport report = DecisionAdvisor.Evaluate(data, snapshot, 60, 1);
        Assert.Equal(3, report.Options.Count);
        Assert.DoesNotContain(report.Options, o => o.Label.Contains("LANTERN"));
        Assert.Contains(report.Notes, n => n.Contains("SOME_UNMODELLED_RELIC"));
    }

    [Fact]
    public void ARelicGivenByADecisionTakesEffect()
    {
        SimData? data = Data();
        if (data == null) return;
        RunSnapshot snapshot = Ironclad("event_ancient_act2.json", DecisionType.Event);
        var rollout = new ActRollout(data, snapshot);
        List<CardDef> deck = snapshot.Deck.Select(c => data.Cards.Get(c.Id, false)).ToList();
        RolloutStart with = rollout.InitialStart(deck);
        with.AcquireOnStart.Add("MANGO");   // +14 max HP, applied on pickup
        RolloutResult a = rollout.Run(rollout.InitialStart(deck), 5);
        RolloutResult b = rollout.Run(with, 5);
        Assert.True(b.MaxHp > a.MaxHp);
    }
}
