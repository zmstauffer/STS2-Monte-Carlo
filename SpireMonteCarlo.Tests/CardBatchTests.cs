using SpireMonteCarlo.Sim;
using Xunit;
using static SpireMonteCarlo.Tests.CombatKit;

namespace SpireMonteCarlo.Tests;

/// <summary>Colorless, event, curse, status and token cards an Ironclad deck can hold (rules from the decompiled card and power classes, v0.111). Skipped when the local Codex cache isn't built.</summary>
public class CardBatchTests
{
    private static Combat Hand(string hand, string deck = "DEFEND_IRONCLAD*30", MonsterDef[]? monsters = null, RelicKind[]? relics = null, int hp = 80)
    {
        Combat c = Fight(deck, relics, monsters: monsters, hp: hp);
        return WithHand(c, hand);
    }

    [Fact]
    public void EveryNonMultiplayerColorlessEventCurseStatusAndQuestCardHasARecipe()
    {
        if (Data == null) return;
        SimData data = Data;
        // The multiplayer-only cards have no recipe on purpose.
        var multiplayer = new HashSet<string> { "BEACON_OF_HOPE", "BELIEVE_IN_YOU", "COORDINATE", "GANG_UP", "HUDDLE_UP", "INTERCEPT", "KNOCKDOWN", "LIFT", "MIMIC", "RALLY", "TAG_TEAM" };
        foreach (string color in new[] { "colorless", "curse", "event", "quest" })
            foreach (string id in data.CardIdsOfColor(color).Where(id => !multiplayer.Contains(id)))
                Assert.True(data.Cards.HasRecipe(id), id);
    }

    [Fact]
    public void SimpleAttacksAndSkillsDoTheirNumbers()
    {
        if (Data == null) return;
        Combat c = Hand("ULTIMATE_STRIKE,FLASH_OF_STEEL,FINESSE,BYRD_SWOOP");
        Play(c, "ULTIMATE_STRIKE"); Assert.Equal(486, c.Enemies[0].Hp);
        Play(c, "FLASH_OF_STEEL"); Assert.Equal(481, c.Enemies[0].Hp);
        Play(c, "FINESSE"); Assert.Equal(4, c.Block);
        Play(c, "BYRD_SWOOP"); Assert.Equal(467, c.Enemies[0].Hp);
        Combat more = Hand("SQUASH,PECK", relics: new[] { RelicKind.Lantern });
        Play(more, "SQUASH"); Assert.Equal(2, more.Enemies[0].Powers[(int)PowerKind.Vulnerable]);
        int afterSquash = more.Enemies[0].Hp;
        Play(more, "PECK"); Assert.True(afterSquash - more.Enemies[0].Hp >= 3);
        Combat all = Hand("EXTERMINATE"); Play(all, "EXTERMINATE", -1); Assert.Equal(488, all.Enemies[0].Hp);
    }

    [Fact]
    public void FisticuffsGivesBlockEqualToTheDamageDealt()
    {
        if (Data == null) return;
        Combat c = Hand("FISTICUFFS");
        Play(c, "FISTICUFFS");
        Assert.Equal(7, c.Block);
    }

    [Fact]
    public void OmnisliceHitsEveryOtherEnemyForTheDamageDealt()
    {
        if (Data == null) return;
        Combat c = Hand("OMNISLICE", monsters: new[] { Dummy(), Dummy() });
        Play(c, "OMNISLICE", 0);
        Assert.Equal(492, c.Enemies[0].Hp);
        Assert.Equal(492, c.Enemies[1].Hp);
    }

    [Fact]
    public void BolasAndTheHatchetComeBackNextTurn()
    {
        if (Data == null) return;
        Combat c = Hand("BOLAS,THRUMMING_HATCHET");
        Play(c, "BOLAS"); Play(c, "THRUMMING_HATCHET");
        Assert.DoesNotContain(c.DiscardPile, x => x.Id is "BOLAS" or "THRUMMING_HATCHET");
        c.EndPlayerTurn();
        Assert.Contains(c.Hand, x => x.Id == "BOLAS");
        Assert.Contains(c.Hand, x => x.Id == "THRUMMING_HATCHET");
    }

    [Fact]
    public void ClashOnlyPlaysWhenTheHandIsAllAttacks()
    {
        if (Data == null) return;
        Combat mixed = Hand("CLASH,DEFEND_IRONCLAD");
        Assert.False(mixed.CanPlay(mixed.Hand.Single(x => x.Id == "CLASH")));
        Combat attacks = Hand("CLASH,STRIKE_IRONCLAD");
        Assert.True(attacks.CanPlay(attacks.Hand.Single(x => x.Id == "CLASH")));
    }

