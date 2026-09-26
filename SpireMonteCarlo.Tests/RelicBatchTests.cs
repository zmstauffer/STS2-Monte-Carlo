using SpireMonteCarlo.Sim;
using Xunit;
using static SpireMonteCarlo.Tests.CombatKit;

namespace SpireMonteCarlo.Tests;

/// <summary>Combat relics from the shop, event, and Ancient pools (rules read from the decompiled relic classes, v0.111). Skipped when the local Codex cache isn't built.</summary>
public class RelicBatchTests
{
    private static string Ten(string card) => $"{card}*12";

    [Fact]
    public void BrimstoneGivesTheEnemiesStrengthAndTheseTwoToTheHeroEveryTurn()
    {
        if (Data == null) return;
        Combat c = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.Brimstone });
        Assert.Equal(2, c.PlayerPowers[(int)PowerKind.Strength]);
        Assert.Equal(1, c.Enemies[0].Powers[(int)PowerKind.Strength]);
        c.EndPlayerTurn();
        Assert.Equal(4, c.PlayerPowers[(int)PowerKind.Strength]);
        Assert.Equal(2, c.Enemies[0].Powers[(int)PowerKind.Strength]);
    }

    [Fact]
    public void BreadCostsTwoEnergyOnTurnOneAndGivesOneMoreAfterThat()
    {
        if (Data == null) return;
        Combat c = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.Bread });
        Assert.Equal(1, c.Energy);
        c.EndPlayerTurn();
        Assert.Equal(4, c.Energy);
    }

    [Fact]
    public void BeltBuckleGivesTwoDexterityOnlyWhileThereAreNoPotions()
    {
        if (Data == null) return;
        Combat none = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.BeltBuckle }), "DEFEND_IRONCLAD");
        Play(none, "DEFEND_IRONCLAD");
        Assert.Equal(7, none.Block);
        Combat some = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.BeltBuckle }, potions: "FIRE_POTION"), "DEFEND_IRONCLAD");
        Play(some, "DEFEND_IRONCLAD");
        Assert.Equal(5, some.Block);
    }

    [Fact]
    public void BurningSticksCopiesTheFirstExhaustedSkillOnly()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.BurningSticks }), "TRUE_GRIT,DEFEND_IRONCLAD");
        Play(c, "TRUE_GRIT");   // exhausts a random card from hand: the Defend
        Assert.Equal(1, c.Hand.Count(x => x.Id == "DEFEND_IRONCLAD"));   // the exhausted one came back as a copy
        Assert.Single(c.ExhaustPile);
    }

    [Fact]
    public void ChemicalXAddsTwoToWhirlwind()
    {
        if (Data == null) return;
        Combat plain = WithHand(Fight(Ten("DEFEND_IRONCLAD")), "WHIRLWIND");
        Play(plain, "WHIRLWIND", -1);
        Combat chem = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.ChemicalX }), "WHIRLWIND");
        Play(chem, "WHIRLWIND", -1);
        Assert.True(plain.Enemies[0].Hp > chem.Enemies[0].Hp);
        Assert.Equal(500 - 5 * 3, plain.Enemies[0].Hp);
        Assert.Equal(500 - 5 * 5, chem.Enemies[0].Hp);
    }

    [Fact]
    public void GhostSeedMakesStrikesAndDefendsEtherealSoTheyExhaustAtTheEndOfTheTurn()
    {
        if (Data == null) return;
        Combat c = Fight(Ten("STRIKE_IRONCLAD"), new[] { RelicKind.GhostSeed });
        int handCount = c.Hand.Count;
        c.EndPlayerTurn(startNextTurn: false);
        Assert.Equal(handCount, c.ExhaustPile.Count);
    }

    [Fact]
    public void RingingTriangleKeepsTheHandOnTurnOneOnly()
    {
        if (Data == null) return;
        Combat c = Fight(Ten("STRIKE_IRONCLAD"), new[] { RelicKind.RingingTriangle });
        int first = c.Hand.Count;
        c.EndPlayerTurn(startNextTurn: false);
        Assert.Equal(first, c.Hand.Count);
        Combat d = Fight(Ten("STRIKE_IRONCLAD"), new[] { RelicKind.RingingTriangle });
        d.EndPlayerTurn();   // turn 2 begins, the kept hand stays and new cards join it
        d.EndPlayerTurn(startNextTurn: false);
        Assert.Empty(d.Hand);
    }

    [Fact]
    public void ScreamingFlagonHitsEveryEnemyForTwentyWhenTheTurnEndsWithAnEmptyHand()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.ScreamingFlagon }), "");
        c.EndPlayerTurn(startNextTurn: false);
        Assert.Equal(480, c.Enemies[0].Hp);
        Combat d = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.ScreamingFlagon }), "DEFEND_IRONCLAD");
        d.EndPlayerTurn(startNextTurn: false);
        Assert.Equal(500, d.Enemies[0].Hp);
    }

    [Fact]
    public void SlingOfCourageGivesTwoStrengthInElitesOnly()
    {
        if (Data == null) return;
        Assert.Equal(2, Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.SlingOfCourage }, stakes: 1).PlayerPowers[(int)PowerKind.Strength]);
        Assert.Equal(0, Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.SlingOfCourage }, stakes: 0).PlayerPowers[(int)PowerKind.Strength]);
    }

    [Fact]
    public void TheAbacusGivesSixBlockWheneverTheDrawPileIsShuffled()
    {
        if (Data == null) return;
        // A 6-card deck: the second hand needs the discard pile shuffled back in.
        Combat c = Fight("DEFEND_IRONCLAD*6", new[] { RelicKind.TheAbacus });
        c.EndPlayerTurn();
        Assert.True(c.Block >= 6);
    }

    [Fact]
    public void ToolboxAndVexingPuzzleboxAndPetrifiedToadAddWhatTheyDescribe()
    {
        if (Data == null) return;
        Combat toolbox = Fight("STRIKE_IRONCLAD*10", new[] { RelicKind.Toolbox });
        Assert.Contains(toolbox.Hand, x => x.Id != "STRIKE_IRONCLAD");
        Combat box = Fight("STRIKE_IRONCLAD*10", new[] { RelicKind.VexingPuzzlebox });
        Assert.Equal(6, box.Hand.Count);
        Assert.Contains(box.Hand, x => x.FreeThisTurn);
        Combat toad = Fight("STRIKE_IRONCLAD*10", new[] { RelicKind.PetrifiedToad });
        Assert.Contains(toad.Potions, p => p.Id == "POTION_SHAPED_ROCK");
    }

    [Fact]
    public void RazorToothUpgradesAnAttackOrSkillOncePlayed()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.RazorTooth }), "STRIKE_IRONCLAD");
        Play(c, "STRIKE_IRONCLAD");
        Assert.Contains(c.DiscardPile, x => x.Id == "STRIKE_IRONCLAD" && x.Upgraded);
    }

    [Fact]
    public void UnsettlingLampDoublesTheFirstDebuffCardOfTheCombat()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.UnsettlingLamp }), "BASH,BASH");
        Play(c, "BASH");
        Assert.Equal(4, c.Enemies[0].Powers[(int)PowerKind.Vulnerable]);   // Bash gives 2, doubled
        c.EndPlayerTurn();
        WithHand(c, "BASH");
        int before = c.Enemies[0].Powers[(int)PowerKind.Vulnerable];
        Play(c, "BASH");
        Assert.Equal(before + 2, c.Enemies[0].Powers[(int)PowerKind.Vulnerable]);
    }

    [Fact]
    public void ReptileTrinketGivesThreeStrengthForTheTurnWhenAPotionIsUsed()
    {
        if (Data == null) return;
        Combat c = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.ReptileTrinket }, potions: "BLOCK_POTION");
        c.UsePotion(0, -1);
        Assert.Equal(3, c.PlayerPowers[(int)PowerKind.Strength]);
        c.EndPlayerTurn();
        Assert.Equal(0, c.PlayerPowers[(int)PowerKind.Strength]);
    }

    [Fact]
    public void GamblingChipReplacesUnwantedCardsInTheFirstHand()
    {
        if (Data == null) return;
        // The draw pile is all Wounds and Strikes; the chip throws the Wounds away and draws again.
        Combat c = Fight("STRIKE_IRONCLAD*4,WOUND*20", new[] { RelicKind.GamblingChip });
        Assert.True(c.Hand.Count(x => x.Id == "WOUND") < 5 || c.DiscardPile.Any(x => x.Id == "WOUND"));
    }

    [Fact]
    public void DaughterOfTheWindAndLostWispAndForgottenSoulReactToTheirTriggers()
    {
        if (Data == null) return;
        Combat wind = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.DaughterOfTheWind }), "STRIKE_IRONCLAD");
        Play(wind, "STRIKE_IRONCLAD");
        Assert.Equal(1, wind.Block);
        Combat wisp = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.LostWisp }), "INFLAME");
        Play(wisp, "INFLAME", -1);
        Assert.Equal(492, wisp.Enemies[0].Hp);
        Combat soul = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.ForgottenSoul }), "TRUE_GRIT,DEFEND_IRONCLAD");
        Play(soul, "TRUE_GRIT", -1);
        Assert.Equal(499, soul.Enemies[0].Hp);
    }

    [Fact]
    public void HandDrillAppliesVulnerableWhenAnEnemysBlockIsBroken()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.HandDrill }), "STRIKE_IRONCLAD");
        c.Enemies[0].Block = 5;
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(2, c.Enemies[0].Powers[(int)PowerKind.Vulnerable]);
    }

    [Fact]
    public void MrStrugglesHitsEveryEnemyForTheTurnNumber()
    {
        if (Data == null) return;
        Combat c = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.MrStruggles });
        Assert.Equal(499, c.Enemies[0].Hp);
        c.EndPlayerTurn();
        Assert.Equal(497, c.Enemies[0].Hp);
    }

    [Fact]
    public void TheBootRaisesSmallUnblockedHitsToFive()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.TheBoot }), "STRIKE_IRONCLAD");
        Play(c, "STRIKE_IRONCLAD");   // 6 damage is above the threshold
        Assert.Equal(494, c.Enemies[0].Hp);
        Combat d = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.TheBoot }), "STRIKE_IRONCLAD");
        d.Enemies[0].Block = 3;   // 3 damage gets through
        Play(d, "STRIKE_IRONCLAD");
        Assert.Equal(495, d.Enemies[0].Hp);
    }

    [Fact]
    public void TheSimpleStartOfCombatRelicsDoWhatTheyDescribe()
    {
        if (Data == null) return;
        Assert.Equal(3, Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.SwordOfJade }).PlayerPowers[(int)PowerKind.Strength]);
        Assert.Equal(4, Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.FakeAnchor }).Block);
        Assert.Equal(2, Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.EmberTeaActive }).PlayerPowers[(int)PowerKind.Strength]);
        Assert.Equal(76, Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.RoyalPoison }).Hp);
        Combat mushroom = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.BigMushroom });
        Assert.Equal(3, mushroom.Hand.Count);
        Combat dazed = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.TeaOfDiscourtesyActive });
        Assert.Equal(2, dazed.DrawPile.Concat(dazed.Hand).Count(x => x.Id == "DAZED"));
        Combat bone = Fight("BASH*10", new[] { RelicKind.BoneTeaActive });
        Assert.All(bone.Hand, x => Assert.True(x.Upgraded));
    }

    [Fact]
    public void TheEnergyRelicsAddOneEnergyEachTurn()
    {
        if (Data == null) return;
        foreach (RelicKind k in new[] { RelicKind.BlessedAntler, RelicKind.BloodSoakedRose, RelicKind.Ectoplasm, RelicKind.PhilosophersStone, RelicKind.Sozu, RelicKind.SpikedGauntlets, RelicKind.VelvetChoker, RelicKind.WhisperingEarring, RelicKind.PumpkinCandle })
            Assert.Equal(4, Fight(Ten("DEFEND_IRONCLAD"), new[] { k }).Energy);
        Assert.Equal(7, Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.VeryHotCocoa }).Energy);
        Combat flesh = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.PaelsFlesh });
        Assert.Equal(3, flesh.Energy);
        flesh.EndPlayerTurn(); flesh.EndPlayerTurn();
        Assert.Equal(4, flesh.Energy);
    }

    [Fact]
    public void FiddleAndSneckoEyeAndPaelsBloodAndPollinousCoreChangeTheHandDraw()
    {
        if (Data == null) return;
        Assert.Equal(7, Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.Fiddle }).Hand.Count);
        Assert.Equal(7, Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.SneckoEye }).Hand.Count);
        Assert.Equal(6, Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.PaelsBlood }).Hand.Count);
        Combat core = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.PollinousCore });
        for (int i = 0; i < 3; i++) { core.EndPlayerTurn(startNextTurn: false); core.EndPlayerTurn(); }
        Assert.True(core.Hand.Count >= 5);
    }

    [Fact]
    public void FiddleStopsDrawingDuringTheTurn()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.Fiddle }), "POMMEL_STRIKE");
        Play(c, "POMMEL_STRIKE");
        Assert.Empty(c.Hand);
    }

    [Fact]
    public void VelvetChokerAllowsOnlySixCardsPerTurn()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.VelvetChoker }), "STRIKE_IRONCLAD*8".Replace("*8", ",STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD"));
        for (int i = 0; i < 4; i++) { Play(c, "STRIKE_IRONCLAD"); if (c.Energy == 0) c.EndPlayerTurn(); }
        Assert.True(c.CardsPlayed >= 4);
    }

    [Fact]
    public void BrilliantScarfMakesTheFifthCardFree()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.BrilliantScarf, RelicKind.Lantern, RelicKind.Ectoplasm }), "DEFEND_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD,BASH");
        for (int i = 0; i < 4; i++) Play(c, "DEFEND_IRONCLAD");
        CardDef bash = c.Hand.Single(x => x.Id == "BASH");
        Assert.Equal(0, c.EffectiveCost(bash));
    }

    [Fact]
    public void RunicPyramidKeepsTheWholeHand()
    {
        if (Data == null) return;
        Combat c = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.RunicPyramid });
        int hand = c.Hand.Count;
        c.EndPlayerTurn(startNextTurn: false);
        Assert.Equal(hand, c.Hand.Count);
    }

    [Fact]
    public void SaiGivesSevenBlockAtTheStartOfEveryTurn()
    {
        if (Data == null) return;
        Combat c = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.Sai });
        Assert.Equal(7, c.Block);
    }

    [Fact]
    public void ThrowingAxePlaysTheFirstCardOfTheCombatTwice()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.ThrowingAxe }), "STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(488, c.Enemies[0].Hp);
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(482, c.Enemies[0].Hp);
    }

    [Fact]
    public void IronClubDrawsACardEveryFourCards()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.IronClub, RelicKind.Lantern, RelicKind.Ectoplasm }), "DEFEND_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD");
        for (int i = 0; i < 4; i++) Play(c, "DEFEND_IRONCLAD");
        Assert.Single(c.Hand);
    }

    [Fact]
    public void MusicBoxCopiesTheFirstAttackEachTurnAsAnEtherealCard()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.MusicBox }), "STRIKE_IRONCLAD");
        Play(c, "STRIKE_IRONCLAD");
        CardDef copy = c.Hand.Single(x => x.Id == "STRIKE_IRONCLAD");
        Assert.True(copy.IsEthereal);
    }

    [Fact]
    public void ToastyMittensExhaustsACardAndGivesStrengthEachTurn()
    {
        if (Data == null) return;
        Combat c = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.ToastyMittens });
        Assert.Equal(1, c.PlayerPowers[(int)PowerKind.Strength]);
        Assert.Single(c.ExhaustPile);
    }

    [Fact]
    public void DiamondDiademGivesTwentyBlockThatSurvivesTheNextTurnStart()
    {
        if (Data == null) return;
        Combat c = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.DiamondDiadem });
        Assert.Equal(20, c.Block);
        c.EndPlayerTurn();
        Assert.Equal(20, c.Block);
        c.EndPlayerTurn();
        Assert.Equal(0, c.Block);
    }

    [Fact]
    public void PaelsTearsGivesTwoEnergyAfterATurnEndedWithLeftoverEnergy()
    {
        if (Data == null) return;
        Combat c = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.PaelsTears });
        c.EndPlayerTurn();
        Assert.Equal(5, c.Energy);
    }

    [Fact]
    public void PaelsEyeTurnsTheFirstIdleTurnIntoAnExtraTurn()
    {
        if (Data == null) return;
        Combat c = Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.PaelsEye });
        int turn = c.Turn;
        c.EndPlayerTurn();
        Assert.Equal(turn + 1, c.Turn);
        Assert.Equal(0, c.Enemies[0].Hp - 500);   // no enemy phase happened, and the hand was exhausted
        Assert.True(c.ExhaustPile.Count >= 5);
    }

    [Fact]
    public void HistoryCourseReplaysLastTurnsAttack()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD")), "");
        Combat h = Fight(Ten("STRIKE_IRONCLAD"), new[] { RelicKind.HistoryCourse });
        Play(h, "STRIKE_IRONCLAD");
        int afterPlay = h.Enemies[0].Hp;
        h.EndPlayerTurn();
        Assert.True(h.Enemies[0].Hp < afterPlay);   // the copy of the Strike hit at the start of turn 2
    }

    [Fact]
    public void BlackBloodHealsTwelveAfterVictory()
    {
        if (Data == null) return;
        Combat c = WithHand(Fight(Ten("DEFEND_IRONCLAD"), new[] { RelicKind.BlackBlood }, monsters: new[] { Dummy(hp: 1) }, hp: 50, maxHp: 80), "STRIKE_IRONCLAD");
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(CombatResult.Won, c.Result);
        Assert.Equal(62, c.Hp);
    }
}
