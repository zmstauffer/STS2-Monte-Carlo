using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>The Act 2, Act 3 and shared events (Events.Later.cs), from the decompiled classes (v0.111). Skipped when the local Codex cache isn't built.</summary>
public class LaterEventTests
{
    private static readonly Lazy<SimData?> Shared = new(() =>
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadMonsterAi() == null ? null : new SimData(cache);
    });

    private static SimData? Data => Shared.Value;

    private static EventState State(int act = 2, double hp = 60, double maxHp = 80, int gold = 300, ulong seed = 1, string deck = "STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH",
        string[]? relics = null, string[]? potions = null)
    {
        SimData data = Data!;
        return new EventState
        {
            Data = data, Pool = data.PoolFor("ironclad"), RelicPool = data.RelicPoolFor("ironclad"), Rng = new SimRng(seed),
            Deck = data.ParseDeck(deck).ToList(), Act = act, Hp = hp, MaxHp = maxHp, Gold = gold, Floor = 20, Ascension = 10,
            Relics = (relics ?? new[] { "BURNING_BLOOD" }).ToList(), Potions = (potions ?? Array.Empty<string>()).ToList(),
        };
    }

    private static EventOptionDef Option(string eventId, string key, EventState s) => EventLibrary.Find(eventId)!.Options(s).Single(o => o.Key == key);

    [Fact]
    public void EveryEventOfEveryActsPoolIsModelledExceptEndlessConveyor()
    {
        foreach (string variant in new[] { "OVERGROWTH", "UNDERDOCKS", "HIVE", "GLORY" })
            foreach (string id in EventLibrary.Pool(variant))
                Assert.True(EventLibrary.Find(id) != null || id == "ENDLESS_CONVEYOR", $"{id} ({variant}) has no definition");
    }

    [Fact]
    public void EveryCardAnEventGivesIsKnownToTheSimulator()
    {
        if (Data == null) return;
        foreach (EventDef def in EventLibrary.All)
            for (ulong seed = 1; seed <= 6; seed++)
            {
                EventState probe = State(seed: seed, potions: new[] { "FIRE_POTION", "BLOCK_POTION" }, relics: new[] { "BURNING_BLOOD", "VAJRA", "ANCHOR", "LANTERN", "BAG_OF_MARBLES", "ORICHALCUM" });
                foreach (EventOptionDef option in def.Options(probe))
                {
                    EventState s = probe.Copy();
                    option.Apply(s);
                    Assert.DoesNotContain(s.Notes, n => n.StartsWith("Card "));
                }
            }
    }

    [Fact]
    public void TheEventFightsExistInTheEncounterData()
    {
        if (Data == null) return;
        Assert.True(Data.Encounters.Contains("MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER"));
    }

    [Fact]
    public void AmalgamatorFusesTwoStrikesIntoUltimateStrike()
    {
        if (Data == null) return;
        EventState s = State();
        Assert.True(EventLibrary.Find("AMALGAMATOR")!.Allowed(s));
        Option("AMALGAMATOR", "COMBINE_STRIKES", s).Apply(s);
        Assert.Equal(3, s.Deck.Count(c => c.Id == "STRIKE_IRONCLAD"));
        Assert.Single(s.Deck, c => c.Id == "ULTIMATE_STRIKE");
        Assert.False(EventLibrary.Find("AMALGAMATOR")!.Allowed(State(deck: "STRIKE_IRONCLAD,DEFEND_IRONCLAD*4,BASH")));
    }

    [Fact]
    public void ColossalFlowerDigsAllTheWayForPollinousCoreWhenHealthy()
    {
        if (Data == null) return;
        EventState healthy = State(hp: 70, gold: 0);
        Option("COLOSSAL_FLOWER", "REACH_DEEPER_1", healthy).Apply(healthy);
        Assert.Equal(70 - 5 - 6 - 7, healthy.Hp);
        Assert.Contains("POLLINOUS_CORE", healthy.PendingRelics);

        EventState low = State(hp: 28, gold: 0);
        Option("COLOSSAL_FLOWER", "REACH_DEEPER_1", low).Apply(low);
        Assert.Equal(23, low.Hp);
        Assert.Equal(75, low.Gold);

        Assert.False(EventLibrary.Find("COLOSSAL_FLOWER")!.Allowed(State(hp: 18)));
    }

    [Fact]
    public void ColorfulPhilosophersGivesCardsOfAnotherCharacter()
    {
        if (Data == null) return;
        int added = 0;
        for (ulong seed = 1; seed <= 10; seed++)
        {
            EventState s = State(seed: seed);
            Option("COLORFUL_PHILOSOPHERS", "SILENT", s).Apply(s);
            added += s.Deck.Count - 10;
        }
        Assert.InRange(added, 10, 30);   // three rewards each, some skipped
    }

    [Fact]
    public void RelicTraderSwapsOneOfYourRelicsForANewOne()
    {
        if (Data == null) return;
        var relics = new[] { "BURNING_BLOOD", "VAJRA", "ANCHOR", "LANTERN", "BAG_OF_MARBLES", "ORICHALCUM" };
        EventState s = State(relics: relics);
        Assert.True(EventLibrary.Find("RELIC_TRADER")!.Allowed(s));
        Option("RELIC_TRADER", "TOP", s).Apply(s);
        string removed = Assert.Single(s.RemovedRelics);
        Assert.NotEqual("BURNING_BLOOD", removed);   // the starter relic can't be traded
        Assert.Single(s.PendingRelics);
        Assert.False(EventLibrary.Find("RELIC_TRADER")!.Allowed(State(relics: relics.Take(5).ToArray())));   // five tradable relics needed
        Assert.False(EventLibrary.Find("RELIC_TRADER")!.Allowed(State(act: 1, relics: relics)));
    }

    [Fact]
    public void TheRolloutGivesUpTradedRelics()
    {
        if (Data == null) return;
        RunSnapshot snapshot = SnapshotSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "event_potion_courier.json")));
        snapshot.Relics = new List<string> { "BURNING_BLOOD", "VAJRA", "ANCHOR", "LANTERN", "BAG_OF_MARBLES", "ORICHALCUM" };
        var rollout = new ActRollout(Data, snapshot);
        RolloutStart start = rollout.InitialStart(snapshot.Deck.Select(c => Data.Cards.Get(c.Id, c.Upgraded)).ToList());
        start.EventEffect = new EventOptionDef("GIVE", x => x.RemoveRelic("VAJRA"));
        RolloutResult r = rollout.Run(start, 1);
        Assert.DoesNotContain("VAJRA", r.End!.Relics);
    }

    [Fact]
    public void TheFutureOfPotionsTradesTheChosenPotionForAnUpgradedCard()
    {
        if (Data == null) return;
        EventState s = State(potions: new[] { "FIRE_POTION", "BLOCK_POTION", "STRENGTH_POTION" });
        var keys = EventLibrary.Find("THE_FUTURE_OF_POTIONS")!.Options(s).Select(o => o.Key).ToList();
        Assert.Equal(new[] { "POTION_0", "POTION_1", "POTION_2" }, keys);
        Option("THE_FUTURE_OF_POTIONS", "POTION_1", s).Apply(s);
        Assert.Equal(new[] { "FIRE_POTION", "STRENGTH_POTION" }, s.Potions);
        Assert.All(s.Deck.Skip(10), c => Assert.True(c.Upgraded));
    }

    [Fact]
    public void ZenWeaverNeedsGoldAndRemovesCards()
    {
        if (Data == null) return;
        Assert.False(EventLibrary.Find("ZEN_WEAVER")!.Allowed(State(gold: 124)));
        EventState s = State(gold: 260);
        Option("ZEN_WEAVER", "ARACHNID_ACUPUNCTURE", s).Apply(s);
        Assert.Equal(10, s.Gold);
        Assert.Equal(8, s.Deck.Count);
        Assert.False(EventLibrary.Find("ZEN_WEAVER")!.Options(State(gold: 200)).Single(o => o.Key == "ARACHNID_ACUPUNCTURE").Enabled);
    }

    [Fact]
    public void ActGatedSharedEventsFollowTheGamesConditions()
    {
        if (Data == null) return;
        Assert.False(EventLibrary.Find("DOLL_ROOM")!.Allowed(State(act: 1)));
        Assert.True(EventLibrary.Find("DOLL_ROOM")!.Allowed(State(act: 2)));
        Assert.False(EventLibrary.Find("DOLL_ROOM")!.Allowed(State(act: 3)));
        Assert.False(EventLibrary.Find("POTION_COURIER")!.Allowed(State(act: 1)));
        Assert.False(EventLibrary.Find("TEA_MASTER")!.Allowed(State(act: 3)));
        Assert.False(EventLibrary.Find("WAR_HISTORIAN_REPY")!.Allowed(State()));
        Assert.False(EventLibrary.Find("STONE_OF_ALL_TIME")!.Allowed(State(act: 2)));   // needs a potion
        Assert.True(EventLibrary.Find("STONE_OF_ALL_TIME")!.Allowed(State(act: 2, potions: new[] { "FIRE_POTION" })));
    }

    [Fact]
    public void ReflectionsShatterDoublesTheDeckAndAddsBadLuck()
    {
        if (Data == null) return;
        EventState s = State(act: 3);
        Option("REFLECTIONS", "SHATTER", s).Apply(s);
        Assert.Equal(21, s.Deck.Count);
        Assert.Single(s.Deck, c => c.Id == "BAD_LUCK");
    }

    [Fact]
    public void BattlewornDummyPicksTheSettingTheDeckCanBeat()
    {
        if (Data == null) return;
        EventDef dummy = EventLibrary.Find("BATTLEWORN_DUMMY")!;
        Assert.Equal("SETTING_1", dummy.Default(State(act: 3)));   // a starter deck deals ~60-80 in three turns
        Assert.InRange(State(act: 3).ThreeTurnDamage(), 40, 110);
    }

    [Theory]
    [InlineData("event_colorful_philosophers.json", 3)]
    [InlineData("event_colossal_flower.json", 2)]
    [InlineData("event_grave_of_the_forgotten.json", 2)]
    [InlineData("event_hungry_for_mushrooms.json", 2)]
    [InlineData("event_potion_courier.json", 2)]
    [InlineData("event_trial.json", 2)]
    [InlineData("event_welcome_to_wongos.json", 3)]
    public void TheAdvisorComparesEveryOptionOfTheRealActTwoAndThreeEventSnapshots(string fixture, int options)
    {
        if (Data == null) return;
        RunSnapshot snapshot = SnapshotSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)));
        Assert.True(DecisionAdvisor.Supports(snapshot));
        AdviceReport report = DecisionAdvisor.Evaluate(Data, snapshot, 24, 1);
        Assert.Equal(options, report.Options.Count);
        Assert.DoesNotContain(report.Notes, n => n.Contains("isn't modelled, so it isn't evaluated"));
    }
}
