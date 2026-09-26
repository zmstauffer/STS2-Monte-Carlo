using SpireMonteCarlo.Codex;

namespace SpireMonteCarlo.Sim;

/// <summary>
/// The relics whose effects the simulator models (rules read from the decompiled relic classes, v0.111; numbers match the
/// game's canonical vars). Relics not listed here are held but do nothing. Combat relics live in Combat.Relics.cs;
/// pickup and between-fight effects (max HP, rest healing, potion slots) are applied by the act rollout.
/// </summary>
public enum RelicKind
{
    Unknown,
    BurningBlood,
    // start of combat
    Anchor, Lantern, BagOfPreparation, BloodVial, BronzeScales, CentennialPuzzle, FestivePopper, Gorget, OddlySmoothStone,
    RedMask, BagOfMarbles, Vajra, Akabeko, Bellows, StoneCracker, Pantograph,
    // by turn number
    HappyFlower, Pendulum, Candelabra, HornCleat, MercuryHourglass, StoneCalendar, CaptainsWheel, Chandelier, SparklingRouge,
    // counting cards played
    Nunchaku, PenNib, Shuriken, Kunai, Kusarigama, OrnamentalFan, LetterOpener, TuningFork, RainbowRing, Pocketwatch, ArtOfWar,
    // playing particular cards
    Permafrost, GamePiece, IntimidatingHelmet, Vambrace, StrikeDummy, MiniatureCannon,
    // end of turn
    Orichalcum, ParryingShield, RippleBasin, CloakClasp, IceCream, SturdyClamp,
    // taking damage and dying
    BeatingRemnant, TungstenRod, LizardTail, DemonTongue, SelfFormingClay, RedSkull, PaperPhrog,
    // other reactions
    JossPaper, UnceasingTop, GremlinHorn, CharonsAshes, RuinedHelmet, MeatOnTheBone,
    // out of combat (applied by the rollout)
    Strawberry, Pear, Mango, PotionBelt, WarPaint, Whetstone, RegalPillow, EternalFeather, Planisphere, FishingRod, StoneHumidifier,
    // Neow boons with lasting effects
    BoomingConch,
    // rare relics that change what the run collects
    MoltenEgg, ToxicEgg, FrozenEgg, OldCoin, PrayerWheel, WhiteStar, Shovel,
    // rare relic acting in combat
    MummifiedHand,
    // shop relics
    Brimstone, BeltBuckle, Bread, BurningSticks, ChemicalX, GhostSeed, RingingTriangle, ScreamingFlagon, SlingOfCourage, TheAbacus, Toolbox,
    // common, uncommon and rare relics that act in combat
    GamblingChip, RazorTooth, UnsettlingLamp, VexingPuzzlebox, PetrifiedToad, ReptileTrinket,
    // Ironclad's upgraded starter relic
    BlackBlood,
    // event relics
    BigMushroom, DaughterOfTheWind, FakeAnchor, FakeBloodVial, FakeHappyFlower, FakeOrichalcum, FakeSneckoEye, FakeStrikeDummy, ForgottenSoul,
    HandDrill, HistoryCourse, LostWisp, MrStruggles, PollinousCore, RoyalPoison, SwordOfJade, TheBoot, ChosenCheese,
    // set by the rollout for one combat when a tea or rest-site relic says "the next combat"
    EmberTeaActive, BoneTeaActive, TeaOfDiscourtesyActive, VenerableTeaSetBonus, FakeVenerableTeaSetBonus,
    // Ancient relics that act in combat
    BlessedAntler, BloodSoakedRose, BrilliantScarf, ChoicesParadox, Crossbow, DelicateFrond, DiamondDiadem, Ectoplasm, Fiddle, IronClub,
    JeweledMask, MusicBox, PaelsBlood, PaelsEye, PaelsFlesh, PaelsTears, PhilosophersStone, PumpkinCandle, RadiantPearl, RunicPyramid, Sai,
    SealOfGold, SneckoEye, Sozu, SpikedGauntlets, ThrowingAxe, ToastyMittens, VelvetChoker, VeryHotCocoa, WhisperingEarring, BiiigHug,
    AlchemicalCoffer, PhialHolster,
    // relics that change what the run collects, spends, or does between fights
    AmethystAubergine, BookOfFiveRings, JuzuBracelet, MealTicket, VenerableTeaSet, BowlerHat, LastingCandy, LuckyFysh, TinyMailbox, Girya, TheCourier,
    WhiteBeastStatue, BlackStar, Driftwood, DingyRug, DragonFruit, LavaLamp, MembershipCard, MiniatureTent, LordsParasol, SilverCrucible, DreamCatcher,
    MawBank, WongosMysteryTicket, SwordOfStone, BingBong, DarkstonePeriapt, FurCoat, PaelsTooth, WarHammer, EmberTea, BoneTea, TeaOfDiscourtesy,
    FakeVenerableTeaSet, TouchOfOrobas,
    // enchantment relics
    MysticLighter, GnarledHammer, Kifuda, PunchDagger, RoyalStamp, WingCharm, Glitter, SilkenTress, FresnelLens, PaelsClaw, PaelsGrowth,
    BeautifulBracelet, ElectricShrymp, NutritiousSoup, TriBoomerang,
    ToyBox, MeatCleaver, PaelsLegion, PaelsWing,
    // set by the rollout for one combat
    GiryaLift1, GiryaLift2, GiryaLift3, FurCoatMarked,
}

