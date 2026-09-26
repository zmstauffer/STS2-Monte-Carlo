using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>Combat cloning, potion rules (from the decompiled potion classes), and the whole-turn planner. Skipped when the local Codex cache isn't built.</summary>
public class PlannerAndPotionTests
{
    private static readonly Lazy<SimData?> Shared = new(() =>
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadCardDefs() == null ? null : new SimData(cache);
    });

    private static SimData? Data => Shared.Value;

    private static MonsterDef Dummy(int hp = 1000, int damage = 0) => new()
    {
        Id = "DUMMY", HpMin = hp, HpMax = hp, HpMinTough = hp, HpMaxTough = hp,
        Moves = { ["HIT"] = new MoveDef { Id = "HIT", Intent = damage > 0 ? "Attack" : "Buff", Damage = damage, DamageDeadly = damage, Hits = 1 } },
        States = { ["HIT_MOVE"] = new StateDef { Id = "HIT_MOVE", Kind = StateKind.Move, MoveId = "HIT", Next = "HIT_MOVE" } },
        InitialState = "HIT_MOVE",
    };

    private static Combat Setup(string hand, string potions = "", int enemyHp = 1000, int enemyDamage = 0, int hp = 80, int stakes = 0)
    {
        SimData data = Data!;
        var c = new Combat(Array.Empty<CardDef>(), hp, 80, new[] { Dummy(enemyHp, enemyDamage) }, ascension: 0, seed: 7, services: data.Services,
            potions: potions.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => PotionLibrary.Find(id)!), stakes: stakes);
        c.Hand.AddRange(data.ParseDeck(hand).Select(x => x.Instantiate()));
        return c;
    }

    [Fact]
    public void ACloneIsIndependentOfTheOriginal()
    {
        if (Data == null) return;
        Combat c = Setup("STRIKE_IRONCLAD,DEFEND_IRONCLAD");
        Combat copy = c.Clone();
        copy.Play(copy.Hand.FindIndex(x => x.Id == "STRIKE_IRONCLAD"), 0);
        Assert.Equal(994, copy.Enemies[0].Hp);
        Assert.Equal(1000, c.Enemies[0].Hp);
        Assert.Equal(2, c.Hand.Count);
        Assert.Equal(3, c.Energy);
        Assert.Equal(2, copy.Energy);
    }

    [Fact]
    public void FirePotionDoesTwentyIgnoringStrengthAndVulnerable()
    {
        if (Data == null) return;
        Combat c = Setup("", "FIRE_POTION");
        c.PlayerPowers[(int)PowerKind.Strength] = 5;
        c.Enemies[0].Powers[(int)PowerKind.Vulnerable] = 2;
        c.UsePotion(0, 0);
        Assert.Equal(980, c.Enemies[0].Hp);
        Assert.Empty(c.Potions);
    }

    [Fact]
    public void BlockPotionGivesTwelveIgnoringDexterityAndFrail()
    {
        if (Data == null) return;
        Combat c = Setup("", "BLOCK_POTION");
        c.PlayerPowers[(int)PowerKind.Dexterity] = 3;
        c.PlayerPowers[(int)PowerKind.Frail] = 2;
        c.UsePotion(0, -1);
        Assert.Equal(12, c.Block);
    }

    [Fact]
    public void PotionsAreNotCardsPlayed()
    {
        if (Data == null) return;
        Combat c = Setup("", "STRENGTH_POTION");
        c.UsePotion(0, -1);
        Assert.Equal(2, c.PlayerPowers[(int)PowerKind.Strength]);
        Assert.Equal(0, c.CardsPlayed);
        Assert.Equal(0, c.CardsPlayedThisTurn);
    }

    [Fact]
    public void SpeedAndFlexPotionsWearOffAtTheEndOfTheTurn()
    {
        if (Data == null) return;
        Combat c = Setup("", "SPEED_POTION,FLEX_POTION");
        c.UsePotion(0, -1);
        c.UsePotion(0, -1);
        Assert.Equal(5, c.PlayerPowers[(int)PowerKind.Dexterity]);
        Assert.Equal(5, c.PlayerPowers[(int)PowerKind.Strength]);
        c.EndPlayerTurn();
        Assert.Equal(0, c.PlayerPowers[(int)PowerKind.Dexterity]);
        Assert.Equal(0, c.PlayerPowers[(int)PowerKind.Strength]);
    }

    [Fact]
    public void BloodPotionHealsTwentyPercentOfMaxHp()
    {
        if (Data == null) return;
        Combat c = Setup("", "BLOOD_POTION", hp: 30);
        c.UsePotion(0, -1);
        Assert.Equal(46, c.Hp);
    }

    [Fact]
    public void FairyInABottleRevivesAt30PercentOnce()
    {
        if (Data == null) return;
        Combat c = Setup("", "FAIRY_IN_A_BOTTLE", enemyDamage: 50, hp: 10);
        c.EndPlayerTurn();
        Assert.Equal(CombatResult.Ongoing, c.Result);
        Assert.Equal(24, c.Hp);
        Assert.Empty(c.Potions);
        // The fairy is gone: the next 50 kills.
        c.EndPlayerTurn();
        Assert.Equal(CombatResult.Lost, c.Result);
    }

    [Fact]
    public void ExplosiveAmpouleHitsEveryEnemy()
    {
        if (Data == null) return;
        SimData data = Data;
        var c = new Combat(Array.Empty<CardDef>(), 80, 80, new[] { Dummy(50), Dummy(50) }, 0, 3, services: data.Services, potions: new[] { PotionLibrary.Find("EXPLOSIVE_AMPOULE")! });
        c.UsePotion(0, -1);
        Assert.All(c.Enemies, e => Assert.Equal(40, e.Hp));
    }

    [Fact]
    public void ThePlannerAppliesVulnerableBeforeItsAttacks()
    {
        if (Data == null) return;
        // 3 energy: Bash (2) + one Strike (1). Bash first means the Strike hits a Vulnerable enemy.
        Combat c = Setup("STRIKE_IRONCLAD,STRIKE_IRONCLAD,BASH");
        List<BotAction> plan = new BasicBot().Plan(c);
        Assert.True(plan.Count >= 2);
        Assert.Equal("BASH", c.Hand[plan[0].Tag - 1].Id);
    }

    [Fact]
    public void ThePlannerBlocksAHitThatWouldKillItEvenWhenAttackingLooksBetter()
    {
        if (Data == null) return;
        // A 20-damage attack is coming at 15 HP and the enemy is far from dead: the energy has to go to Defend, not to Strikes.
        Combat c = Setup("STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD", enemyHp: 300, enemyDamage: 20, hp: 15);
        List<BotAction> plan = new BasicBot().Plan(c);
        Assert.Equal(2, plan.Count(a => c.Hand[a.Tag - 1].Id == "DEFEND_IRONCLAD"));
    }

    [Fact]
    public void ThePlannerAttacksWhenNothingThreatens()
    {
        if (Data == null) return;
        Combat c = Setup("STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD,DEFEND_IRONCLAD,DEFEND_IRONCLAD", enemyHp: 300, enemyDamage: 0);
        List<BotAction> plan = new BasicBot().Plan(c);
        Assert.Equal(3, plan.Count(a => c.Hand[a.Tag - 1].Id == "STRIKE_IRONCLAD"));
    }

    [Fact]
    public void ThePlannerKillsTheDeadlierEnemyFirst()
    {
        if (Data == null) return;
        SimData data = Data;
        // Two enemies with the same HP: the one that hits for 20 should take the Strikes before the one that hits for 2.
        var c = new Combat(Array.Empty<CardDef>(), 80, 80, new[] { Dummy(30, 2), Dummy(30, 20) }, 0, 7, services: data.Services);
        c.Hand.AddRange(data.ParseDeck("STRIKE_IRONCLAD,STRIKE_IRONCLAD,STRIKE_IRONCLAD").Select(x => x.Instantiate()));
        List<BotAction> plan = new BasicBot().Plan(c);
        Assert.NotEmpty(plan);
        Assert.All(plan, a => Assert.Equal(1, a.Target));
    }

    [Fact]
    public void PlanningDoesNotChangeTheRealCombat()
    {
        if (Data == null) return;
        Combat c = Setup("STRIKE_IRONCLAD,DEFEND_IRONCLAD,BASH,STRIKE_IRONCLAD,DEFEND_IRONCLAD", "FIRE_POTION", enemyDamage: 12);
        int hp = c.Enemies[0].Hp, energy = c.Energy, hand = c.Hand.Count, block = c.Block;
        ulong before = c.Rng.NextU64();
        c = Setup("STRIKE_IRONCLAD,DEFEND_IRONCLAD,BASH,STRIKE_IRONCLAD,DEFEND_IRONCLAD", "FIRE_POTION", enemyDamage: 12);
        new BasicBot().Plan(c);
        Assert.Equal(hp, c.Enemies[0].Hp);
        Assert.Equal(energy, c.Energy);
        Assert.Equal(hand, c.Hand.Count);
        Assert.Equal(block, c.Block);
        Assert.Equal(before, c.Rng.NextU64());   // planning must not consume the real fight's luck
    }

    [Fact]
    public void PlanningNeverChangesCardsSittingInTheRealPiles()
    {
        if (Data == null) return;
        SimData data = Data;
        // Havoc plays the top card of the draw pile; Rampage grows its own damage when played. Trying that on a copy must not touch the real card.
        var c = new Combat(Array.Empty<CardDef>(), 80, 80, new[] { Dummy() }, 0, 7, services: data.Services);
        c.Hand.AddRange(data.ParseDeck("HAVOC").Select(x => x.Instantiate()));
        c.DrawPile.AddRange(data.ParseDeck("STRIKE_IRONCLAD,RAMPAGE").Select(x => x.Instantiate()));   // Rampage on top
        CardDef rampage = c.DrawPile[^1];
        int before = rampage.BonusDamage;
        new BasicBot().Plan(c);
        Assert.Equal(before, rampage.BonusDamage);
        Assert.Equal(2, c.DrawPile.Count);
        Assert.Same(rampage, c.DrawPile[^1]);
    }

    [Fact]
    public void ThePlannerSavesAPotionInAnEasyFightButUsesItAgainstABoss()
    {
        if (Data == null) return;
        Combat normal = Setup("", "FIRE_POTION", stakes: 0);
        Assert.Empty(new BasicBot().Plan(normal));
        Combat boss = Setup("", "FIRE_POTION", stakes: 2);
        List<BotAction> plan = new BasicBot().Plan(boss);
        Assert.Single(plan);
        Assert.Equal("FIRE_POTION", plan[0].PotionId);
    }

    [Fact]
    public void ThePlannerUsesAPotionWhenNothingElseStopsALethalHit()
    {
        if (Data == null) return;
        Combat c = Setup("", "BLOCK_POTION", enemyDamage: 40, hp: 30, stakes: 0);
        List<BotAction> plan = new BasicBot().Plan(c);
        Assert.Single(plan);
        Assert.Equal("BLOCK_POTION", plan[0].PotionId);
    }

    [Fact]
    public void PotionRollsFollowTheGamesRarityOdds()
    {
        var rng = new SimRng(99);
        int n = 20000;
        var byRarity = new Dictionary<PotionRarity, int>();
        for (int i = 0; i < n; i++)
        {
            PotionDef roll = PotionLibrary.Roll(rng)!;
            byRarity[roll.Rarity] = byRarity.GetValueOrDefault(roll.Rarity) + 1;
        }
        Assert.InRange((double)byRarity[PotionRarity.Rare] / n, 0.08, 0.12);
        Assert.InRange((double)byRarity[PotionRarity.Uncommon] / n, 0.22, 0.28);
        Assert.InRange((double)byRarity[PotionRarity.Common] / n, 0.62, 0.68);
    }
}
