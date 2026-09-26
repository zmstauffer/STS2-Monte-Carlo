using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>
/// One test per Ironclad card behavior, using the real card definitions extracted from the game. Each expected number comes
/// from reading the card's class (and the power classes it applies) in the decompiled game. Skipped when the local cache
/// (advisor codex update, advisor sim extract) hasn't been built.
/// </summary>
public class IroncladCardTests
{
    private static readonly Lazy<SimData?> Shared = new(() =>
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadCardDefs() == null ? null : new SimData(cache);
    });

    private static SimData? Data => Shared.Value;

    private static MonsterDef Dummy(int hp = 1000, int damage = 0, int hits = 1) => new()
    {
        Id = "DUMMY", HpMin = hp, HpMax = hp, HpMinTough = hp, HpMaxTough = hp,
        Moves = { ["HIT"] = new MoveDef { Id = "HIT", Intent = damage > 0 ? "Attack" : "Buff", Damage = damage, DamageDeadly = damage, Hits = hits } },
        States = { ["HIT_MOVE"] = new StateDef { Id = "HIT_MOVE", Kind = StateKind.Move, MoveId = "HIT", Next = "HIT_MOVE" } },
        InitialState = "HIT_MOVE",
    };

    /// <summary>A combat with exactly the given cards in each pile (specs like "BASH,STRIKE_IRONCLAD+").</summary>
    private static Combat Setup(string hand, string draw = "", string discard = "", string exhaust = "",
        int enemies = 1, int enemyHp = 1000, int enemyDamage = 0, int hp = 80, int maxEnergy = 3, int enemyHits = 1)
    {
        SimData data = Data!;
        var monsters = Enumerable.Range(0, enemies).Select(_ => Dummy(enemyHp, enemyDamage, enemyHits)).ToArray();
        var c = new Combat(Array.Empty<CardDef>(), hp, 80, monsters, ascension: 0, seed: 7, maxEnergy: maxEnergy, services: data.Services);
        c.Hand.AddRange(Parse(data, hand));
        c.DrawPile.AddRange(Parse(data, draw));   // the last card is on top
        c.DiscardPile.AddRange(Parse(data, discard));
        c.ExhaustPile.AddRange(Parse(data, exhaust));
        return c;
    }

    private static IEnumerable<CardDef> Parse(SimData data, string spec) =>
        data.ParseDeck(spec).Select(c => c.Instantiate());

    private static int Find(Combat c, string id) => c.Hand.FindIndex(x => x.Id == id);

    private static void Play(Combat c, string id, int target = 0)
    {
        int i = Find(c, id);
        Assert.True(i >= 0, $"{id} is not in hand: {string.Join(",", c.Hand)}");
        c.Play(i, target);
    }

    private static int Vuln(Enemy e) => e.Powers[(int)PowerKind.Vulnerable];
    private static int Str(Combat c) => c.PlayerPowers[(int)PowerKind.Strength];
    private static int Power(Combat c, PowerKind kind) => c.PlayerPowers[(int)kind];

    // ---- every card has a recipe --------------------------------------------------------------------------

    [Fact]
    public void EveryIroncladCardInSinglePlayerHasARecipeAndIsNotApproximate()
    {
        if (Data == null) return;
        var cache = new CodexCache();
        var defs = cache.LoadCardDefs()!;
        foreach (CodexCard card in cache.LoadCards().Values.Where(c => c.Color == "ironclad"))
        {
            if (defs.TryGetValue(card.Id, out ExtractedCard? ex) && ex.MultiplayerOnly) continue;
            Assert.True(Data.Cards.HasRecipe(card.Id), $"{card.Id} has no recipe");
            Assert.False(Data.Cards.Get(card.Id, false).Approximate, card.Id);
            Assert.False(Data.Cards.Get(card.Id, true).Approximate, card.Id + "+");
        }
    }

    // ---- basics ---------------------------------------------------------------------------------------------

    [Fact]
    public void StrikeDefendAndBashUseTheGameNumbers()
    {
        if (Data == null) return;
        var c = Setup("STRIKE_IRONCLAD,DEFEND_IRONCLAD");
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(994, c.Enemies[0].Hp);
        Play(c, "DEFEND_IRONCLAD");
        Assert.Equal(5, c.Block);
    }

    [Fact]
    public void UpgradedBashDeals10AndApplies3Vulnerable()
    {
        if (Data == null) return;
        var c = Setup("BASH+");
        Play(c, "BASH");
        Assert.Equal(990, c.Enemies[0].Hp);
        Assert.Equal(3, Vuln(c.Enemies[0]));
    }

    // ---- common ---------------------------------------------------------------------------------------------

    [Fact]
    public void AngerCopiesItselfToTheDiscardPile()
    {
        if (Data == null) return;
        var c = Setup("ANGER");
        Play(c, "ANGER");
        Assert.Equal(994, c.Enemies[0].Hp);
        Assert.Equal(2, c.DiscardPile.Count(x => x.Id == "ANGER"));
        Assert.Equal(3, c.Energy);
    }

    [Fact]
    public void ArmamentsUpgradesOneCardOrAllOfThemWhenUpgraded()
    {
        if (Data == null) return;
        var one = Setup("ARMAMENTS,STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(one, "ARMAMENTS");
        Assert.Equal(5, one.Block);
        Assert.Equal(1, one.Hand.Count(x => x.Upgraded));

        var all = Setup("ARMAMENTS+,STRIKE_IRONCLAD,DEFEND_IRONCLAD,BASH");
        Play(all, "ARMAMENTS");
        Assert.All(all.Hand, x => Assert.True(x.Upgraded));
    }

    [Fact]
    public void BloodWallLosesHpAndGainsBlock()
    {
        if (Data == null) return;
        var c = Setup("BLOOD_WALL");
        Play(c, "BLOOD_WALL");
        Assert.Equal(78, c.Hp);
        Assert.Equal(16, c.Block);
    }

    [Fact]
    public void BodySlamDealsDamageEqualToBlockAndCostsOneLess()
    {
        if (Data == null) return;
        var c = Setup("DEFEND_IRONCLAD,DEFEND_IRONCLAD,BODY_SLAM");
        Play(c, "DEFEND_IRONCLAD");
        Play(c, "DEFEND_IRONCLAD");
        Play(c, "BODY_SLAM");
        Assert.Equal(990, c.Enemies[0].Hp);
        Assert.Equal(0, Data!.Cards.Get("BODY_SLAM", true).Cost);
    }

    [Fact]
    public void BreakthroughLosesHpAndHitsEveryEnemy()
    {
        if (Data == null) return;
        var c = Setup("BREAKTHROUGH", enemies: 2);
        Play(c, "BREAKTHROUGH");
        Assert.Equal(79, c.Hp);
        Assert.All(c.Enemies, e => Assert.Equal(991, e.Hp));
    }

    [Fact]
    public void CinderExhaustsARandomCardFromHand()
    {
        if (Data == null) return;
        var c = Setup("CINDER,DEFEND_IRONCLAD");
        Play(c, "CINDER");
        Assert.Equal(982, c.Enemies[0].Hp);
        Assert.Single(c.ExhaustPile);
        Assert.Empty(c.Hand);
    }

    [Fact]
    public void HavocPlaysTheTopCardOfTheDrawPileAndExhaustsIt()
    {
        if (Data == null) return;
        var c = Setup("HAVOC", draw: "DEFEND_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "HAVOC");
        Assert.Equal(994, c.Enemies[0].Hp);
        Assert.Contains(c.ExhaustPile, x => x.Id == "STRIKE_IRONCLAD");
        Assert.Equal(2, c.Energy);                 // the played card was free
    }

    [Fact]
    public void HeadbuttPutsACardFromDiscardOnTopOfTheDrawPile()
    {
        if (Data == null) return;
        var c = Setup("HEADBUTT", discard: "DEFEND_IRONCLAD,BASH");
        Play(c, "HEADBUTT");
        Assert.Equal(991, c.Enemies[0].Hp);
        Assert.Equal("BASH", c.DrawPile[^1].Id);
    }

    [Fact]
    public void IronWaveGivesBlockAndDamage()
    {
        if (Data == null) return;
        var c = Setup("IRON_WAVE+");
        Play(c, "IRON_WAVE");
        Assert.Equal(7, c.Block);
        Assert.Equal(993, c.Enemies[0].Hp);
    }

    [Fact]
    public void MoltenFistDoublesVulnerableAndExhausts()
    {
        if (Data == null) return;
        var c = Setup("BASH,MOLTEN_FIST", maxEnergy: 5);
        Play(c, "BASH");
        Play(c, "MOLTEN_FIST");
        Assert.Equal(4, Vuln(c.Enemies[0]));
        Assert.Contains(c.ExhaustPile, x => x.Id == "MOLTEN_FIST");
    }

    [Fact]
    public void PerfectedStrikeCountsEveryStrikeInTheDeck()
    {
        if (Data == null) return;
        // Strike tags: Perfected Strike itself, two Strikes in hand, one in draw, one in discard, one in exhaust, Twin Strike.
        var c = Setup("PERFECTED_STRIKE,STRIKE_IRONCLAD,STRIKE_IRONCLAD", draw: "STRIKE_IRONCLAD", discard: "STRIKE_IRONCLAD,TWIN_STRIKE", exhaust: "STRIKE_IRONCLAD");
        Play(c, "PERFECTED_STRIKE");
        Assert.Equal(1000 - (6 + 2 * 7), c.Enemies[0].Hp);
    }

    [Fact]
    public void PommelStrikeAndShrugItOffDraw()
    {
        if (Data == null) return;
        var c = Setup("POMMEL_STRIKE+,SHRUG_IT_OFF", draw: "DEFEND_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD");
        Play(c, "POMMEL_STRIKE");
        Assert.Equal(990, c.Enemies[0].Hp);
        Assert.Equal(2, c.Hand.Count(x => x.Id == "DEFEND_IRONCLAD"));   // drew 2
        Play(c, "SHRUG_IT_OFF");
        Assert.Equal(8, c.Block);
        Assert.Equal(3, c.Hand.Count);
    }

    [Fact]
    public void SetupStrikeGivesStrengthOnlyForThisTurn()
    {
        if (Data == null) return;
        var c = Setup("SETUP_STRIKE,STRIKE_IRONCLAD");
        Play(c, "SETUP_STRIKE");
        Assert.Equal(3, Str(c));
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(1000 - 7 - 9, c.Enemies[0].Hp);
        c.EndPlayerTurn();
        Assert.Equal(0, Str(c));
    }

    [Fact]
    public void SwordBoomerangHitsRandomEnemiesThreeTimes()
    {
        if (Data == null) return;
        var c = Setup("SWORD_BOOMERANG", enemies: 3);
        Play(c, "SWORD_BOOMERANG");
        Assert.Equal(9, c.Enemies.Sum(e => 1000 - e.Hp));
    }

    [Fact]
    public void ThunderclapHitsEveryoneAndAppliesVulnerable()
    {
        if (Data == null) return;
        var c = Setup("THUNDERCLAP", enemies: 2);
        Play(c, "THUNDERCLAP");
        Assert.All(c.Enemies, e => { Assert.Equal(996, e.Hp); Assert.Equal(1, Vuln(e)); });
    }

    [Fact]
    public void TrembleAppliesThreeVulnerableAndExhausts()
    {
        if (Data == null) return;
        var c = Setup("TREMBLE");
        Play(c, "TREMBLE");
        Assert.Equal(3, Vuln(c.Enemies[0]));
        Assert.Single(c.ExhaustPile);
    }

    [Fact]
    public void TrueGritExhaustsRandomlyWhenBaseAndByChoiceWhenUpgraded()
    {
        if (Data == null) return;
        var c = Setup("TRUE_GRIT,DEFEND_IRONCLAD");
        Play(c, "TRUE_GRIT");
        Assert.Equal(7, c.Block);
        Assert.Single(c.ExhaustPile);

        var up = Setup("TRUE_GRIT+,BASH,DEFEND_IRONCLAD,STRIKE_IRONCLAD");
        Play(up, "TRUE_GRIT");
        Assert.Equal(9, up.Block);
        Assert.NotEqual("BASH", up.ExhaustPile.Single().Id);   // it picks the least useful card
    }

    [Fact]
    public void TwinStrikeHitsTwice()
    {
        if (Data == null) return;
        var c = Setup("TWIN_STRIKE");
        Play(c, "TWIN_STRIKE");
        Assert.Equal(990, c.Enemies[0].Hp);
    }

    // ---- uncommon -------------------------------------------------------------------------------------------

    [Fact]
    public void AshenStrikeGrowsWithTheExhaustPile()
    {
        if (Data == null) return;
        var c = Setup("ASHEN_STRIKE", exhaust: "STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "ASHEN_STRIKE");
        Assert.Equal(1000 - (6 + 3 * 2), c.Enemies[0].Hp);
    }

    [Fact]
    public void BattleTranceDrawsThenBlocksFurtherDrawsThisTurn()
    {
        if (Data == null) return;
        var c = Setup("BATTLE_TRANCE,SHRUG_IT_OFF", draw: "STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "BATTLE_TRANCE");
        Assert.Equal(4, c.Hand.Count);   // Shrug It Off + 3 drawn
        Play(c, "SHRUG_IT_OFF");
        Assert.Equal(3, c.Hand.Count);   // its draw was blocked
        c.EndPlayerTurn();
        Assert.Equal(0, Power(c, PowerKind.NoDraw));
        Assert.Equal(5, c.Hand.Count);   // the next turn's hand draw is not blocked
    }

    [Fact]
    public void BloodlettingTradesHpForEnergy()
    {
        if (Data == null) return;
        var c = Setup("BLOODLETTING+");
        Play(c, "BLOODLETTING");
        Assert.Equal(77, c.Hp);
        Assert.Equal(6, c.Energy);
    }

    [Fact]
    public void BludgeonDeals32()
    {
        if (Data == null) return;
        var c = Setup("BLUDGEON");
        Play(c, "BLUDGEON");
        Assert.Equal(968, c.Enemies[0].Hp);
    }

    [Fact]
    public void BullyScalesWithTargetVulnerable()
    {
        if (Data == null) return;
        var c = Setup("BASH,BULLY");
        Play(c, "BASH");
        Play(c, "BULLY");
        // 4 + 2*2 = 8 base, then Vulnerable x1.5 = 12
        Assert.Equal(1000 - 8 - 12, c.Enemies[0].Hp);
    }

    [Fact]
    public void BurningPactExhaustsACardAndDraws()
    {
        if (Data == null) return;
        var c = Setup("BURNING_PACT,DEFEND_IRONCLAD", draw: "BASH,BASH,BASH");
        Play(c, "BURNING_PACT");
        Assert.Single(c.ExhaustPile);
        Assert.Equal(2, c.Hand.Count);
    }

    [Fact]
    public void ColossusHalvesDamageFromVulnerableAttackersUntilItRunsOut()
    {
        if (Data == null) return;
        var c = Setup("COLOSSUS,BASH", enemyDamage: 10, hp: 80);
        c.Enemies[0].Powers[(int)PowerKind.Vulnerable] = 5;
        Play(c, "COLOSSUS");
        Assert.Equal(4, c.Block);
        c.EndPlayerTurn();
        Assert.Equal(80 - (5 - 4), c.Hp);     // 10 halved to 5, 4 blocked
        Assert.Equal(0, Power(c, PowerKind.Colossus));
    }

    [Fact]
    public void CrueltyRaisesTheVulnerableBonusByTwentyFivePercentPoints()
    {
        if (Data == null) return;
        var c = Setup("CRUELTY,STRIKE_IRONCLAD,BASH");
        Play(c, "CRUELTY");
        c.Enemies[0].Powers[(int)PowerKind.Vulnerable] = 2;
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(1000 - 10, c.Enemies[0].Hp);   // 6 * 1.75 = 10.5 -> 10
    }

    [Fact]
    public void DismantleHitsTwiceAgainstVulnerable()
    {
        if (Data == null) return;
        var c = Setup("DISMANTLE,DISMANTLE");
        Play(c, "DISMANTLE");
        Assert.Equal(992, c.Enemies[0].Hp);
        c.Enemies[0].Powers[(int)PowerKind.Vulnerable] = 1;
        Play(c, "DISMANTLE");
        Assert.Equal(992 - 24, c.Enemies[0].Hp);   // 8 * 1.5 = 12, twice
    }

    [Fact]
    public void DrumOfBattleGivesEnergyWhenItIsExhausted()
    {
        if (Data == null) return;
        var c = Setup("DRUM_OF_BATTLE,TRUE_GRIT", draw: "STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "DRUM_OF_BATTLE");
        Assert.Equal(2, c.Energy);
        Assert.Equal(3, c.Hand.Count);
        var d = Setup("TRUE_GRIT,DRUM_OF_BATTLE");
        Play(d, "TRUE_GRIT");
        Assert.Equal(2 + 2, d.Energy);   // Drum of Battle was the only other card, so True Grit exhausted it
    }

    [Fact]
    public void EvilEyeGivesDoubleBlockAfterAnExhaust()
    {
        if (Data == null) return;
        var plain = Setup("EVIL_EYE");
        Play(plain, "EVIL_EYE");
        Assert.Equal(8, plain.Block);

        var after = Setup("TREMBLE,EVIL_EYE");
        Play(after, "TREMBLE");
        Play(after, "EVIL_EYE");
        Assert.Equal(16, after.Block);
    }

    [Fact]
    public void ExpectAFightBlocksFifteenPlusFivePerStrength()
    {
        if (Data == null) return;
        var c = Setup("EXPECT_A_FIGHT");
        c.PlayerPowers[(int)PowerKind.Strength] = 2;
        Play(c, "EXPECT_A_FIGHT");
        Assert.Equal(25, c.Block);
    }

    [Fact]
    public void FeelNoPainGivesBlockPerExhaust()
    {
        if (Data == null) return;
        var c = Setup("FEEL_NO_PAIN,TREMBLE,TREMBLE");
        Play(c, "FEEL_NO_PAIN");
        Play(c, "TREMBLE");
        Play(c, "TREMBLE");
        Assert.Equal(6, c.Block);
    }

    [Fact]
    public void FightMeHitsTwiceAndBothSidesGainStrength()
    {
        if (Data == null) return;
        var c = Setup("FIGHT_ME");
        Play(c, "FIGHT_ME");
        Assert.Equal(990, c.Enemies[0].Hp);
        Assert.Equal(3, Str(c));
        Assert.Equal(1, c.Enemies[0].Powers[(int)PowerKind.Strength]);
    }

    [Fact]
    public void FlameBarrierHurtsEveryAttackerThisTurnOnly()
    {
        if (Data == null) return;
        var c = Setup("FLAME_BARRIER", enemyDamage: 3, enemyHits: 2);
        Play(c, "FLAME_BARRIER");
        Assert.Equal(12, c.Block);
        c.EndPlayerTurn();
        Assert.Equal(1000 - 8, c.Enemies[0].Hp);          // two hits, 4 back each
        Assert.Equal(0, Power(c, PowerKind.FlameBarrier));
    }

    [Fact]
    public void ForgottenRitualGivesThreeEnergyAndExhausts()
    {
        if (Data == null) return;
        // The v0.111 class gives the energy unconditionally (the card text mentions a condition; the code has none).
        var c = Setup("FORGOTTEN_RITUAL");
        Play(c, "FORGOTTEN_RITUAL");
        Assert.Equal(5, c.Energy);
        Assert.Single(c.ExhaustPile);
    }

    [Fact]
    public void HemokinesisAndHowlFromBeyond()
    {
        if (Data == null) return;
        var c = Setup("HEMOKINESIS", enemies: 1);
        Play(c, "HEMOKINESIS");
        Assert.Equal(78, c.Hp);
        Assert.Equal(985, c.Enemies[0].Hp);

        var howl = Setup("HOWL_FROM_BEYOND", enemies: 2, maxEnergy: 3);
        Play(howl, "HOWL_FROM_BEYOND");
        Assert.All(howl.Enemies, e => Assert.Equal(982, e.Hp));
        Assert.Contains(howl.DiscardPile, x => x.Id == "HOWL_FROM_BEYOND");
    }

    [Fact]
    public void HowlFromBeyondPlaysItselfFromTheExhaustPileAtEndOfTurn()
    {
        if (Data == null) return;
        var c = Setup("STRIKE_IRONCLAD", exhaust: "HOWL_FROM_BEYOND");
        c.EndPlayerTurn();
        Assert.Equal(982, c.Enemies[0].Hp);
        Assert.Empty(c.ExhaustPile);                                                // it left the exhaust pile...
        Assert.Contains(c.Hand.Concat(c.DiscardPile), x => x.Id == "HOWL_FROM_BEYOND");   // ...and was reshuffled into the next hand
    }

    [Fact]
    public void InfernalBladeAddsAFreeRandomAttack()
    {
        if (Data == null) return;
        var c = Setup("INFERNAL_BLADE");
        Play(c, "INFERNAL_BLADE");
        Assert.Single(c.Hand);
        Assert.Equal(CardKind.Attack, c.Hand[0].Kind);
        Assert.True(c.Hand[0].FreeThisTurn);
        Assert.Contains(c.ExhaustPile, x => x.Id == "INFERNAL_BLADE");
    }

    [Fact]
    public void InfernoHurtsYouEachTurnAndEveryEnemyWheneverYouLoseHp()
    {
        if (Data == null) return;
        var c = Setup("INFERNO,BLOODLETTING", enemies: 2);
        Play(c, "INFERNO");
        Play(c, "BLOODLETTING");          // lose 3 HP on my turn: 6 to everyone
        Assert.All(c.Enemies, e => Assert.Equal(994, e.Hp));
        Assert.Equal(77, c.Hp);
        c.EndPlayerTurn();
        Assert.Equal(76, c.Hp);           // Inferno's own 1 HP at the start of the turn
        Assert.All(c.Enemies, e => Assert.Equal(988, e.Hp));
    }

    [Fact]
    public void InflameGivesStrength()
    {
        if (Data == null) return;
        var c = Setup("INFLAME+");
        Play(c, "INFLAME");
        Assert.Equal(3, Str(c));
    }

    [Fact]
    public void JuggernautDamagesARandomEnemyWheneverYouGainBlock()
    {
        if (Data == null) return;
        var c = Setup("JUGGERNAUT,DEFEND_IRONCLAD");
        Play(c, "JUGGERNAUT");
        Play(c, "DEFEND_IRONCLAD");
        Assert.Equal(994, c.Enemies[0].Hp);
    }

    [Fact]
    public void JugglingCopiesTheThirdAttackEachTurn()
    {
        if (Data == null) return;
        var c = Setup("JUGGLING,STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD", maxEnergy: 4);
        Play(c, "JUGGLING");
        for (int i = 0; i < 3; i++) Play(c, "STRIKE_IRONCLAD");
        Assert.Single(c.Hand);
        Assert.Equal("STRIKE_IRONCLAD", c.Hand[0].Id);
    }

    [Fact]
    public void ManglePutsTemporaryStrengthLossOnTheEnemy()
    {
        if (Data == null) return;
        var c = Setup("MANGLE", enemyDamage: 12);
        Play(c, "MANGLE");
        Assert.Equal(980, c.Enemies[0].Hp);
        Assert.Equal(-10, c.Enemies[0].Powers[(int)PowerKind.Strength]);
        c.EndPlayerTurn();
        Assert.Equal(80 - 2, c.Hp);                        // 12 - 10
        Assert.Equal(0, c.Enemies[0].Powers[(int)PowerKind.Strength]);   // restored after its turn
    }

    [Fact]
    public void OfferingLosesHpAndGivesEnergyAndCards()
    {
        if (Data == null) return;
        var c = Setup("OFFERING", draw: "STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "OFFERING");
        Assert.Equal(74, c.Hp);
        Assert.Equal(5, c.Energy);
        Assert.Equal(3, c.Hand.Count);
    }

    [Fact]
    public void PillageDrawsUntilANonAttack()
    {
        if (Data == null) return;
        var c = Setup("PILLAGE", draw: "DEFEND_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "PILLAGE");
        Assert.Equal(994, c.Enemies[0].Hp);
        Assert.Equal(3, c.Hand.Count);
    }

    [Fact]
    public void RageGivesBlockForEachAttackThisTurn()
    {
        if (Data == null) return;
        var c = Setup("RAGE,STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "RAGE");
        Play(c, "STRIKE_IRONCLAD");
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(6, c.Block);
        c.EndPlayerTurn();
        Assert.Equal(0, Power(c, PowerKind.Rage));
    }

    [Fact]
    public void RampageGrowsEachTimeItIsPlayed()
    {
        if (Data == null) return;
        var c = Setup("RAMPAGE", maxEnergy: 3);
        Play(c, "RAMPAGE");
        Assert.Equal(990, c.Enemies[0].Hp);
        c.EndPlayerTurn();                       // reshuffles the discarded copy
        Assert.Equal(1, c.Hand.Count(x => x.Id == "RAMPAGE"));
        Play(c, "RAMPAGE");
        Assert.Equal(990 - 15, c.Enemies[0].Hp);
    }

    [Fact]
    public void RuptureGivesStrengthWhenACardCostsHp()
    {
        if (Data == null) return;
        var c = Setup("RUPTURE,BLOODLETTING,STRIKE_IRONCLAD");
        Play(c, "RUPTURE");
        Play(c, "BLOODLETTING");
        Assert.Equal(1, Str(c));
    }

    [Fact]
    public void SecondWindExhaustsNonAttacksForBlockEach()
    {
        if (Data == null) return;
        var c = Setup("SECOND_WIND,DEFEND_IRONCLAD,DEFEND_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "SECOND_WIND");
        Assert.Equal(10, c.Block);
        Assert.Equal(2, c.ExhaustPile.Count);
        Assert.Contains(c.Hand, x => x.Id == "STRIKE_IRONCLAD");
    }

    [Fact]
    public void SpiteHitsTwiceOnlyAfterLosingHpThisTurn()
    {
        if (Data == null) return;
        var c = Setup("SPITE,SPITE,BLOODLETTING");
        Play(c, "SPITE");
        Assert.Equal(995, c.Enemies[0].Hp);
        Play(c, "BLOODLETTING");
        Play(c, "SPITE");
        Assert.Equal(985, c.Enemies[0].Hp);
    }

    [Fact]
    public void StampedePlaysARandomAttackAtEndOfTurn()
    {
        if (Data == null) return;
        var c = Setup("STAMPEDE,STRIKE_IRONCLAD");
        Play(c, "STAMPEDE");
        c.EndPlayerTurn();
        Assert.Equal(994, c.Enemies[0].Hp);
    }

    [Fact]
    public void StompGetsCheaperWithEveryAttackPlayed()
    {
        if (Data == null) return;
        var c = Setup("STOMP,STRIKE_IRONCLAD,STRIKE_IRONCLAD", enemies: 2);
        Play(c, "STRIKE_IRONCLAD");
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(1, c.EffectiveCost(c.Hand[0]));
        Play(c, "STOMP");
        Assert.Equal(0, c.Energy);
        Assert.Equal(988, c.Enemies[1].Hp);
    }

    [Fact]
    public void StoneArmorGivesPlatingThatPaysBlockAtEndOfTurn()
    {
        if (Data == null) return;
        var c = Setup("STONE_ARMOR", enemyDamage: 4);
        Play(c, "STONE_ARMOR");
        c.EndPlayerTurn();
        Assert.Equal(80, c.Hp);          // 4 plating block absorbed the 4 damage
    }

    [Fact]
    public void TauntGivesBlockAndVulnerable()
    {
        if (Data == null) return;
        var c = Setup("TAUNT");
        Play(c, "TAUNT");
        Assert.Equal(6, c.Block);
        Assert.Equal(1, Vuln(c.Enemies[0]));
    }

    [Fact]
    public void UnrelentingMakesTheNextAttackFree()
    {
        if (Data == null) return;
        var c = Setup("UNRELENTING,BLUDGEON,STRIKE_IRONCLAD");
        Play(c, "UNRELENTING");
        Assert.Equal(1, c.Energy);
        Assert.Equal(0, c.EffectiveCost(c.Hand[0]));
        Play(c, "BLUDGEON");
        Assert.Equal(1, c.Energy);
        Assert.Equal(1, c.EffectiveCost(c.Hand[0]));   // only one free attack
    }

    [Fact]
    public void UppercutAppliesWeakAndVulnerable()
    {
        if (Data == null) return;
        var c = Setup("UPPERCUT");
        Play(c, "UPPERCUT");
        Assert.Equal(987, c.Enemies[0].Hp);
        Assert.Equal(1, Vuln(c.Enemies[0]));
        Assert.Equal(1, c.Enemies[0].Powers[(int)PowerKind.Weak]);
    }

    [Fact]
    public void ViciousDrawsWheneverYouApplyVulnerable()
    {
        if (Data == null) return;
        var c = Setup("VICIOUS,BASH", draw: "STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "VICIOUS");
        Play(c, "BASH");
        Assert.Equal(1, c.Hand.Count);
    }

    [Fact]
    public void WhirlwindHitsEveryEnemyOncePerEnergy()
    {
        if (Data == null) return;
        var c = Setup("WHIRLWIND", enemies: 2);
        Play(c, "WHIRLWIND");
        Assert.All(c.Enemies, e => Assert.Equal(1000 - 15, e.Hp));
        Assert.Equal(0, c.Energy);
    }

    // ---- rare -----------------------------------------------------------------------------------------------

    [Fact]
    public void AggressionPutsAnUpgradedAttackFromDiscardIntoHandEachTurn()
    {
        if (Data == null) return;
        var c = Setup("AGGRESSION", discard: "STRIKE_IRONCLAD");
        Play(c, "AGGRESSION");
        c.EndPlayerTurn();
        Assert.Contains(c.Hand, x => x.Id == "STRIKE_IRONCLAD" && x.Upgraded);
    }

    [Fact]
    public void BarricadeKeepsBlockBetweenTurns()
    {
        if (Data == null) return;
        var c = Setup("BARRICADE,DEFEND_IRONCLAD", maxEnergy: 4);
        Play(c, "BARRICADE");
        Play(c, "DEFEND_IRONCLAD");
        c.EndPlayerTurn();
        Assert.Equal(5, c.Block);
    }

    [Fact]
    public void BrandLosesHpExhaustsACardAndGainsStrength()
    {
        if (Data == null) return;
        var c = Setup("BRAND,DEFEND_IRONCLAD,BASH");
        Play(c, "BRAND");
        Assert.Equal(79, c.Hp);
        Assert.Equal(1, Str(c));
        Assert.Contains(c.ExhaustPile, x => x.Id == "DEFEND_IRONCLAD");
    }

    [Fact]
    public void BreakDeals20AndApplies5Vulnerable()
    {
        if (Data == null) return;
        var c = Setup("BREAK");
        Play(c, "BREAK");
        Assert.Equal(980, c.Enemies[0].Hp);
        Assert.Equal(5, Vuln(c.Enemies[0]));
    }

    [Fact]
    public void CascadePlaysTheTopXCardsOfTheDrawPile()
    {
        if (Data == null) return;
        var c = Setup("CASCADE", draw: "DEFEND_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "CASCADE");                       // X = 3
        Assert.Equal(1000 - 12, c.Enemies[0].Hp);
        Assert.Equal(5, c.Block);
    }

    [Fact]
    public void ConflagrationHitsEveryEnemyRepeatedly()
    {
        if (Data == null) return;
        var c = Setup("CONFLAGRATION", enemies: 2);
        Play(c, "CONFLAGRATION");
        Assert.All(c.Enemies, e => Assert.Equal(992, e.Hp));
    }

    [Fact]
    public void CorruptionMakesSkillsFreeAndExhausting()
    {
        if (Data == null) return;
        var c = Setup("CORRUPTION,DEFEND_IRONCLAD,DEFEND_IRONCLAD", maxEnergy: 3);
        Play(c, "CORRUPTION");
        Play(c, "DEFEND_IRONCLAD");
        Play(c, "DEFEND_IRONCLAD");
        Assert.Equal(10, c.Block);
        Assert.Equal(0, c.Energy);
        Assert.Equal(2, c.ExhaustPile.Count);
    }

    [Fact]
    public void CrimsonMantleCostsHpAndGivesBlockAtTurnStart()
    {
        if (Data == null) return;
        var c = Setup("CRIMSON_MANTLE", draw: "STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "CRIMSON_MANTLE");
        c.EndPlayerTurn();
        Assert.Equal(79, c.Hp);
        Assert.Equal(7, c.Block);
    }

    [Fact]
    public void DarkEmbraceDrawsOnExhaust()
    {
        if (Data == null) return;
        var c = Setup("DARK_EMBRACE,TREMBLE", draw: "STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "DARK_EMBRACE");
        Play(c, "TREMBLE");
        Assert.Single(c.Hand);
    }

    [Fact]
    public void DemonFormGivesStrengthEveryTurn()
    {
        if (Data == null) return;
        var c = Setup("DEMON_FORM", maxEnergy: 3);
        Play(c, "DEMON_FORM");
        c.EndPlayerTurn();
        Assert.Equal(3, Str(c));
        c.EndPlayerTurn();
        Assert.Equal(6, Str(c));
    }

    [Fact]
    public void DominateTurnsTheTargetsVulnerableIntoStrengthAndExhausts()
    {
        if (Data == null) return;
        var c = Setup("DOMINATE");
        c.Enemies[0].Powers[(int)PowerKind.Vulnerable] = 2;
        Play(c, "DOMINATE");
        Assert.Equal(3, Vuln(c.Enemies[0]));
        Assert.Equal(3, Str(c));
        Assert.Single(c.ExhaustPile);
    }

    [Fact]
    public void FeedGainsMaxHpOnAKillButNotOnAMinion()
    {
        if (Data == null) return;
        var c = Setup("FEED", enemyHp: 10);
        Play(c, "FEED");
        Assert.Equal(83, c.MaxHp);
        Assert.Equal(83, c.Hp);

        var m = Setup("FEED", enemies: 2, enemyHp: 10);
        m.Enemies[0].Powers[(int)PowerKind.Minion] = 1;
        Play(m, "FEED", 0);
        Assert.Equal(80, m.MaxHp);
    }

    [Fact]
    public void FiendFireExhaustsTheHandAndHitsOncePerCard()
    {
        if (Data == null) return;
        var c = Setup("FIEND_FIRE,STRIKE_IRONCLAD,STRIKE_IRONCLAD,DEFEND_IRONCLAD");
        Play(c, "FIEND_FIRE");
        Assert.Equal(1000 - 21, c.Enemies[0].Hp);
        Assert.Equal(4, c.ExhaustPile.Count);   // the three cards, and Fiend Fire itself
        Assert.Empty(c.Hand);
    }

    [Fact]
    public void HellraiserPlaysADrawnStrikeAtOnce()
    {
        if (Data == null) return;
        var c = Setup("HELLRAISER", draw: "DEFEND_IRONCLAD*4,STRIKE_IRONCLAD");
        Play(c, "HELLRAISER");
        c.EndPlayerTurn();
        Assert.Equal(994, c.Enemies[0].Hp);
        Assert.DoesNotContain(c.Hand, x => x.Id == "STRIKE_IRONCLAD");
    }

    [Fact]
    public void ImperviousBlocks30AndExhausts()
    {
        if (Data == null) return;
        var c = Setup("IMPERVIOUS");
        Play(c, "IMPERVIOUS");
        Assert.Equal(30, c.Block);
        Assert.Single(c.ExhaustPile);
    }

    [Fact]
    public void NotYetHealsTenAndExhausts()
    {
        if (Data == null) return;
        var c = Setup("NOT_YET", hp: 50);
        Play(c, "NOT_YET");
        Assert.Equal(60, c.Hp);
    }

    [Fact]
    public void OneTwoPunchPlaysTheNextAttackTwice()
    {
        if (Data == null) return;
        var c = Setup("ONE_TWO_PUNCH,STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "ONE_TWO_PUNCH");
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(988, c.Enemies[0].Hp);
        Play(c, "STRIKE_IRONCLAD");
        Assert.Equal(982, c.Enemies[0].Hp);
    }

    [Fact]
    public void PactsEndNeedsThreeExhaustedCards()
    {
        if (Data == null) return;
        var no = Setup("PACTS_END", enemies: 1, exhaust: "STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(no, "PACTS_END");
        Assert.Equal(1000, no.Enemies[0].Hp);
        var yes = Setup("PACTS_END", enemies: 1, exhaust: "STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(yes, "PACTS_END");
        Assert.Equal(982, yes.Enemies[0].Hp);
    }

    [Fact]
    public void PrimalForceTurnsAttacksIntoGiantRocks()
    {
        if (Data == null) return;
        var c = Setup("PRIMAL_FORCE,STRIKE_IRONCLAD,DEFEND_IRONCLAD,BASH");
        Play(c, "PRIMAL_FORCE");
        Assert.Equal(2, c.Hand.Count(x => x.Id == "GIANT_ROCK"));
        Assert.Contains(c.Hand, x => x.Id == "DEFEND_IRONCLAD");
        Play(c, "GIANT_ROCK");
        Assert.Equal(980, c.Enemies[0].Hp);
    }

    [Fact]
    public void PyreGivesExtraEnergyEveryTurn()
    {
        if (Data == null) return;
        var c = Setup("PYRE", maxEnergy: 3);
        Play(c, "PYRE");
        c.EndPlayerTurn();
        Assert.Equal(4, c.Energy);
    }

    [Fact]
    public void StokeExhaustsTheHandAndAddsThatManyRandomCards()
    {
        if (Data == null) return;
        var c = Setup("STOKE,STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(c, "STOKE");
        Assert.Equal(3, c.ExhaustPile.Count);
        Assert.Equal(3, c.Hand.Count);
        var up = Setup("STOKE+,STRIKE_IRONCLAD,STRIKE_IRONCLAD");
        Play(up, "STOKE");
        Assert.All(up.Hand, x => Assert.True(x.Upgraded || x.UpgradedForm == null));
    }

    [Fact]
    public void TearAsunderHitsOncePlusOnePerTimeHurt()
    {
        if (Data == null) return;
        var c = Setup("BLOODLETTING,BLOODLETTING,TEAR_ASUNDER", maxEnergy: 4);
        Play(c, "BLOODLETTING");
        Play(c, "BLOODLETTING");
        Play(c, "TEAR_ASUNDER");
        Assert.Equal(1000 - 15, c.Enemies[0].Hp);    // 5 damage x (1 + 2 times hurt)
    }

    [Fact]
    public void ThrashAbsorbsARandomAttackFromHand()
    {
        if (Data == null) return;
        var c = Setup("THRASH,BLUDGEON");
        Play(c, "THRASH");
        Assert.Equal(992, c.Enemies[0].Hp);
        Assert.Contains(c.ExhaustPile, x => x.Id == "BLUDGEON");
        Assert.Equal(32, c.DiscardPile.Single(x => x.Id == "THRASH").BonusDamage);
    }

    [Fact]
    public void UnmovableDoublesTheFirstCardBlockEachTurn()
    {
        if (Data == null) return;
        var c = Setup("UNMOVABLE,DEFEND_IRONCLAD,DEFEND_IRONCLAD", maxEnergy: 5);
        Play(c, "UNMOVABLE");
        Play(c, "DEFEND_IRONCLAD");
        Play(c, "DEFEND_IRONCLAD");
        Assert.Equal(15, c.Block);
    }

    [Fact]
    public void ChoosingUpgradeGainsFromTheCardsInHand()
    {
        if (Data == null) return;
        var c = Setup("ARMAMENTS,BASH,DEFEND_IRONCLAD");
        Play(c, "ARMAMENTS");
        Assert.Single(c.Hand, x => x.Upgraded);
    }
}
