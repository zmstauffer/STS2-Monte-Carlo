using SpireMonteCarlo.Sim;
using Xunit;
using static SpireMonteCarlo.Tests.CombatKit;

namespace SpireMonteCarlo.Tests;

/// <summary>Every potion the Ironclad can find (rules from the decompiled potion and power classes, v0.111). Skipped when the local Codex cache isn't built.</summary>
public class PotionBatchTests
{
    private static Combat Drink(string potion, string hand = "STRIKE_IRONCLAD,DEFEND_IRONCLAD,BASH", string deck = "DEFEND_IRONCLAD*12", int seed = 5, MonsterDef[]? monsters = null, int hp = 80)
    {
        Combat c = Fight(deck, potions: potion, seed: seed, monsters: monsters, hp: hp);
        WithHand(c, hand);
        return c;
    }

    private static void Use(Combat c, int target = 0) => c.UsePotion(0, target);

    [Fact]
    public void EveryPotionInTheRewardPoolsIsModelled()
    {
        string[] ids =
        {
            "ATTACK_POTION", "BLOCK_POTION", "BLOOD_POTION", "COLORLESS_POTION", "DEXTERITY_POTION", "ENERGY_POTION", "EXPLOSIVE_AMPOULE", "FIRE_POTION",
            "FLEX_POTION", "SKILL_POTION", "POWER_POTION", "SPEED_POTION", "STRENGTH_POTION", "SWIFT_POTION", "VULNERABLE_POTION", "WEAK_POTION",
            "ASHWATER", "BLESSING_OF_THE_FORGE", "CLARITY", "CURE_ALL", "DUPLICATOR", "FORTIFIER", "FYSH_OIL", "GAMBLERS_BREW", "HEART_OF_IRON", "LIQUID_BRONZE",
            "POTION_OF_BINDING", "POWDERED_DEMISE", "RADIANT_TINCTURE", "REGEN_POTION", "STABLE_SERUM", "TOUCH_OF_INSANITY",
            "BEETLE_JUICE", "BOTTLED_POTENTIAL", "DISTILLED_CHAOS", "DROPLET_OF_PRECOGNITION", "ENTROPIC_BREW", "FAIRY_IN_A_BOTTLE", "FRUIT_JUICE", "GIGANTIFICATION_POTION",
            "LIQUID_MEMORIES", "LUCKY_TONIC", "MAZALETHS_GIFT", "OROBIC_ACID", "SHACKLING_POTION", "SHIP_IN_A_BOTTLE", "SNECKO_OIL", "SOLDIERS_STEW",
        };
        Assert.All(ids, id => Assert.NotNull(PotionLibrary.Find(id)));
        // Each rarity of the Ironclad's pool holds 16 potions.
        Assert.Equal(16, PotionLibrary.All.Count(p => p.Rarity == PotionRarity.Common && !p.Token));
        Assert.Equal(16, PotionLibrary.All.Count(p => p.Rarity == PotionRarity.Uncommon && !p.Token));
        Assert.Equal(16, PotionLibrary.All.Count(p => p.Rarity == PotionRarity.Rare && !p.Token));
    }

    [Fact]
    public void RollNeverComesUpEmptyNow()
    {
        var rng = new SimRng(4);
        for (int i = 0; i < 2000; i++) Assert.NotNull(PotionLibrary.Roll(rng));
    }

    [Fact]
    public void TheSimplePowerPotionsGiveTheirStacks()
    {
        if (Data == null) return;
        Combat bronze = Drink("LIQUID_BRONZE"); Use(bronze, -1);
        Assert.Equal(3, bronze.PlayerPowers[(int)PowerKind.Thorns]);
        Combat tonic = Drink("LUCKY_TONIC"); Use(tonic, -1);
        Assert.Equal(1, tonic.PlayerPowers[(int)PowerKind.Buffer]);
        Combat gift = Drink("MAZALETHS_GIFT"); Use(gift, -1);
        Assert.Equal(1, gift.PlayerPowers[(int)PowerKind.Ritual]);
        gift.EndPlayerTurn();
        Assert.Equal(1, gift.PlayerPowers[(int)PowerKind.Strength]);
        Combat regen = Drink("REGEN_POTION", hp: 50); regen.EndPlayerTurn(startNextTurn: false);
        Assert.Equal(50, regen.Hp);
    }

    [Fact]
    public void LuckyTonicPreventsTheNextHpLoss()
    {
        if (Data == null) return;
        Combat c = Drink("LUCKY_TONIC", monsters: new[] { Dummy(damage: 10) });
        Use(c, -1);
        c.EndPlayerTurn();
        Assert.Equal(80, c.Hp);
        c.EndPlayerTurn();
        Assert.Equal(70, c.Hp);
    }

