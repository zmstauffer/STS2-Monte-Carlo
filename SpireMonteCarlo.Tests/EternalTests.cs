using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>Eternal cards (Ascender's Bane and a few curses) can't be removed (<c>CardModel.IsRemovable</c>); removals take the next worst card.</summary>
public class EternalTests
{
    [Fact]
    public void RemovingTheWorstCardSkipsAscendersBane()
    {
        SimData? data = CombatKit.Data;
        if (data == null) return;
        var deck = data.ParseDeck("ASCENDERS_BANE,STRIKE_IRONCLAD,BASH");
        Assert.True(DeckPolicies.RemoveWorst(deck));
        Assert.Equal(new[] { "ASCENDERS_BANE", "BASH" }, deck.Select(c => c.Id));
        Assert.False(DeckPolicies.RemoveWorst(data.ParseDeck("ASCENDERS_BANE")));
    }
}
