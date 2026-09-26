namespace SpireMonteCarlo.Sim;

public enum ShopKind { Card, Relic, Potion }

/// <summary>One thing for sale. Cards and relics are ids; a potion is the modelled potion's id.</summary>
public sealed record ShopItem(ShopKind Kind, string Id, int Price, bool OnSale = false);

/// <summary>
/// A merchant's stock, generated the way MerchantInventory does (checked against the decompiled v0.111 classes): five
/// character cards (two Attacks, two Skills, one Power; one of them half price), a colorless uncommon and rare, three relics
/// (two rolled by rarity, one of the merchant's own Shop rarity), three potions, and card removal.
/// </summary>
public static class ShopModel
{
    private static readonly string[] CharacterCardTypes = { "Attack", "Attack", "Skill", "Skill", "Power" };

    /// <summary>Card removal starts at 75 gold and costs 25 more each time (100 and +50 from Ascension 6, "Inflation").</summary>
    public static int RemovalPrice(int ascension, int removalsUsed) => ascension >= 6 ? 100 + 50 * removalsUsed : 75 + 25 * removalsUsed;

    private static int CardBasePrice(CardRarity rarity) => rarity switch { CardRarity.Rare => 150, CardRarity.Uncommon => 75, _ => 50 };

    private static int PotionBasePrice(PotionRarity rarity) => rarity switch { PotionRarity.Rare => 100, PotionRarity.Uncommon => 75, _ => 50 };

    public static List<ShopItem> Generate(RewardPool cards, RelicPool relics, int ascension, ICollection<string> ownedRelics, SimRng rng)
    {
        var items = new List<ShopItem>();
        var taken = new HashSet<string>();
        var odds = new RarityOdds(ascension, 0f);
        int sale = rng.Next(CharacterCardTypes.Length);

        for (int i = 0; i < CharacterCardTypes.Length; i++)
        {
            string? id = cards.RollShopCard(CharacterCardTypes[i], odds, rng, taken);
            if (id == null) continue;
            taken.Add(id);
            int price = (int)Math.Round(CardBasePrice(cards.RarityOf(id)) * (0.95 + 0.1 * rng.NextDouble()));
            if (i == sale) price /= 2;
            items.Add(new ShopItem(ShopKind.Card, id, price, i == sale));
        }
        foreach (CardRarity rarity in new[] { CardRarity.Uncommon, CardRarity.Rare })
        {
            string? id = cards.RollColorless(rarity, rng, taken);
            if (id == null) continue;
            taken.Add(id);
            items.Add(new ShopItem(ShopKind.Card, id, (int)Math.Round(Math.Round(CardBasePrice(rarity) * 1.15) * (0.95 + 0.1 * rng.NextDouble()))));
        }

        var owned = new HashSet<string>(ownedRelics);
        for (int i = 0; i < 3; i++)
        {
            string? id = i < 2 ? relics.Roll(rng, owned) : relics.RollShopRarity(rng, owned);
            if (id == null) continue;
            owned.Add(id);
            items.Add(new ShopItem(ShopKind.Relic, id, (int)Math.Round(relics.BasePrice(id) * (0.85 + 0.3 * rng.NextDouble()))));
        }

        for (int i = 0; i < 3; i++)
        {
            PotionDef? potion = PotionLibrary.Roll(rng);
            if (potion == null) continue;   // unmodelled potions aren't worth simulating buying
            items.Add(new ShopItem(ShopKind.Potion, potion.Id, (int)Math.Round(PotionBasePrice(potion.Rarity) * (0.95 + 0.1 * rng.NextDouble()))));
        }
        return items;
    }
}