    [Fact]
    public void GoldAxeAndMindBlastAndStackAndRendCount()
    {
        if (Data == null) return;
        Combat axe = Hand("STRIKE_IRONCLAD,GOLD_AXE"); Play(axe, "STRIKE_IRONCLAD"); int hp = axe.Enemies[0].Hp; Play(axe, "GOLD_AXE");
        Assert.True(hp - axe.Enemies[0].Hp >= 1);
        Combat blast = Hand("MIND_BLAST", deck: "DEFEND_IRONCLAD*30"); int pile = blast.DrawPile.Count; Play(blast, "MIND_BLAST");
        Assert.Equal(500 - pile, blast.Enemies[0].Hp);
        Combat stack = Hand("STACK"); stack.DiscardPile.AddRange(Data.ParseDeck("STRIKE_IRONCLAD*4").Select(x => x.Instantiate())); Play(stack, "STACK", -1);
        Assert.Equal(4, stack.Block);
        Combat rend = Hand("REND"); rend.Enemies[0].Powers[(int)PowerKind.Weak] = 1; rend.Enemies[0].Powers[(int)PowerKind.Vulnerable] = 1; Play(rend, "REND");
        Assert.Equal(500 - 30, rend.Enemies[0].Hp);
    }

    [Fact]
    public void DrawingCardsFromPilesAndTheHand()
    {
        if (Data == null) return;
        Combat mos = Hand("MASTER_OF_STRATEGY"); Play(mos, "MASTER_OF_STRATEGY", -1);
        Assert.Equal(3, mos.Hand.Count);
        Combat impatience = Hand("IMPATIENCE,DEFEND_IRONCLAD"); Play(impatience, "IMPATIENCE", -1);
        Assert.Equal(3, impatience.Hand.Count);
        Combat restless = Hand("RESTLESSNESS"); Play(restless, "RESTLESSNESS", -1);
        Assert.Equal(2, restless.Hand.Count);
        Assert.Equal(5, restless.Energy);
        Combat scrawl = Hand("SCRAWL"); Play(scrawl, "SCRAWL", -1);
        Assert.Equal(10, scrawl.Hand.Count);
    }

    [Fact]
    public void SecretTechniqueAndWeaponAndWishAndSeekerPullFromTheDrawPile()
    {
        if (Data == null) return;
        Combat tech = Hand("SECRET_TECHNIQUE", deck: "DEFEND_IRONCLAD*10,STRIKE_IRONCLAD*10"); Play(tech, "SECRET_TECHNIQUE", -1);
        Assert.Contains(tech.Hand, x => x.Kind == CardKind.Skill);
        Combat weapon = Hand("SECRET_WEAPON", deck: "DEFEND_IRONCLAD*10,STRIKE_IRONCLAD*10"); Play(weapon, "SECRET_WEAPON", -1);
        Assert.Contains(weapon.Hand, x => x.Kind == CardKind.Attack);
        Combat wish = Hand("WISH", deck: "BASH*10"); Play(wish, "WISH", -1);
        Assert.Single(wish.Hand);
        Combat seeker = Hand("SEEKER_STRIKE", deck: "BASH*10"); Play(seeker, "SEEKER_STRIKE");
        Assert.Single(seeker.Hand);
    }

    [Fact]
    public void TheColorlessPowersDoTheirThing()
    {
        if (Data == null) return;
        Combat calamity = Hand("CALAMITY,STRIKE_IRONCLAD", relics: new[] { RelicKind.Ectoplasm, RelicKind.Lantern });
        Play(calamity, "CALAMITY", -1); Play(calamity, "STRIKE_IRONCLAD");
        Assert.Contains(calamity.Hand, x => x.Kind == CardKind.Attack);
        Combat prep = Hand("PREP_TIME"); Play(prep, "PREP_TIME", -1); prep.EndPlayerTurn();
        Assert.Equal(4, prep.PlayerPowers[(int)PowerKind.Vigor]);
        Combat boulder = Hand("ROLLING_BOULDER"); Play(boulder, "ROLLING_BOULDER", -1); boulder.EndPlayerTurn();
        Assert.Equal(495, boulder.Enemies[0].Hp);
        boulder.EndPlayerTurn();
        Assert.Equal(485, boulder.Enemies[0].Hp);
        Combat fasten = Hand("FASTEN,DEFEND_IRONCLAD"); Play(fasten, "FASTEN", -1); Play(fasten, "DEFEND_IRONCLAD", -1);
        Assert.Equal(9, fasten.Block);
        Combat prowess = Hand("PROWESS"); Play(prowess, "PROWESS", -1);
        Assert.Equal(1, prowess.PlayerPowers[(int)PowerKind.Strength]);
        Assert.Equal(1, prowess.PlayerPowers[(int)PowerKind.Dexterity]);
        Combat armor = Hand("ETERNAL_ARMOR", relics: new[] { RelicKind.Lantern, RelicKind.Ectoplasm }); Play(armor, "ETERNAL_ARMOR", -1);
        Assert.Equal(9, armor.PlayerPowers[(int)PowerKind.Plating]);
    }