public static class RelicRules
{
    private static readonly Dictionary<string, RelicKind> ById =
        Enum.GetValues<RelicKind>().Where(k => k != RelicKind.Unknown).ToDictionary(k => ToSnake(k.ToString()), StringComparer.OrdinalIgnoreCase);

    public static readonly int Count = Enum.GetValues<RelicKind>().Length;

    /// <summary>BURNING_BLOOD, BAG_OF_MARBLES, ... to a kind; Unknown for relics the simulator doesn't model.</summary>
    public static RelicKind Parse(string id) => ById.GetValueOrDefault(id, RelicKind.Unknown);

    /// <summary>Relics that really do nothing in the game (joke items).</summary>
    public static bool IsInert(string id) => id is "FAKE_MERCHANTS_RUG" or "WONGO_CUSTOMER_APPRECIATION_BADGE" or "CIRCLET";

    private static string ToSnake(string pascal)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(pascal[i]));
        }
        return sb.ToString();
    }
}

/// <summary>
/// The relics a character can find as elite and chest rewards: rarity is rolled first (50% common, 33% uncommon, 17% rare, as in
/// RelicFactory), then one of that rarity's relics that the player doesn't own yet. Only the character's own and shared relics are
/// in the pool. Relics the simulator doesn't model are still drawn (and used up) but do nothing.
/// </summary>
public sealed class RelicPool
{
    private readonly string[][] _byRarity;   // 0 common, 1 uncommon, 2 rare
    private readonly string[] _shop;
    private readonly Dictionary<string, int> _price = new();

    public RelicPool(IEnumerable<CodexRelic> relics, string character)
    {
        string own = character.ToLowerInvariant();
        var all = relics.Where(r => r.Pool == "shared" || r.Pool == own).ToList();
        _byRarity = new[] { "Common Relic", "Uncommon Relic", "Rare Relic" }
            .Select(rarity => all.Where(r => r.Rarity == rarity).Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray())
            .ToArray();
        _shop = all.Where(r => r.Rarity == "Shop Relic").Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        foreach (CodexRelic r in all)
            if (r.MerchantPrice != null) _price[r.Id] = r.MerchantPrice.Base;
    }

    /// <summary>What the merchant charges for the relic before his random +-15%.</summary>
    public int BasePrice(string id) => _price.GetValueOrDefault(id, 200);

    /// <summary>A relic of the merchant's own "Shop" rarity that the player doesn't own.</summary>
    public string? RollShopRarity(SimRng rng, ICollection<string> owned)
    {
        var free = _shop.Where(id => !owned.Contains(id)).ToList();
        return free.Count == 0 ? null : free[rng.Next(free.Count)];
    }

    /// <summary>A relic of one rarity (0 common, 1 uncommon, 2 rare) the player doesn't own, or null if none is left.</summary>
    public string? RollRarity(int rarity, SimRng rng, ICollection<string> owned)
    {
        var free = _byRarity[rarity].Where(id => !owned.Contains(id)).ToList();
        return free.Count == 0 ? null : free[rng.Next(free.Count)];
    }

    /// <summary>Whether events can trade the relic away: the common, uncommon, rare and shop relics are; starter, event and Ancient relics aren't.</summary>
    public bool IsTradable(string id) => _shop.Contains(id) || _byRarity.Any(pool => pool.Contains(id));

    /// <summary>A relic id the player doesn't own, or null if the rolled rarity has none left.</summary>
    public string? Roll(SimRng rng, ICollection<string> owned)
    {
        double r = rng.NextDouble();
        int rarity = r < 0.5 ? 0 : r < 0.83 ? 1 : 2;
        string[] pool = _byRarity[rarity];
        var free = pool.Where(id => !owned.Contains(id)).ToList();
        return free.Count == 0 ? null : free[rng.Next(free.Count)];
    }
}
