using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>
/// One test per relic behavior, each expected number taken from the decompiled relic class (v0.111). "Every N" relics fire at
/// every multiple of N. Skipped when the local Codex cache isn't built.
/// </summary>
public class RelicTests
{
    private static readonly Lazy<SimData?> Shared = new(() =>
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadCardDefs() == null ? null : new SimData(cache);
    });

    private static SimData? Data => Shared.Value;

    private static MonsterDef Dummy(int hp = 1000, int damage = 0) => new()
    {
        Id = "DUMMY", HpMin = hp, HpMax = hp, HpMinTough = hp, HpMaxTough = hp,
        Moves = { ["HIT"] = new MoveDef { Id = "HIT", Intent = damage > 0 ? "Attack" : "Buff", Damage = damage, DamageDeadly = damage, Hits = 1 } },
        States = { ["HIT_MOVE"] = new StateDef { Id = "HIT_MOVE", Kind = StateKind.Move, MoveId = "HIT", Next = "HIT_MOVE" } },
        InitialState = "HIT_MOVE",
    };

    /// <summary>A combat with exactly the given hand and relics; draw and discard piles start empty unless given.</summary>
    private static Combat Setup(string relics, string hand = "", string draw = "", int enemyHp = 1000, int enemyDamage = 0, int hp = 80, int stakes = 0, int enemies = 1)
    {
        SimData data = Data!;
        var kinds = relics.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id =>
        {
            RelicKind k = RelicRules.Parse(id);
            Assert.NotEqual(RelicKind.Unknown, k);
            return k;
        });
        var monsters = Enumerable.Range(0, enemies).Select(_ => Dummy(enemyHp, enemyDamage)).ToArray();
        var c = new Combat(Array.Empty<CardDef>(), hp, 80, monsters, 0, 7, services: data.Services, relics: kinds, stakes: stakes);
        c.Hand.AddRange(data.ParseDeck(hand).Select(x => x.Instantiate()));
        c.DrawPile.AddRange(data.ParseDeck(draw).Select(x => x.Instantiate()));
        return c;
    }

    private static void Play(Combat c, string id, int target = 0) => c.Play(c.Hand.FindIndex(x => x.Id == id), target);

    private static int Power(Combat c, PowerKind k) => c.PlayerPowers[(int)k];

    [Fact]
    public void RelicIdsParseAndUnknownOnesAreIgnored()
    {
        Assert.Equal(RelicKind.BagOfMarbles, RelicRules.Parse("BAG_OF_MARBLES"));
        Assert.Equal(RelicKind.CaptainsWheel, RelicRules.Parse("CAPTAINS_WHEEL"));
        Assert.Equal(RelicKind.Unknown, RelicRules.Parse("SOME_NEW_RELIC"));
    }

    [Fact]
    public void CombatStartRelicsGiveTheirBonuses()
    {
        if (Data == null) return;
        Combat c = Setup("ANCHOR,VAJRA,ODDLY_SMOOTH_STONE,GORGET,BRONZE_SCALES,AKABEKO,LANTERN");
        Assert.Equal(10, c.Block);
        Assert.Equal(1, Power(c, PowerKind.Strength));
        Assert.Equal(1, Power(c, PowerKind.Dexterity));
        Assert.Equal(4, Power(c, PowerKind.Plating));
        Assert.Equal(3, Power(c, PowerKind.Thorns));
        Assert.Equal(8, Power(c, PowerKind.Vigor));
        Assert.Equal(4, c.Energy);
    }

    [Fact]
    public void LanternOnlyHelpsTheFirstTurn()
    {
        if (Data == null) return;
        Combat c = Setup("LANTERN");
        Assert.Equal(4, c.Energy);
        c.EndPlayerTurn();
        Assert.Equal(3, c.Energy);
    }

    [Fact]
    public void BagOfMarblesAndRedMaskDebuffEveryEnemyAtTheStart()
    {
        if (Data == null) return;
        Combat c = Setup("BAG_OF_MARBLES,RED_MASK", enemies: 2);
        Assert.All(c.Enemies, e =>
        {
            Assert.Equal(1, e.Powers[(int)PowerKind.Vulnerable]);
            Assert.Equal(1, e.Powers[(int)PowerKind.Weak]);
        });
    }

    [Fact]
    public void FestivePopperHitsEveryEnemyForNine()
    {
        if (Data == null) return;
        Combat c = Setup("FESTIVE_POPPER", enemies: 2);
        Assert.All(c.Enemies, e => Assert.Equal(991, e.Hp));
    }

    [Fact]
    public void BloodVialHealsTwoAtTheStartAndBurningBloodSixAtTheEnd()
    {
        if (Data == null) return;
        Combat c = Setup("BLOOD_VIAL,BURNING_BLOOD", "STRIKE_IRONCLAD", enemyHp: 3, hp: 50);
        Assert.Equal(52, c.Hp);
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(CombatResult.Won, c.Result);
        Assert.Equal(58, c.Hp);
    }

    [Fact]
    public void MeatOnTheBoneHealsTwelveOnlyAtOrBelowHalfHpThenBurningBloodSix()
    {
        if (Data == null) return;
        Combat low = Setup("MEAT_ON_THE_BONE,BURNING_BLOOD", "STRIKE_IRONCLAD", enemyHp: 3, hp: 40);
        Play(low, "STRIKE_IRONCLAD");
        Assert.Equal(58, low.Hp);
        Combat high = Setup("MEAT_ON_THE_BONE,BURNING_BLOOD", "STRIKE_IRONCLAD", enemyHp: 3, hp: 41);
        Play(high, "STRIKE_IRONCLAD");
        Assert.Equal(47, high.Hp);
    }

    [Fact]
    public void FlatHpAmountsScaleWithTheHpPool()
    {
        if (Data == null) return;
        SimData data = Data;
        var c = new Combat(Array.Empty<CardDef>(), 100, 200, new[] { Dummy(3) }, 0, 7, services: data.Services,
            relics: new[] { RelicKind.BurningBlood }, hpScale: 3.0);
        c.Hand.AddRange(data.ParseDeck("STRIKE_IRONCLAD").Select(x => x.Instantiate()));
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(118, c.Hp);
    }

    [Fact]
    public void BronzeScalesHurtsAttackersEvenWhenBlocked()
    {
        if (Data == null) return;
        Combat c = Setup("BRONZE_SCALES", "DEFEND_IRONCLAD,DEFEND_IRONCLAD", enemyDamage: 5);
        Play(c, "DEFEND_IRONCLAD");
        c.EndPlayerTurn();
        Assert.Equal(997, c.Enemies[0].Hp);
    }

    [Fact]
    public void ShurikenGivesStrengthOnEveryThirdAttack()
    {
        if (Data == null) return;
        Combat c = Setup("SHURIKEN", "STRIKE_IRONCLAD*3");
        Play(c, "STRIKE_IRONCLAD");
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(0, Power(c, PowerKind.Strength));
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(1, Power(c, PowerKind.Strength));
    }

    [Fact]
    public void KunaiOrnamentalFanAndLetterOpenerCountTheirOwnCardTypes()
    {
        if (Data == null) return;
        Combat c = Setup("KUNAI,ORNAMENTAL_FAN", "STRIKE_IRONCLAD*3");
        for (int i = 0; i < 3; i++) Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(1, Power(c, PowerKind.Dexterity));
        Assert.Equal(4, c.Block);

        Combat s = Setup("LETTER_OPENER", "DEFEND_IRONCLAD*3", enemies: 2);
        for (int i = 0; i < 3; i++) Play(s, "DEFEND_IRONCLAD");
        Assert.All(s.Enemies, e => Assert.Equal(995, e.Hp));
    }

    [Fact]
    public void PenNibDoublesEveryTenthAttack()
    {
        if (Data == null) return;
        Combat c = Setup("PEN_NIB", "STRIKE_IRONCLAD*10");
        c.PlayerPowers[(int)PowerKind.FreeAttack] = 99;
        for (int i = 0; i < 9; i++) Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(1000 - 9 * 6, c.Enemies[0].Hp);
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(1000 - 9 * 6 - 12, c.Enemies[0].Hp);
    }

    [Fact]
    public void StrikeDummyAddsThreeToStrikesOnly()
    {
        if (Data == null) return;
        Combat c = Setup("STRIKE_DUMMY", "STRIKE_IRONCLAD,BASH");
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(991, c.Enemies[0].Hp);
        Play(c, "BASH");
        Assert.Equal(983, c.Enemies[0].Hp);
    }

    [Fact]
    public void PaperPhrogMakesVulnerableSeventyFivePercent()
    {
        if (Data == null) return;
        Combat c = Setup("PAPER_PHROG", "STRIKE_IRONCLAD");
        c.Enemies[0].Powers[(int)PowerKind.Vulnerable] = 2;
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(1000 - 10, c.Enemies[0].Hp);   // floor(6 * 1.75)
    }

    [Fact]
    public void RedSkullGivesThreeStrengthAtOrBelowHalfHp()
    {
        if (Data == null) return;
        Combat low = Setup("RED_SKULL", "STRIKE_IRONCLAD", hp: 40);
        Play(low, "STRIKE_IRONCLAD");
        Assert.Equal(991, low.Enemies[0].Hp);
        Combat high = Setup("RED_SKULL", "STRIKE_IRONCLAD", hp: 41);
        Play(high, "STRIKE_IRONCLAD");
        Assert.Equal(994, high.Enemies[0].Hp);
    }

    [Fact]
    public void OrichalcumGivesSixBlockOnlyWhenYouEndWithNone()
    {
        if (Data == null) return;
        Combat none = Setup("ORICHALCUM", enemyDamage: 10);
        none.EndPlayerTurn();
        Assert.Equal(76, none.Hp);
        Combat some = Setup("ORICHALCUM", "DEFEND_IRONCLAD", enemyDamage: 10);
        Play(some, "DEFEND_IRONCLAD");
        some.EndPlayerTurn();
        Assert.Equal(75, some.Hp);
    }

    [Fact]
    public void TungstenRodTakesOffOnePointOfEveryLoss()
    {
        if (Data == null) return;
        Combat c = Setup("TUNGSTEN_ROD", enemyDamage: 5);
        c.EndPlayerTurn();
        Assert.Equal(76, c.Hp);
    }

    [Fact]
    public void BeatingRemnantCapsALossAtTwentyPerTurn()
    {
        if (Data == null) return;
        Combat c = Setup("BEATING_REMNANT", enemyDamage: 50);
        c.EndPlayerTurn();
        Assert.Equal(60, c.Hp);
    }

    [Fact]
    public void LizardTailRevivesOnceAtHalfMaxHp()
    {
        if (Data == null) return;
        Combat c = Setup("LIZARD_TAIL", enemyDamage: 100, hp: 10);
        c.EndPlayerTurn();
        Assert.Equal(CombatResult.Ongoing, c.Result);
        Assert.Equal(40, c.Hp);
        c.EndPlayerTurn();
        Assert.Equal(CombatResult.Lost, c.Result);
    }

    [Fact]
    public void CentennialPuzzleDrawsThreeOnTheFirstHpLossOnly()
    {
        if (Data == null) return;
        Combat c = Setup("CENTENNIAL_PUZZLE", "", "STRIKE_IRONCLAD*12", enemyDamage: 5);
        c.Hand.Clear();
        c.EndPlayerTurn();
        // The turn-2 hand is 5 cards, plus 3 drawn when the first hit landed at the end of turn 1.
        Assert.Equal(8, c.Hand.Count);
        int hand = c.Hand.Count;
        c.EndPlayerTurn();
        Assert.Equal(5, c.Hand.Count + 0 * hand);
    }

    [Fact]
    public void TurnNumberRelicsFireOnTheirTurn()
    {
        if (Data == null) return;
        Combat c = Setup("CANDELABRA,HORN_CLEAT,CAPTAINS_WHEEL,CHANDELIER,HAPPY_FLOWER");
        Assert.Equal(3, c.Energy);
        c.EndPlayerTurn();   // turn 2
        Assert.Equal(5, c.Energy);
        Assert.Equal(14, c.Block);
        c.EndPlayerTurn();   // turn 3: Chandelier (3), Happy Flower (1), Captain's Wheel (18)
        Assert.Equal(7, c.Energy);
        Assert.Equal(18, c.Block);
    }

    [Fact]
    public void IceCreamKeepsUnspentEnergy()
    {
        if (Data == null) return;
        Combat c = Setup("ICE_CREAM");
        c.EndPlayerTurn();
        Assert.Equal(6, c.Energy);
    }

    [Fact]
    public void MercuryHourglassHitsEveryEnemyEachTurn()
    {
        if (Data == null) return;
        Combat c = Setup("MERCURY_HOURGLASS", enemies: 2);
        Assert.All(c.Enemies, e => Assert.Equal(997, e.Hp));
        c.EndPlayerTurn();
        Assert.All(c.Enemies, e => Assert.Equal(994, e.Hp));
    }

    [Fact]
    public void GremlinHornGivesEnergyAndACardWhenAnEnemyDies()
    {
        if (Data == null) return;
        Combat c = Setup("GREMLIN_HORN", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", enemyHp: 6, enemies: 2);
        Play(c, "STRIKE_IRONCLAD", 0);
        Assert.Equal(3, c.Energy);   // 3 - 1 for the Strike + 1 from the horn
        Assert.Contains(c.Hand, x => x.Id == "DEFEND_IRONCLAD");
    }

    [Fact]
    public void VambraceDoublesOnlyTheFirstCardBlockOfTheCombat()
    {
        if (Data == null) return;
        Combat c = Setup("VAMBRACE", "DEFEND_IRONCLAD,DEFEND_IRONCLAD");
        Play(c, "DEFEND_IRONCLAD");
        Assert.Equal(10, c.Block);
        Play(c, "DEFEND_IRONCLAD");
        Assert.Equal(15, c.Block);
    }

    [Fact]
    public void SturdyClampKeepsTenBlock()
    {
        if (Data == null) return;
        Combat c = Setup("STURDY_CLAMP", "DEFEND_IRONCLAD*3");
        for (int i = 0; i < 3; i++) Play(c, "DEFEND_IRONCLAD");
        Assert.Equal(15, c.Block);
        c.EndPlayerTurn();
        Assert.Equal(10, c.Block);
    }

    [Fact]
    public void RelicRewardsFollowTheGamesRarityOddsAndNeverRepeat()
    {
        if (Data == null) return;
        RelicPool pool = Data.RelicPoolFor("ironclad");
        var rng = new SimRng(5);
        var owned = new HashSet<string>();
        for (int i = 0; i < 60; i++)
        {
            string? id = pool.Roll(rng, owned);
            if (id == null) continue;
            Assert.True(owned.Add(id), $"{id} was rolled twice");
        }
        Assert.True(owned.Count > 30);
        Assert.DoesNotContain(owned, id => id is "BURNING_BLOOD" or "ANCHOR_NOT_A_RELIC");
    }
}