    [Fact]
    public void PanacheHitsEveryEnemyAfterFiveMoreCardsInATurn()
    {
        if (Data == null) return;
        Combat c = Hand("PANACHE,DEFEND_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD", relics: new[] { RelicKind.Ectoplasm, RelicKind.Lantern, RelicKind.VeryHotCocoa });
        Play(c, "PANACHE", -1);
        for (int i = 0; i < 5; i++) Play(c, "DEFEND_IRONCLAD", -1);
        Assert.Equal(490, c.Enemies[0].Hp);
    }

    [Fact]
    public void TheBombExplodesAfterThreeTurns()
    {
        if (Data == null) return;
        Combat c = Hand("THE_BOMB"); Play(c, "THE_BOMB", -1);
        c.EndPlayerTurn(); c.EndPlayerTurn();
        Assert.Equal(500, c.Enemies[0].Hp);
        c.EndPlayerTurn();
        Assert.Equal(460, c.Enemies[0].Hp);
    }

    [Fact]
    public void EquilibriumAndSalvoKeepTheHandForOneTurn()
    {
        if (Data == null) return;
        Combat c = Hand("EQUILIBRIUM,STRIKE_IRONCLAD"); Play(c, "EQUILIBRIUM", -1);
        Assert.Equal(13, c.Block);
        c.EndPlayerTurn(startNextTurn: false);
        Assert.Contains(c.Hand, x => x.Id == "STRIKE_IRONCLAD");
    }

    [Fact]
    public void PanicButtonBlocksThirtyThenStopsCardBlockForTwoTurns()
    {
        if (Data == null) return;
        Combat c = Hand("PANIC_BUTTON,DEFEND_IRONCLAD"); Play(c, "PANIC_BUTTON", -1);
        Assert.Equal(30, c.Block);
        Play(c, "DEFEND_IRONCLAD", -1);
        Assert.Equal(30, c.Block);
    }

    [Fact]
    public void TheGambitBlocksFiftyAndKillsOnUnblockedDamage()
    {
        if (Data == null) return;
        Combat safe = Hand("THE_GAMBIT", monsters: new[] { Dummy(damage: 10) }); Play(safe, "THE_GAMBIT", -1);
        safe.EndPlayerTurn();
        Assert.Equal(CombatResult.Ongoing, safe.Result);
        Combat dead = Hand("THE_GAMBIT", monsters: new[] { Dummy(damage: 100) }); Play(dead, "THE_GAMBIT", -1);
        dead.EndPlayerTurn();
        Assert.Equal(CombatResult.Lost, dead.Result);
    }

    [Fact]
    public void ApparitionMakesTheNextAttackDoOneDamage()
    {
        if (Data == null) return;
        Combat c = Hand("APPARITION", monsters: new[] { Dummy(damage: 30) }); Play(c, "APPARITION", -1);
        c.EndPlayerTurn();
        Assert.Equal(79, c.Hp);
        Assert.Contains(c.ExhaustPile.Concat(c.DiscardPile), x => x.Id == "APPARITION");
    }

    [Fact]
    public void ApotheosisUpgradesEverything()
    {
        if (Data == null) return;
        Combat c = Hand("APOTHEOSIS,STRIKE_IRONCLAD", deck: "STRIKE_IRONCLAD*20"); Play(c, "APOTHEOSIS", -1);
        Assert.All(c.Hand.Concat(c.DrawPile), x => Assert.True(x.Upgraded));
    }

    [Fact]
    public void BrightestFlameAndRelaxAndOutmaneuverMoveEnergyAndDraws()
    {
        if (Data == null) return;
        Combat flame = Hand("BRIGHTEST_FLAME"); Play(flame, "BRIGHTEST_FLAME", -1);
        Assert.Equal(5, flame.Energy);
        Assert.Equal(2, flame.Hand.Count);
        Combat relax = Hand("RELAX", relics: new[] { RelicKind.VeryHotCocoa }); Play(relax, "RELAX", -1);
        Assert.Equal(16, relax.Block);
        relax.EndPlayerTurn();
        Assert.Equal(5, relax.Energy);
        Assert.Equal(7, relax.Hand.Count);
        Combat outmaneuver = Hand("OUTMANEUVER"); Play(outmaneuver, "OUTMANEUVER", -1); outmaneuver.EndPlayerTurn();
        Assert.Equal(5, outmaneuver.Energy);
    }