    [Fact]
    public void RegenHealsAtTheEndOfEachTurnWithFewerStacks()
    {
        if (Data == null) return;
        Combat c = Drink("REGEN_POTION", monsters: new[] { Dummy(damage: 20) });
        Use(c, -1);
        c.EndPlayerTurn();   // heals 5 before the hit: 80 - 20 = 60 (capped at 80 first)
        Assert.Equal(60, c.Hp);
        c.EndPlayerTurn();   // heals 4, then 20 damage
        Assert.Equal(44, c.Hp);
    }

    [Fact]
    public void PotionOfBindingWeakensAndBreaksEveryEnemy()
    {
        if (Data == null) return;
        Combat c = Drink("POTION_OF_BINDING", monsters: new[] { Dummy(), Dummy() });
        Use(c, -1);
        Assert.All(c.Enemies, e => { Assert.Equal(1, e.Powers[(int)PowerKind.Weak]); Assert.Equal(1, e.Powers[(int)PowerKind.Vulnerable]); });
    }

    [Fact]
    public void PowderedDemiseHurtsTheEnemyAtTheEndOfItsTurns()
    {
        if (Data == null) return;
        Combat c = Drink("POWDERED_DEMISE");
        Use(c, 0);
        c.EndPlayerTurn();
        Assert.Equal(491, c.Enemies[0].Hp);
        c.EndPlayerTurn();
        Assert.Equal(482, c.Enemies[0].Hp);
    }

    [Fact]
    public void BeetleJuiceCutsEnemyDamageByThirtyPercentForFourTurns()
    {
        if (Data == null) return;
        Combat c = Drink("BEETLE_JUICE", monsters: new[] { Dummy(damage: 10) });
        Use(c, 0);
        c.EndPlayerTurn();
        Assert.Equal(73, c.Hp);   // 10 * 0.7 = 7
        for (int i = 0; i < 3; i++) c.EndPlayerTurn();
        int before = c.Hp;
        c.EndPlayerTurn();   // the fifth hit is at full strength
        Assert.Equal(before - 10, c.Hp);
    }

    [Fact]
    public void ClarityAndRadiantTinctureAndShipInABottleWorkOverSeveralTurns()
    {
        if (Data == null) return;
        Combat clarity = Drink("CLARITY", deck: "STRIKE_IRONCLAD*20"); Use(clarity, -1);
        clarity.EndPlayerTurn();
        Assert.Equal(6, clarity.Hand.Count);
        Combat tincture = Drink("RADIANT_TINCTURE"); Use(tincture, -1);
        Assert.Equal(4, tincture.Energy);
        tincture.EndPlayerTurn();
        Assert.Equal(4, tincture.Energy);
        Combat ship = Drink("SHIP_IN_A_BOTTLE"); Use(ship, -1);
        Assert.Equal(10, ship.Block);
        ship.EndPlayerTurn();
        Assert.Equal(10, ship.Block);
    }

    [Fact]
    public void DuplicatorPlaysTheNextCardTwiceAndGigantificationTriplesTheNextAttack()
    {
        if (Data == null) return;
        Combat dup = Drink("DUPLICATOR"); Use(dup, -1);
        Play(dup, "STRIKE_IRONCLAD");
        Assert.Equal(500 - 12, dup.Enemies[0].Hp);
        Combat giant = Drink("GIGANTIFICATION_POTION"); Use(giant, -1);
        Play(giant, "STRIKE_IRONCLAD");
        Assert.Equal(500 - 18, giant.Enemies[0].Hp);
        Play(giant, "BASH");
        Assert.Equal(500 - 18 - 8, giant.Enemies[0].Hp);
    }

    [Fact]
    public void StableSerumKeepsTheHandForTwoTurns()
    {
        if (Data == null) return;
        Combat c = Drink("STABLE_SERUM", hand: "STRIKE_IRONCLAD,DEFEND_IRONCLAD", deck: "DEFEND_IRONCLAD*40"); Use(c, -1);
        c.EndPlayerTurn();
        Assert.Equal(7, c.Hand.Count);    // the two kept cards plus a new hand
        c.EndPlayerTurn();
        Assert.Equal(10, c.Hand.Count);   // kept again
        c.EndPlayerTurn();
        Assert.Equal(5, c.Hand.Count);    // the serum has worn off
    }

