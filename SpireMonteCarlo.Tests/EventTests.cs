using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>Each event option's numbers and conditions come from the decompiled event classes (v0.111). Skipped when the local Codex cache isn't built.</summary>
public class EventTests
{
    private static readonly Lazy<SimData?> Shared = new(() =>
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadCardDefs() == null ? null : new SimData(cache);
    });

    private static SimData? Data => Shared.Value;

    private static EventState State(double hp = 60, double maxHp = 80, int gold = 150, ulong seed = 1, int floor = 8)
    {
        SimData data = Data!;
        return new EventState
        {
            Data = data, Pool = data.PoolFor("ironclad"), RelicPool = data.RelicPoolFor("ironclad"), Rng = new SimRng(seed),
            Deck = data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH").ToList(),
            Hp = hp, MaxHp = maxHp, Gold = gold, Floor = floor, Ascension = 10,
        };
    }

    private static EventOptionDef Option(string eventId, string key, EventState s) => EventLibrary.Find(eventId)!.Options(s).Single(o => o.Key == key);

    [Fact]
    public void EveryEventOfTheActOnePoolsIsModelledExceptTheOnesWeKnowWeSkip()
    {
        var skipped = new HashSet<string> { "ENDLESS_CONVEYOR" };
        foreach (string variant in new[] { "OVERGROWTH", "UNDERDOCKS" })
        {
            IReadOnlyList<string> pool = EventLibrary.Pool(variant);
            foreach (string id in pool.Take(pool.Count - 18))   // the events of the act itself, without the 18 shared ones
                Assert.True(EventLibrary.Find(id) != null || skipped.Contains(id), $"{id} has no definition");
        }
    }

    [Fact]
    public void ByrdonisNestEatGivesSevenMaxHp()
    {
        if (Data == null) return;
        EventState s = State(hp: 60, maxHp: 80);
        Option("BYRDONIS_NEST", "EAT", s).Apply(s);
        Assert.Equal(87, s.MaxHp);
        Assert.Equal(67, s.Hp);
    }

    [Fact]
    public void DenseVegetationTrudgeOnCostsEightHpAndPaysBetween61And99Gold()
    {
        if (Data == null) return;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            EventState s = State(hp: 60, gold: 0, seed: seed);
            Option("DENSE_VEGETATION", "TRUDGE_ON", s).Apply(s);
            Assert.Equal(52, s.Hp);
            Assert.InRange(s.Gold, 61, 99);
        }
    }

    [Fact]
    public void DenseVegetationRestHealsAThirdThenForcesACombat()
    {
        if (Data == null) return;
        EventState s = State(hp: 20, maxHp: 80);
        Option("DENSE_VEGETATION", "REST", s).Apply(s);
        Assert.Equal(44, s.Hp);
        Assert.Equal("DENSE_VEGETATION_EVENT_ENCOUNTER", Assert.Single(s.PendingFights).Encounter);
        Assert.True(Data.Encounters.Contains("DENSE_VEGETATION_EVENT_ENCOUNTER"));
    }

    [Fact]
    public void SunkenTreasurysBigChestPaysAbout333GoldForACurse()
    {
        if (Data == null) return;
        EventState s = State(gold: 0);
        Option("SUNKEN_TREASURY", "SECOND_CHEST", s).Apply(s);
        Assert.InRange(s.Gold, 303, 363);
        Assert.Contains(s.Deck, c => c.Id == "GREED");
    }

    [Fact]
    public void UnrestSiteOnlyAppearsAtSeventyPercentHpOrLessAndKillingItCostsEightMaxHp()
    {
        if (Data == null) return;
        EventDef def = EventLibrary.Find("UNREST_SITE")!;
        Assert.False(def.Allowed(State(hp: 60, maxHp: 80)));   // 75%
        Assert.True(def.Allowed(State(hp: 56, maxHp: 80)));    // 70%
        EventState s = State(hp: 40, maxHp: 80);
        Option("UNREST_SITE", "KILL", s).Apply(s);
        Assert.Equal(72, s.MaxHp);
        Assert.Single(s.PendingRelics);
    }

    [Fact]
    public void WellspringBatheRemovesACardAndAddsGuilty()
    {
        if (Data == null) return;
        EventState s = State();
        int strikes = s.Deck.Count(c => c.Id == "STRIKE_IRONCLAD");
        Option("WELLSPRING", "BATHE", s).Apply(s);
        Assert.Equal(strikes - 1, s.Deck.Count(c => c.Id == "STRIKE_IRONCLAD"));
        Assert.Contains(s.Deck, c => c.Id == "GUILTY");
    }

    [Fact]
    public void MorphicGroveNeedsGoldAndTwoTransformableCardsAndGroupTakesAllYourGold()
    {
        if (Data == null) return;
        EventDef def = EventLibrary.Find("MORPHIC_GROVE")!;
        Assert.False(def.Allowed(State(gold: 99)));
        Assert.True(def.Allowed(State(gold: 100)));
        EventState s = State(gold: 180);
        Option("MORPHIC_GROVE", "GROUP", s).Apply(s);
        Assert.Equal(0, s.Gold);
        Assert.Equal(10, s.Deck.Count);   // two Strikes became two other cards
        Assert.Equal(3, s.Deck.Count(c => c.Id == "STRIKE_IRONCLAD"));
    }

    [Fact]
    public void PunchOffOnlyAppearsFromFloorSixAndFightingItQueuesTheCombatWithRewards()
    {
        if (Data == null) return;
        EventDef def = EventLibrary.Find("PUNCH_OFF")!;
        Assert.False(def.Allowed(State(floor: 5)));
        Assert.True(def.Allowed(State(floor: 6)));
        EventState s = State();
        Option("PUNCH_OFF", "I_CAN_TAKE_THEM", s).Apply(s);
        PendingFight fight = Assert.Single(s.PendingFights);
        Assert.True(fight.RelicReward && fight.PotionReward);
    }

    [Fact]
    public void TabletOfTruthDecipherCostsThreeMaxHpAndUpgradesACard()
    {
        if (Data == null) return;
        EventState s = State(maxHp: 80);
        Option("TABLET_OF_TRUTH", "DECIPHER_1", s).Apply(s);
        Assert.Equal(77, s.MaxHp);
        Assert.Equal(1, s.Deck.Count(c => c.Upgraded));
    }

    [Fact]
    public void TheEventAdvisorComparesTheOptionsTheGameShows()
    {
        if (Data == null) return;
        RunSnapshot snapshot = SnapshotSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "shop_act1.json")));
        snapshot.Decision = DecisionType.Event;
        snapshot.EventId = "SUNKEN_STATUE";
        snapshot.Run.Character = "ironclad";
        snapshot.Run.CurrentHp = 60; snapshot.Run.MaxHp = 80;
        snapshot.Deck = Enumerable.Repeat("STRIKE_IRONCLAD", 5).Concat(Enumerable.Repeat("DEFEND_IRONCLAD", 4)).Append("BASH").Select(id => new CardSnapshot { Id = id }).ToList();
        snapshot.Relics = new List<string> { "BURNING_BLOOD" };
        snapshot.Potions = new List<PotionSnapshot>();
        snapshot.EventOptions = new List<EventOptionSnapshot>
        {
            new() { TextKey = "SUNKEN_STATUE.pages.INITIAL.options.GRAB_SWORD", Title = "Grab the sword" },
            new() { TextKey = "SUNKEN_STATUE.pages.INITIAL.options.DIVE_INTO_WATER", Title = "Dive into the water" },
        };
        Assert.True(DecisionAdvisor.Supports(snapshot));
        AdviceReport report = DecisionAdvisor.Evaluate(Data, snapshot, 60, 1);
        Assert.Equal(2, report.Options.Count);
        Assert.Contains(report.Options, o => o.Label == "Dive into the water");
        Assert.Contains(report.Notes, n => n.Contains("SWORD_OF_STONE"));
    }

    [Fact]
    public void AnEventTheLibraryHasNeverHeardOfIsNotSupported()
    {
        Assert.False(DecisionAdvisor.Supports(new RunSnapshot { Decision = DecisionType.Event, EventId = "SOME_NEW_EVENT", EventOptions = { new EventOptionSnapshot { TextKey = "X.Y" } } }));
    }

    [Fact]
    public void RolloutsPlayTheEventsInTheActsQueue()
    {
        if (Data == null) return;
        RunSnapshot snapshot = SnapshotSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "card_reward_act1.json")));
        snapshot.Run.Character = "ironclad"; snapshot.Run.CurrentHp = snapshot.Run.MaxHp = 80; snapshot.Run.Gold = 100; snapshot.Run.Ascension = 10;
        snapshot.Relics = new List<string> { "BURNING_BLOOD" };
        snapshot.Potions = new List<PotionSnapshot>();
        snapshot.Map!.Current = null; snapshot.Map.Visited = new();
        snapshot.Plan = new ActPlan { ActId = "OVERGROWTH", Events = new List<string>(Enumerable.Repeat("BYRDONIS_NEST", 8)) };
        var rollout = new ActRollout(Data, snapshot);
        List<CardDef> deck = Data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH").ToList();
        // Every event is the Byrdonis nest, whose default is +7 max HP: rollouts that visit events end with more max HP than the starting 80.
        int withMoreMaxHp = Enumerable.Range(0, 50).Count(i => rollout.Run(deck, (ulong)i).MaxHp > 80);
        Assert.True(withMoreMaxHp > 25, $"only {withMoreMaxHp} of 50 rollouts gained max HP");
    }
}