    [Fact]
    public void MaulGrowsForEveryMaulPlayed()
    {
        if (Data == null) return;
        Combat c = Hand("MAUL,MAUL", relics: new[] { RelicKind.Lantern, RelicKind.Ectoplasm });
        Play(c, "MAUL");
        Assert.Equal(500 - 10, c.Enemies[0].Hp);
        Play(c, "MAUL");
        Assert.Equal(500 - 10 - 14, c.Enemies[0].Hp);
    }

    [Fact]
    public void WhistleStunsAndHandOfGreedPaysGoldWhenFatal()
    {
        if (Data == null) return;
        Combat w = Hand("WHISTLE", monsters: new[] { Dummy(damage: 20) }); Play(w, "WHISTLE");
        Assert.Equal(467, w.Enemies[0].Hp);
        w.EndPlayerTurn();
        Assert.Equal(80, w.Hp);
        Combat g = Hand("HAND_OF_GREED", monsters: new[] { Dummy(hp: 10), Dummy() }); Play(g, "HAND_OF_GREED", 0);
        Assert.Equal(20, g.GoldGained);
    }

    [Fact]
    public void GeneratingCardsAddToTheHand()
    {
        if (Data == null) return;
        foreach (string id in new[] { "DISCOVERY", "JACK_OF_ALL_TRADES", "JACKPOT", "DISTRACTION" })
        {
            Combat c = Hand(id); c.Play(0, id == "JACKPOT" ? 0 : -1);
            Assert.True(c.Hand.Count >= 1, id);
        }
    }

    [Fact]
    public void HiddenGemGivesADrawPileCardReplay()
    {
        if (Data == null) return;
        Combat c = Hand("HIDDEN_GEM", deck: "STRIKE_IRONCLAD*30"); Play(c, "HIDDEN_GEM", -1);
        Assert.Contains(c.DrawPile, x => x.Replay == 2);
    }

    [Fact]
    public void CurseEndOfTurnDrawbacksHitWhileInHand()
    {
        if (Data == null) return;
        Combat badLuck = Hand("BAD_LUCK"); badLuck.EndPlayerTurn(startNextTurn: false);
        Assert.Equal(67, badLuck.Hp);
        Combat decay = Hand("DECAY"); decay.EndPlayerTurn(startNextTurn: false);
        Assert.Equal(78, decay.Hp);
        Combat regret = Hand("REGRET,WOUND,WOUND"); regret.EndPlayerTurn(startNextTurn: false);
        Assert.Equal(77, regret.Hp);
        Assert.Equal(PowerKind.Weak, Data.Cards.Get("DOUBT", false).EndTurnPower);
        Assert.Equal(PowerKind.Frail, Data.Cards.Get("SHAME", false).EndTurnPower);
        Combat debt = Hand("DEBT"); debt.EndPlayerTurn(startNextTurn: false);
        Assert.Equal(10, debt.GoldSpent);
    }

    [Fact]
    public void NormalityAndEnthralledLimitWhatCanBePlayed()
    {
        if (Data == null) return;
        Combat normality = Hand("NORMALITY,DEFEND_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD", relics: new[] { RelicKind.Lantern, RelicKind.Ectoplasm });
        for (int i = 0; i < 3; i++) Play(normality, "DEFEND_IRONCLAD", -1);
        Assert.False(normality.CanPlay(normality.Hand.First(x => x.Id == "DEFEND_IRONCLAD")));
        Combat enthralled = Hand("ENTHRALLED,DEFEND_IRONCLAD");
        Assert.False(enthralled.CanPlay(enthralled.Hand.First(x => x.Id == "DEFEND_IRONCLAD")));
        Assert.True(enthralled.CanPlay(enthralled.Hand.First(x => x.Id == "ENTHRALLED")));
    }

    [Fact]
    public void StatusCardsBurnVoidAndSlimed()
    {
        if (Data == null) return;
        Combat burn = Hand("BURN,INFECTION,TOXIC,WITHER"); burn.EndPlayerTurn(startNextTurn: false);
        Assert.Equal(80 - 2 - 3 - 5 - 3, burn.Hp);
        Combat slimed = Hand("SLIMED"); Play(slimed, "SLIMED", -1);
        Assert.Single(slimed.Hand);
        Combat voidCard = Fight("VOID*20", potions: ""); voidCard.EndPlayerTurn();
        Assert.True(voidCard.Energy < 3);
    }
}
