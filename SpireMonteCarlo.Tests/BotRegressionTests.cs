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
