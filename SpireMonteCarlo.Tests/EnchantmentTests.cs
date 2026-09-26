using SpireMonteCarlo.Sim;
using Xunit;
using static SpireMonteCarlo.Tests.CombatKit;

namespace SpireMonteCarlo.Tests;

/// <summary>Card enchantments and the relics that hand them out (rules from the decompiled enchantment classes, v0.111). Skipped when the local Codex cache isn't built.</summary>
public class EnchantmentTests
{
    private static Combat WithEnchanted(string cardId, Enchant e, int amount, RelicKind[]? relics = null, int hp = 80)
    {
        Combat c = Fight("DEFEND_IRONCLAD*12", relics, hp: hp);
        c.Hand.Clear();
        c.Hand.Add(Enchantments.Apply(Data!.Cards.Get(cardId, false), e, amount).Instantiate());
        return c;
    }

    [Fact]
    public void SharpAddsDamageAndInstinctDoublesItAndCorruptedIsOneAndAHalfAtACost()
    {
        if (Data == null) return;
        Combat sharp = WithEnchanted("STRIKE_IRONCLAD", Enchant.Sharp, 3);
        Play(sharp, "STRIKE_IRONCLAD");
        Assert.Equal(500 - 9, sharp.Enemies[0].Hp);
        Combat instinct = WithEnchanted("STRIKE_IRONCLAD", Enchant.Instinct, 1);
        Play(instinct, "STRIKE_IRONCLAD");
        Assert.Equal(500 - 12, instinct.Enemies[0].Hp);
        Combat corrupted = WithEnchanted("STRIKE_IRONCLAD", Enchant.Corrupted, 1);
        Play(corrupted, "STRIKE_IRONCLAD");
        Assert.Equal(500 - 9, corrupted.Enemies[0].Hp);
        Assert.Equal(78, corrupted.Hp);
    }

    [Fact]
    public void NimbleAndAdroitAndGoopyChangeBlock()
    {
        if (Data == null) return;
        Combat nimble = WithEnchanted("DEFEND_IRONCLAD", Enchant.Nimble, 2);
        Play(nimble, "DEFEND_IRONCLAD");
        Assert.Equal(7, nimble.Block);
        Combat adroit = WithEnchanted("STRIKE_IRONCLAD", Enchant.Adroit, 3);
        Play(adroit, "STRIKE_IRONCLAD");
        Assert.Equal(3, adroit.Block);
        Combat goopy = WithEnchanted("DEFEND_IRONCLAD", Enchant.Goopy, 1);
        Assert.Contains(goopy.Hand, x => x.Exhaust);
        Play(goopy, "DEFEND_IRONCLAD");
        Assert.Equal(5, goopy.Block);
    }

    [Fact]
    public void GlamAndSpiralPlayACardAnExtraTime()
    {
        if (Data == null) return;
        Combat glam = WithEnchanted("STRIKE_IRONCLAD", Enchant.Glam, 1);
        Play(glam, "STRIKE_IRONCLAD");
        Assert.Equal(500 - 12, glam.Enemies[0].Hp);
        Combat spiral = WithEnchanted("STRIKE_IRONCLAD", Enchant.Spiral, 1);
        Play(spiral, "STRIKE_IRONCLAD");
        Assert.Equal(500 - 12, spiral.Enemies[0].Hp);
    }

    [Fact]
    public void SwiftDrawsAndSownGivesEnergyOnTheFirstPlayOnly()
    {
        if (Data == null) return;
        Combat swift = WithEnchanted("STRIKE_IRONCLAD", Enchant.Swift, 2);
        Play(swift, "STRIKE_IRONCLAD");
        Assert.Equal(2, swift.Hand.Count);
        Combat sown = WithEnchanted("STRIKE_IRONCLAD", Enchant.Sown, 2);
        Play(sown, "STRIKE_IRONCLAD");
        Assert.Equal(4, sown.Energy);
    }

    [Fact]
    public void MomentumGrowsAndVigorousPaysOnlyOnce()
    {
        if (Data == null) return;
        Combat momentum = WithEnchanted("STRIKE_IRONCLAD", Enchant.Momentum, 5);
        Play(momentum, "STRIKE_IRONCLAD");
        Assert.Equal(494, momentum.Enemies[0].Hp);
        Combat vigorous = WithEnchanted("STRIKE_IRONCLAD", Enchant.Vigorous, 8);
        Play(vigorous, "STRIKE_IRONCLAD");
        Assert.Equal(500 - 14, vigorous.Enemies[0].Hp);
    }

    [Fact]
    public void InkyAppliesWeakAndTezcatarasEmberIsFreeAndStronger()
    {
        if (Data == null) return;
        Combat inky = WithEnchanted("STRIKE_IRONCLAD", Enchant.Inky, 1);
        Play(inky, "STRIKE_IRONCLAD");
        Assert.Equal(1, inky.Enemies[0].Powers[(int)PowerKind.Weak]);
        Combat ember = WithEnchanted("STRIKE_IRONCLAD", Enchant.TezcatarasEmber, 1);
        Play(ember, "STRIKE_IRONCLAD");
        Assert.Equal(3, ember.Energy);
        Assert.Equal(500 - 9, ember.Enemies[0].Hp);
    }

    [Fact]
    public void RoyallyApprovedIsInnateAndRetainedAndSteadyRetains()
    {
        if (Data == null) return;
        Combat royal = WithEnchanted("STRIKE_IRONCLAD", Enchant.RoyallyApproved, 1);
        Assert.True(royal.Hand[0].Innate);
        royal.EndPlayerTurn(startNextTurn: false);
        Assert.Contains(royal.Hand, x => x.Id == "STRIKE_IRONCLAD");
        Combat steady = WithEnchanted("STRIKE_IRONCLAD", Enchant.Steady, 1);
        steady.EndPlayerTurn(startNextTurn: false);
        Assert.Contains(steady.Hand, x => x.Id == "STRIKE_IRONCLAD");
    }

