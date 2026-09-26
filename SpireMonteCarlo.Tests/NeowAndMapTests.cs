using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>The generated act maps and Neow's boons (rules read from the decompiled StandardActMap and relic classes, v0.111). Skipped when the local Codex cache isn't built.</summary>
public class NeowAndMapTests
{
    private static readonly Lazy<SimData?> Shared = new(() =>
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadCardDefs() == null ? null : new SimData(cache);
    });

    private static SimData? Data => Shared.Value;

    private static EventState State(ulong seed = 1)
    {
        SimData data = Data!;
        return new EventState
        {
            Data = data, Pool = data.PoolFor("ironclad"), RelicPool = data.RelicPoolFor("ironclad"), Rng = new SimRng(seed),
            Deck = data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH").ToList(),
            Hp = 70, MaxHp = 80, Gold = 99, Floor = 0, Ascension = 10,
        };
    }

    [Fact]
    public void GeneratedMapsHaveTheGamesFixedRowsAndEveryNodeLeadsToTheBoss()
    {
        for (ulong seed = 1; seed <= 30; seed++)
        {
            MapSnapshot map = MapGenerator.Generate(seed);
            var points = map.Points.ToDictionary(p => new MapCoordinate(p.Col, p.Row));
            Assert.All(map.Points.Where(p => p.Row == 1), p => Assert.Equal("Monster", p.Type));
            Assert.All(map.Points.Where(p => p.Row == 9), p => Assert.Equal("Treasure", p.Type));
            Assert.All(map.Points.Where(p => p.Row == 15), p => Assert.Equal("RestSite", p.Type));
            Assert.Equal("Boss", points[map.Boss!.Value].Type);

            // Walking forward from the start always reaches the boss.
            MapCoordinate at = points.Values.Single(p => p.Type == "Ancient").Let(p => new MapCoordinate(p.Col, p.Row));
            while (points[at].Type != "Boss")
            {
                Assert.NotEmpty(points[at].Children);
                at = points[at].Children[0];
            }
        }
    }

    [Fact]
    public void GeneratedMapsFollowThePlacementRules()
    {
        for (ulong seed = 1; seed <= 30; seed++)
        {
            MapSnapshot map = MapGenerator.Generate(seed);
            var points = map.Points.ToDictionary(p => new MapCoordinate(p.Col, p.Row));
            foreach (MapPointSnapshot p in map.Points.Where(p => p.Type is "Elite" or "Shop" or "Treasure" or "RestSite"))
                foreach (MapCoordinate child in p.Children)
                    Assert.NotEqual(p.Type, points[child].Type);   // these never sit back to back
            Assert.All(map.Points.Where(p => p.Row < 6 && p.Row > 0), p => Assert.NotEqual("Elite", p.Type));
            Assert.All(map.Points.Where(p => p.Row is 13 or 14), p => Assert.NotEqual("RestSite", p.Type));
        }
    }

    [Fact]
    public void NeowOffersTwoDifferentBoons()
    {
        for (ulong seed = 1; seed <= 50; seed++)
        {
            string[] offer = NeowBoons.Offer(new SimRng(seed));
            Assert.Equal(2, offer.Length);
            Assert.NotEqual(offer[0], offer[1]);
            Assert.All(offer, id => Assert.True(NeowBoons.Has(id)));
        }
    }

    [Fact]
    public void TheGoldAndMaxHpBoonsPayTheirDecompiledAmounts()
    {
        if (Data == null) return;
        EventState pearl = State();
        NeowBoons.Apply("GOLDEN_PEARL", pearl);
        Assert.Equal(99 + 150, pearl.Gold);
        EventState oyster = State();
        NeowBoons.Apply("NUTRITIOUS_OYSTER", oyster);
        Assert.Equal(91, oyster.MaxHp);
        Assert.Equal(81, oyster.Hp);
    }

    [Fact]
    public void TheDeckBoonsEditTheDeck()
    {
        if (Data == null) return;
        EventState scissors = State();
        NeowBoons.Apply("PRECISE_SCISSORS", scissors);
        Assert.Equal(9, scissors.Deck.Count);

        EventState talisman = State();
        NeowBoons.Apply("NEOWS_TALISMAN", talisman);
        Assert.Equal(1, talisman.Deck.Count(c => c.Id.StartsWith("STRIKE_") && c.Upgraded));
        Assert.Equal(1, talisman.Deck.Count(c => c.Id.StartsWith("DEFEND_") && c.Upgraded));

        EventState scroll = State();
        NeowBoons.Apply("ARCANE_SCROLL", scroll);
        Assert.Equal(11, scroll.Deck.Count);
        Assert.Equal(CardRarity.Rare, scroll.Pool.RarityOf(scroll.Deck[^1].Id));

        EventState boxes = State();
        NeowBoons.Apply("SCROLL_BOXES", boxes);
        Assert.Equal(13, boxes.Deck.Count);

        EventState leaf = State();
        NeowBoons.Apply("NEW_LEAF", leaf);
        Assert.Equal(10, leaf.Deck.Count);
        Assert.Equal(4, leaf.Deck.Count(c => c.Id.StartsWith("STRIKE_")));
    }

    [Fact]
    public void ThePhialHolsterAddsASlotAndPotions()
    {
        if (Data == null) return;
        EventState s = State();
        NeowBoons.Apply("PHIAL_HOLSTER", s);
        Assert.Equal(4, s.PotionSlots);
        Assert.Equal(2, s.Potions.Count);
    }

    [Fact]
    public void BoomingConchGivesElitesTwoExtraCardsAndOneEnergyOnTurnOne()
    {
        if (Data == null) return;
        SimData data = Data;
        var monster = new MonsterDef
        {
            Id = "DUMMY", HpMin = 500, HpMax = 500, HpMinTough = 500, HpMaxTough = 500,
            Moves = { ["HIT"] = new MoveDef { Id = "HIT", Intent = "Buff", Hits = 1 } },
            States = { ["HIT_MOVE"] = new StateDef { Id = "HIT_MOVE", Kind = StateKind.Move, MoveId = "HIT", Next = "HIT_MOVE" } },
            InitialState = "HIT_MOVE",
        };
        List<CardDef> deck = data.ParseDeck("STRIKE_IRONCLAD*20");
        Combat elite = new(deck, 80, 80, new[] { monster }, 10, 5, services: data.Services, relics: new[] { RelicKind.BoomingConch }, stakes: 1);
        Combat normal = new(deck, 80, 80, new[] { monster }, 10, 5, services: data.Services, relics: new[] { RelicKind.BoomingConch }, stakes: 0);
        Assert.Equal(7, elite.Hand.Count);
        Assert.Equal(4, elite.Energy);
        Assert.Equal(5, normal.Hand.Count);
        Assert.Equal(3, normal.Energy);
    }

    [Fact]
    public void EggRelicsUpgradeMatchingCardsAsTheyJoinTheDeck()
    {
        if (Data == null) return;
        EventState s = State();
        s.Relics.AddRange(new[] { "MOLTEN_EGG", "TOXIC_EGG" });
        s.AddCard("TWIN_STRIKE");     // an Attack
        s.AddCard("SHRUG_IT_OFF");    // a Skill
        s.AddCard("INFLAME");         // a Power, no Frozen Egg
        Assert.True(s.Deck.Single(c => c.Id == "TWIN_STRIKE").Upgraded);
        Assert.True(s.Deck.Single(c => c.Id == "SHRUG_IT_OFF").Upgraded);
        Assert.False(s.Deck.Single(c => c.Id == "INFLAME").Upgraded);
    }

    [Fact]
    public void MummifiedHandMakesACardFreeAfterEveryPower()
    {
        if (Data == null) return;
        SimData data = Data;
        var monster = new MonsterDef
        {
            Id = "DUMMY", HpMin = 500, HpMax = 500, HpMinTough = 500, HpMaxTough = 500,
            Moves = { ["HIT"] = new MoveDef { Id = "HIT", Intent = "Buff", Hits = 1 } },
            States = { ["HIT_MOVE"] = new StateDef { Id = "HIT_MOVE", Kind = StateKind.Move, MoveId = "HIT", Next = "HIT_MOVE" } },
            InitialState = "HIT_MOVE",
        };
        var c = new Combat(Array.Empty<CardDef>(), 80, 80, new[] { monster }, 0, 3, services: data.Services, relics: new[] { RelicKind.MummifiedHand });
        c.Hand.AddRange(data.ParseDeck("INFLAME,BASH").Select(x => x.Instantiate()));
        int energy = c.Energy;
        c.Play(c.Hand.FindIndex(x => x.Id == "INFLAME"), -1);
        CardDef bash = c.Hand.Single(x => x.Id == "BASH");
        Assert.Equal(0, c.EffectiveCost(bash));
        Assert.Equal(energy - 1, c.Energy);
    }

    [Fact]
    public void PillageStopsDrawingWhenHellraiserPlaysEveryStrikeItDraws()
    {
        if (Data == null) return;
        SimData data = Data;
        var monster = new MonsterDef
        {
            Id = "DUMMY", HpMin = 500, HpMax = 500, HpMinTough = 500, HpMaxTough = 500,
            Moves = { ["HIT"] = new MoveDef { Id = "HIT", Intent = "Buff", Hits = 1 } },
            States = { ["HIT_MOVE"] = new StateDef { Id = "HIT_MOVE", Kind = StateKind.Move, MoveId = "HIT", Next = "HIT_MOVE" } },
            InitialState = "HIT_MOVE",
        };
        // Every card left to draw is a Strike, and Hellraiser plays each one the moment it is drawn, so the hand never fills: this used to loop forever.
        var c = new Combat(data.ParseDeck("STRIKE_IRONCLAD*6"), 80, 80, new[] { monster }, 0, 3, services: data.Services);
        c.Hand.AddRange(data.ParseDeck("HELLRAISER+,PILLAGE").Select(x => x.Instantiate()));
        c.Play(c.Hand.FindIndex(x => x.Id == "HELLRAISER"), -1);
        c.Play(c.Hand.FindIndex(x => x.Id == "PILLAGE"), 0);
        Assert.Equal(CombatResult.Ongoing, c.Result);
    }

    [Fact]
    public void ARunStartingActOneTakesANeowBoonAndAdviceForTheChoiceItselfDoesNot()
    {
        if (Data == null) return;
        SimData data = Data;
        RunSnapshot snap = new()
        {
            Run = { Character = "ironclad", Ascension = 10, Act = 1, MaxHp = 80, CurrentHp = 64, Gold = 99 },
            Relics = new List<string> { "BURNING_BLOOD" },
            Map = MapGenerator.Generate(3),
            Plan = new ActPlan { ActId = "OVERGROWTH" },
        };
        snap.Map!.Current = null;
        List<CardDef> deck = data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH");
        var withNeow = new ActRollout(data, snap);
        var without = new ActRollout(data, snap) { DefaultNeow = false };
        int differing = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            RolloutResult a = withNeow.Run(deck, seed), b = without.Run(deck, seed);
            if (a.DeckSize != b.DeckSize || a.HpEnd != b.HpEnd) differing++;
        }
        Assert.True(differing > 10, "Neow's boon should change most futures");
    }
}

internal static class LetExtensions
{
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> f) => f(value);
}
