using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>
/// The Act 3 mechanics (Combat.Act3.cs, MonsterBehaviorsAct3.cs), from the decompiled monster and power classes (v0.111): the Test Subject's
/// three forms, the Queen and her Amalgam, Chains of Binding, Dampen, Possess, The Forgotten's Dread, and Aeonglass. Skipped when the local
/// Codex cache isn't built.
/// </summary>
public class Act3MechanicsTests
{
    private static readonly Lazy<SimData?> Shared = new(() =>
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadMonsterAi() == null ? null : new SimData(cache);
    });

    private static SimData? Data => Shared.Value;

    private static Combat Fight(string deck, int maxEnergy, params string[] monsters) =>
        new(Data!.ParseDeck(deck).Select(c => c.Instantiate()), 300, 300, monsters.Select(Data.Monsters.Get), 10, 3, maxEnergy: maxEnergy, services: Data.Services);

    private static void Kill(Combat c, int enemy)
    {
        c.Enemies[enemy].Hp = 1;
        c.Enemies[enemy].Block = 0;
        c.Hand.Add(Data!.Cards.Get("STRIKE_IRONCLAD", false).Instantiate());
        c.Play(c.Hand.Count - 1, enemy);
    }

    [Fact]
    public void TheTestSubjectComesBackTwiceWithBiggerFormsBeforeTheFightIsWon()
    {
        if (Data == null) return;
        Combat c = Fight("DEFEND_IRONCLAD*10", 10, "TEST_SUBJECT");
        Enemy subject = c.Enemies[0];
        Kill(c, 0);
        Assert.Equal(CombatResult.Ongoing, c.Result);   // down, not beaten
        Assert.True(subject.Reviving);
        c.EndPlayerTurn();
        Assert.Equal((212, 212), (subject.Hp, subject.MaxHp));   // A8+: 212
        Assert.Equal(1, subject.Powers[(int)PowerKind.PainfulStabs]);
        Assert.Equal(0, subject.Powers[(int)PowerKind.Enrage]);   // powers are lost on death
        Assert.Equal("MULTI_CLAW", subject.Move?.Id);
        Assert.Equal(3, c.MoveHits(subject, subject.Move!));

        Kill(c, 0);
        c.EndPlayerTurn();
        Assert.Equal(313, subject.Hp);
        Assert.Equal(1, subject.Powers[(int)PowerKind.Nemesis]);
        Assert.Equal(0, subject.Powers[(int)PowerKind.Adaptable]);
        Assert.Equal("PHASE3_LACERATE", subject.Move?.Id);

        Kill(c, 0);
        Assert.Equal(CombatResult.Won, c.Result);
    }

    [Fact]
    public void TheQueenEnragesWhenHerAmalgamDiesAndBurnBrightGivesItStrength()
    {
        if (Data == null) return;
        Combat c = Fight("DEFEND_IRONCLAD*10", 3, "TORCH_HEAD_AMALGAM", "QUEEN");
        Enemy amalgam = c.Enemies[0], queen = c.Enemies[1];
        c.EndPlayerTurn();   // Puppet Strings
        Assert.True(c.PlayerPowers[(int)PowerKind.ChainsOfBinding] > 0);
        c.EndPlayerTurn();   // You Are Mine
        Assert.Equal("BURN_BRIGHT_FOR_ME", queen.Move?.Id);
        c.EndPlayerTurn();   // Burn Bright for Me
        Assert.Equal(1, amalgam.Powers[(int)PowerKind.Strength]);
        Assert.Equal("BURN_BRIGHT_FOR_ME", queen.Move?.Id);   // again while the Amalgam lives
        Kill(c, 0);
        Assert.Equal("ENRAGE", queen.Move?.Id);
        Assert.Equal(CombatResult.Ongoing, c.Result);   // the Amalgam is only a minion
    }

    [Fact]
    public void ChainsOfBindingLetsOnlyOneOfTheBoundCardsBePlayedEachTurn()
    {
        if (Data == null) return;
        Combat c = Fight("STRIKE_IRONCLAD*10", 10, "TEST_SUBJECT");
        c.PlayerPowers[(int)PowerKind.ChainsOfBinding] = 3;
        c.EndPlayerTurn();   // the next hand's first three Strikes are Bound
        Assert.Equal(5, c.Hand.Count);
        int played = 0;
        while (c.Hand.Count > 0 && c.CanPlay(c.Hand[0])) { c.Play(0, 0); played++; }
        Assert.Equal(3, played);   // two unbound Strikes and one Bound one
        c.EndPlayerTurn();
        played = 0;
        while (c.Hand.Count > 0 && c.CanPlay(c.Hand[0]) && c.Result == CombatResult.Ongoing) { c.Play(0, 0); played++; }
        Assert.Equal(3, played);   // it resets each turn
    }

    [Fact]
    public void DampenDowngradesUpgradedCardsUntilTheMagiKnightDies()
    {
        if (Data == null) return;
        Combat c = Fight("BASH+,STRIKE_IRONCLAD+*4,DEFEND_IRONCLAD*5", 3, "MAGI_KNIGHT", "FLAIL_KNIGHT");
        c.Enemies[0].SetMoveNow("DAMPEN_MOVE", c);
        c.EndPlayerTurn();
        var all = c.DrawPile.Concat(c.Hand).Concat(c.DiscardPile).ToList();
        Assert.DoesNotContain(all, card => card.Upgraded);
        Kill(c, 0);
        all = c.DrawPile.Concat(c.Hand).Concat(c.DiscardPile).ToList();
        Assert.Equal(5, all.Count(card => card.Upgraded));
    }

    [Fact]
    public void TheLostGivesBackTheStrengthItStoleWhenItDies()
    {
        if (Data == null) return;
        Combat c = Fight("DEFEND_IRONCLAD*10", 3, "THE_LOST", "THE_FORGOTTEN");
        c.EndPlayerTurn();   // Debilitating Smog and Miasma
        Assert.Equal(-2, c.PlayerPowers[(int)PowerKind.Strength]);
        Assert.Equal(-2, c.PlayerPowers[(int)PowerKind.Dexterity]);
        Kill(c, 0);
        Assert.Equal(0, c.PlayerPowers[(int)PowerKind.Strength]);
        Assert.Equal(-2, c.PlayerPowers[(int)PowerKind.Dexterity]);   // The Forgotten still has its share
    }

    [Fact]
    public void TheForgottensDreadHitsForFifteenPlusItsDexterity()
    {
        if (Data == null) return;
        Combat c = Fight("DEFEND_IRONCLAD*10", 3, "THE_FORGOTTEN");
        Enemy forgotten = c.Enemies[0];
        forgotten.Powers[(int)PowerKind.Dexterity] = 4;
        forgotten.SetMoveNow("DREAD", c);
        Assert.Equal(19, c.IntentDamage(forgotten));
    }

    [Fact]
    public void AeonglassGainsMoreStrengthEachIntensityAndItsWithersHurtMore()
    {
        if (Data == null) return;
        Combat c = Fight("DEFEND_IRONCLAD*10", 3, "AEONGLASS");
        Enemy glass = c.Enemies[0];
        glass.SetMoveNow("INCREASING_INTENSITY_MOVE", c);
        c.EndPlayerTurn();
        Assert.Equal(4, glass.Powers[(int)PowerKind.Strength]);   // A9+: 4
        glass.SetMoveNow("INCREASING_INTENSITY_MOVE", c);
        c.EndPlayerTurn();
        Assert.Equal(4 + 5, glass.Powers[(int)PowerKind.Strength]);

        glass.SetMoveNow("INCREASING_INTENSITY_MOVE", c);   // no attack this turn
        c.Hand.Clear();
        c.Hand.Add(Data.Cards.Get("WITHER", false).Instantiate());
        int hp = c.Hp;
        c.EndPlayerTurn();
        Assert.Equal(3 + 2 * 3, hp - c.Hp);   // two Intensities: 3 + 6
    }
}