    [Fact]
    public void ImbuedSkillsAreFreeAtTheStartOfTheCombat()
    {
        if (Data == null) return;
        SimData data = Data;
        var deck = data.ParseDeck("DEFEND_IRONCLAD*12").ToList();
        deck[0] = Enchantments.Apply(data.Cards.Get("SHRUG_IT_OFF", false), Enchant.Imbued, 1);
        var c = new Combat(deck, 80, 80, new[] { Dummy() }, 10, 5, services: data.Services);
        Assert.Equal(3, c.Energy);
        Assert.True(c.Block >= 8);   // the Shrug It Off already played
    }

    [Fact]
    public void MysticLighterAddsNineToEnchantedAttacksOnly()
    {
        if (Data == null) return;
        Combat lit = WithEnchanted("STRIKE_IRONCLAD", Enchant.Steady, 1, new[] { RelicKind.MysticLighter });
        Play(lit, "STRIKE_IRONCLAD");
        Assert.Equal(500 - 15, lit.Enemies[0].Hp);
        Combat plain = WithHand(Fight("DEFEND_IRONCLAD*12", new[] { RelicKind.MysticLighter }), "STRIKE_IRONCLAD");
        Play(plain, "STRIKE_IRONCLAD");
        Assert.Equal(494, plain.Enemies[0].Hp);
    }

    [Fact]
    public void EnchantmentRelicsPutTheirEnchantmentsOnTheDeck()
    {
        if (Data == null) return;
        SimData data = Data;
        EventState State() => new()
        {
            Data = data, Pool = data.PoolFor("ironclad"), RelicPool = data.RelicPoolFor("ironclad"), Rng = new SimRng(3),
            Deck = data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH,SHRUG_IT_OFF,INFLAME").ToList(), Hp = 70, MaxHp = 80, Gold = 100, Ascension = 10,
        };
        EventState claw = State(); RelicPickups.Apply("PAELS_CLAW", claw);
        Assert.Equal(4, claw.Deck.Count(c => c.Enchantment == Enchant.Goopy));
        EventState soup = State(); RelicPickups.Apply("NUTRITIOUS_SOUP", soup);
        Assert.Equal(5, soup.Deck.Count(c => c.Enchantment == Enchant.TezcatarasEmber));
        EventState boomerang = State(); RelicPickups.Apply("TRI_BOOMERANG", boomerang);
        Assert.Equal(3, boomerang.Deck.Count(c => c.Enchantment == Enchant.Instinct));
        EventState hammer = State(); RelicPickups.Apply("GNARLED_HAMMER", hammer);
        Assert.Equal(3, hammer.Deck.Count(c => c.Enchantment == Enchant.Sharp && c.EnchantAmount == 3));
        EventState dagger = State(); RelicPickups.Apply("PUNCH_DAGGER", dagger);
        Assert.Single(dagger.Deck, c => c.Enchantment == Enchant.Momentum);
    }

    [Fact]
    public void TheRunLevelPickupsEditTheRun()
    {
        if (Data == null) return;
        SimData data = Data;
        EventState State() => new()
        {
            Data = data, Pool = data.PoolFor("ironclad"), RelicPool = data.RelicPoolFor("ironclad"), Rng = new SimRng(3),
            Deck = data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH").ToList(), Hp = 70, MaxHp = 80, Gold = 100, Ascension = 10,
        };
        EventState s = State(); RelicPickups.Apply("LOOMING_FRUIT", s);
        Assert.Equal(111, s.MaxHp);
        s = State(); RelicPickups.Apply("SIGNET_RING", s);
        Assert.Equal(1099, s.Gold);
        s = State(); RelicPickups.Apply("EMPTY_CAGE", s);
        Assert.Equal(8, s.Deck.Count);
        s = State(); RelicPickups.Apply("PRESERVED_FOG", s);
        Assert.Equal(8, s.Deck.Count);
        Assert.Contains(s.Deck, c => c.Id == "FOLLY");
        s = State(); RelicPickups.Apply("PRECARIOUS_SHEARS", s);
        Assert.Equal(54, s.Hp);
        Assert.Equal(8, s.Deck.Count);
        s = State(); RelicPickups.Apply("SERE_TALON", s);
        Assert.Equal(3, s.Deck.Count(c => c.Id == "WISH"));
        Assert.Equal(71, s.MaxHp);
        s = State(); RelicPickups.Apply("CURSED_PEARL", s);
        Assert.Equal(433, s.Gold);
        Assert.Contains(s.Deck, c => c.Id == "GREED");
        s = State(); RelicPickups.Apply("SAND_CASTLE", s);
        Assert.True(s.Deck.Count(c => c.Upgraded) >= 3);
        s = State(); RelicPickups.Apply("DOLLYS_MIRROR", s);
        Assert.Equal(11, s.Deck.Count);
        s = State(); RelicPickups.Apply("LEES_WAFFLE", s);
        Assert.Equal(87, s.MaxHp);
        Assert.Equal(87, s.Hp);
        s = State(); s.Relics.Add("DARKSTONE_PERIAPT"); RelicPickups.Apply("HEFTY_TABLET", s);
        Assert.Contains(s.Deck, c => c.Id == "INJURY");
        Assert.Equal(86, s.MaxHp);
    }
}
