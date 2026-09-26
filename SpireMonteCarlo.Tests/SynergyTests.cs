using SpireMonteCarlo.Sim;
using Xunit;
using static SpireMonteCarlo.Tests.CombatKit;

namespace SpireMonteCarlo.Tests;

/// <summary>Build-around picks (<see cref="Synergy"/>). Skipped when the local Codex cache isn't built.</summary>
public class SynergyTests
{
    private static CardDef Card(string id) => Data!.Cards.Get(id, false);

    [Fact]
    public void ThemesAreReadFromTheRules()
    {
        if (Data == null) return;
        Assert.True(Synergy.PaysOff(Card("FEEL_NO_PAIN"), Theme.Exhaust) > 0);
        Assert.True(Synergy.Enables(Card("TRUE_GRIT"), Theme.Exhaust) > 0);
        Assert.True(Synergy.Enables(Card("HEMOKINESIS"), Theme.SelfDamage) > 0);
        Assert.True(Synergy.PaysOff(Card("RUPTURE"), Theme.SelfDamage) > 0);
        Assert.True(Synergy.PaysOff(Card("VICIOUS"), Theme.Vulnerable) > 0);
        Assert.True(Synergy.Enables(Card("UPPERCUT"), Theme.Vulnerable) > 0);
        Assert.Equal(0, Synergy.PaysOff(Card("SHRUG_IT_OFF"), Theme.Exhaust));
    }

    [Fact]
    public void PayoffsFitDecksWithEnablersAndEnablersFitDecksWithPayoffs()
    {
        if (Data == null) return;
        var exhaustDeck = Data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH,TRUE_GRIT,BURNING_PACT,SECOND_WIND");
        var plain = Data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH,SHRUG_IT_OFF,POMMEL_STRIKE,ANGER");
        Assert.True(Synergy.Fit(Card("FEEL_NO_PAIN"), exhaustDeck) > Synergy.Fit(Card("FEEL_NO_PAIN"), plain));
        var withPayoff = Data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH,FEEL_NO_PAIN");
        Assert.True(Synergy.Fit(Card("BURNING_PACT"), withPayoff) > 0);
        Assert.Equal(0, Synergy.Fit(Card("BURNING_PACT"), plain));
    }

    [Fact]
    public void StarterCardsDontCountSoPerfectedStrikeIsNotAFavouriteInEveryDeck()
    {
        if (Data == null) return;
        Assert.Equal(0, Synergy.Fit(Card("PERFECTED_STRIKE"), Data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH")));
    }

    [Fact]
    public void FitReordersPicksWithoutMakingSkipsRarer()
    {
        if (Data == null) return;
        RewardPool pool = Data.PoolFor("ironclad");
        var deck = Data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH,TRUE_GRIT,BURNING_PACT,SECOND_WIND,HAVOC");
        string[] offer = { "FEEL_NO_PAIN", "SHRUG_IT_OFF", "ANGER" };
        int feel = 0, skips = 0, plainSkips = 0;
        for (ulong i = 0; i < 4000; i++)
        {
            int pick = PickPolicy.Choose(offer, pool, deck, id => Data.Cards.Get(id, false), new SimRng(i));
            if (pick == 0) feel++;
            if (pick < 0) skips++;
            if (PickPolicy.Choose(offer, pool, deck.Count, new SimRng(i)) < 0) plainSkips++;
        }
        Assert.True(feel > 1600, $"Feel No Pain picked {feel} of 4000");
        Assert.InRange(skips, plainSkips * 0.8, plainSkips * 1.25);
    }
}
