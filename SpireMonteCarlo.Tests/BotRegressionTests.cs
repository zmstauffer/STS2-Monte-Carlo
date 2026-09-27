using SpireMonteCarlo.Sim;
using Xunit;
using static SpireMonteCarlo.Tests.CombatKit;

namespace SpireMonteCarlo.Tests;

/// <summary>Bot mistakes found in playtests and by <c>sim cardvalue</c>, pinned so they stay fixed. Skipped when the local Codex cache isn't built.</summary>
public class BotRegressionTests
{
    [Fact]
    public void BloodlettingIsPlayedFirstWhenTheRestOfTheHandWantsTheEnergy()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight("DEFEND_IRONCLAD*20", monsters: new[] { Dummy(damage: 18, hits: 1) }, hp: 120), "BLOODLETTING,STRIKE_IRONCLAD,DEFEND_IRONCLAD,BASH");
        List<BotAction> plan = new BasicBot().Plan(c);
        Assert.NotEmpty(plan);
        Assert.Equal("BLOODLETTING", c.Hand.First(x => x.Tag == plan[0].Tag).Id);
    }

    [Fact]
    public void AFreeOfferingIsPlayedWhileItsEnergyAndCardsCanBeUsed()
    {
        // The exhaust pricing charged Offering for every later draw, as if it could be played each time, so the bot held it for turns
        // (fourth playtest analysis: turn 3 of a Cultists fight it played Strike and Defend with Offering and Neow's Fury in hand).
        if (Data == null) return;
        Combat c = WithHand(Fight("STRIKE_IRONCLAD*20", monsters: new[] { Dummy(damage: 9, hits: 1) }, hp: 200), "NEOWS_FURY,OFFERING,ASCENDERS_BANE,DEFEND_IRONCLAD,STRIKE_IRONCLAD");
        List<BotAction> plan = new BasicBot().Plan(c);
        Assert.Contains(plan, a => a.PotionId == null && c.Hand.First(x => x.Tag == a.Tag).Id == "OFFERING");
    }

    [Fact]
    public void ImperviousIsKeptForABigHitAndUsedOnOne()
    {
        // A card that exhausts itself loses nothing when played, except for block: 30 block spent on a 5-damage turn is wasted.
        if (Data == null) return;
        const string hand = "IMPERVIOUS,STRIKE_IRONCLAD,STRIKE_IRONCLAD,DEFEND_IRONCLAD,BASH";
        bool Plays(int damage)
        {
            Combat c = WithHand(Fight("STRIKE_IRONCLAD*20", monsters: new[] { Dummy(damage: damage, hits: 1) }, hp: 200), hand);
            return new BasicBot().Plan(c).Any(a => a.PotionId == null && c.Hand.First(x => x.Tag == a.Tag).Id == "IMPERVIOUS");
        }
        Assert.False(Plays(5));
        Assert.True(Plays(35));
    }

    [Fact]
    public void BurningPactNeverThinsTheDeckToNothing()
    {
        // The bot used to exhaust its whole starter deck with Burning Pact and then lose a normal fight at 200 HP.
        if (Data == null) return;
        SimData data = Data;
        List<CardDef> deck = data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH,BURNING_PACT");
        EncounterDef encounter = data.Encounters.Get("MAWLER_NORMAL");
        var bot = new BasicBot();
        for (ulong seed = 1; seed <= 60; seed++)
        {
            string[] lineup = encounter.Generate(new SimRng(SimRng.Mix(seed, 1)));
            FightResult r = FightSimulator.Run(deck, 200, 200, lineup.Select(data.Monsters.Get), 10, seed, bot: bot, services: data.Services);
            Assert.True(r.Won, $"seed {seed}");
        }
    }
}
