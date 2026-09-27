using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

public class SynergyPayoffTests
{
    [Fact]
    public void CascadeDoesNotFeedExhaustButHavocDoes()
    {
        SimData? data = CombatKit.Data;
        if (data == null) return;
        Assert.Equal(0, Synergy.Enables(data.Cards.Get("CASCADE", false), Theme.Exhaust));
        Assert.True(Synergy.Enables(data.Cards.Get("HAVOC", false), Theme.Exhaust) > 0);
    }

    [Fact]
    public void APurePayoffsRatingCountsOnlyAsFarAsTheDeckFeedsIt()
    {
        SimData? data = CombatKit.Data;
        if (data == null) return;
        CardDef fnp = data.Cards.Get("FEEL_NO_PAIN", false);
        Assert.Equal(0, Synergy.RatingShare(fnp, data.ParseDeck("STRIKE_IRONCLAD*5,DEFEND_IRONCLAD*4,BASH,CASCADE")));
        Assert.Equal(1, Synergy.RatingShare(fnp, data.ParseDeck("TRUE_GRIT,BURNING_PACT,SECOND_WIND,HAVOC")));
        Assert.Equal(1, Synergy.RatingShare(data.Cards.Get("TRUE_GRIT", false), data.ParseDeck("BASH")));   // an enabler keeps its rating
    }
}