    [Fact]
    public void BottledPotentialShufflesTheHandBackAndDrawsFive()
    {
        if (Data == null) return;
        Combat c = Drink("BOTTLED_POTENTIAL", hand: "STRIKE_IRONCLAD,DEFEND_IRONCLAD,BASH"); Use(c, -1);
        Assert.Equal(5, c.Hand.Count);
    }

    [Fact]
    public void GlowwaterAndAshwaterAndGamblersBrewChangeTheHand()
    {
        if (Data == null) return;
        Combat glow = Drink("GLOWWATER_POTION", hand: "STRIKE_IRONCLAD,DEFEND_IRONCLAD", deck: "BASH*40"); Use(glow, -1);
        Assert.Equal(10, glow.Hand.Count);
        Assert.Equal(2, glow.ExhaustPile.Count);
        Combat ash = Drink("ASHWATER", hand: "WOUND,STRIKE_IRONCLAD"); Use(ash, -1);
        Assert.Single(ash.ExhaustPile);
        Combat brew = Drink("GAMBLERS_BREW", hand: "WOUND,WOUND,STRIKE_IRONCLAD"); Use(brew, -1);
        Assert.Equal(3, brew.Hand.Count);
        Assert.Equal(2, brew.DiscardPile.Count);
    }

    [Fact]
    public void DropletAndLiquidMemoriesMoveACardToTheHandAndTheLatterIsFree()
    {
        if (Data == null) return;
        Combat droplet = Drink("DROPLET_OF_PRECOGNITION", hand: "STRIKE_IRONCLAD", deck: "BASH*10"); Use(droplet, -1);
        Assert.Equal(2, droplet.Hand.Count);
        Combat memories = Drink("LIQUID_MEMORIES", hand: "STRIKE_IRONCLAD"); memories.DiscardPile.Add(Data.Cards.Get("BASH", false).Instantiate()); Use(memories, -1);
        CardDef bash = memories.Hand.Single(x => x.Id == "BASH");
        Assert.Equal(0, memories.EffectiveCost(bash));
    }

    [Fact]
    public void TheGeneratingPotionsAddAFreeCard()
    {
        if (Data == null) return;
        foreach (string potion in new[] { "COLORLESS_POTION", "SKILL_POTION", "POWER_POTION", "OROBIC_ACID" })
        {
            Combat c = Drink(potion, hand: "STRIKE_IRONCLAD"); Use(c, -1);
            Assert.True(c.Hand.Count >= 2, potion);
            Assert.All(c.Hand.Skip(1), x => Assert.True(x.FreeThisTurn, potion));
        }
        Combat acid = Drink("OROBIC_ACID", hand: ""); Use(acid, -1);
        Assert.Equal(3, acid.Hand.Count);
    }

    [Fact]
    public void SneckoOilDrawsSevenAndRandomisesCosts()
    {
        if (Data == null) return;
        Combat c = Drink("SNECKO_OIL", hand: "STRIKE_IRONCLAD", deck: "BASH*20"); Use(c, -1);
        Assert.Equal(8, c.Hand.Count);
        Assert.All(c.Hand, x => Assert.InRange(c.EffectiveCost(x), 0, 3));
    }

    [Fact]
    public void SoldiersStewPlaysEveryStrikeAnExtraTime()
    {
        if (Data == null) return;
        Combat c = Drink("SOLDIERS_STEW", hand: "STRIKE_IRONCLAD,BASH"); Use(c, -1);
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(500 - 12, c.Enemies[0].Hp);
        Play(c, "BASH");
        Assert.Equal(500 - 12 - 8, c.Enemies[0].Hp);
    }

    [Fact]
    public void TouchOfInsanityMakesTheCostliestCardFreeForTheCombat()
    {
        if (Data == null) return;
        Combat c = Drink("TOUCH_OF_INSANITY", hand: "STRIKE_IRONCLAD,BASH"); Use(c, -1);
        Assert.Equal(0, c.EffectiveCost(c.Hand.Single(x => x.Id == "BASH")));
        c.EndPlayerTurn();
        Assert.Equal(0, c.EffectiveCost(c.DiscardPile.Concat(c.Hand).First(x => x.Id == "BASH")));
    }

    [Fact]
    public void EntropicBrewFillsEveryEmptySlot()
    {
        if (Data == null) return;
        Combat c = Drink("ENTROPIC_BREW"); Use(c, -1);
        Assert.Equal(3, c.Potions.Count);
    }

    [Fact]
    public void FoulPotionHurtsEveryoneIncludingThePlayer()
    {
        if (Data == null) return;
        Combat c = Drink("FOUL_POTION"); Use(c, -1);
        Assert.Equal(488, c.Enemies[0].Hp);
        Assert.Equal(68, c.Hp);
    }
}
