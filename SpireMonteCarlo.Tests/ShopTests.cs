using SpireMonteCarlo.Codex;
using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

/// <summary>Shop inventory and prices, checked against the decompiled MerchantInventory and entry classes (v0.111). Skipped when the local Codex cache isn't built.</summary>
public class ShopTests
{
    private static readonly Lazy<SimData?> Shared = new(() =>
    {
        var cache = new CodexCache();
        return cache.ReadMeta() == null || cache.LoadCardDefs() == null ? null : new SimData(cache);
    });

    private static SimData? Data => Shared.Value;

    [Fact]
    public void RemovalStartsAt75AndRisesBy25EachTimeAnd100By50FromAscension6()
    {
        Assert.Equal(75, ShopModel.RemovalPrice(0, 0));
        Assert.Equal(100, ShopModel.RemovalPrice(0, 1));
        Assert.Equal(100, ShopModel.RemovalPrice(6, 0));
        Assert.Equal(200, ShopModel.RemovalPrice(6, 2));
    }

    [Fact]
    public void AShopHasSevenCardsThreeRelicsAndExactlyOneCharacterCardOnSale()
    {
        if (Data == null) return;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            List<ShopItem> stock = ShopModel.Generate(Data.PoolFor("ironclad"), Data.RelicPoolFor("ironclad"), 0, new HashSet<string>(), new SimRng(seed));
            var cards = stock.Where(i => i.Kind == ShopKind.Card).ToList();
            Assert.Equal(7, cards.Count);
            Assert.Equal(cards.Count, cards.Select(c => c.Id).Distinct().Count());
            Assert.Equal(1, cards.Count(c => c.OnSale));
            Assert.Equal(3, stock.Count(i => i.Kind == ShopKind.Relic));
            Assert.InRange(stock.Count(i => i.Kind == ShopKind.Potion), 0, 3);
        }
    }

    [Fact]
    public void CardPricesFollowRarityWithinFivePercentAndSalesAreHalfPrice()
    {
        if (Data == null) return;
        RewardPool pool = Data.PoolFor("ironclad");
        for (ulong seed = 1; seed <= 30; seed++)
        {
            foreach (ShopItem item in ShopModel.Generate(pool, Data.RelicPoolFor("ironclad"), 0, new HashSet<string>(), new SimRng(seed)).Where(i => i.Kind == ShopKind.Card))
            {
                double basePrice = pool.RarityOf(item.Id) switch { CardRarity.Rare => 150, CardRarity.Uncommon => 75, _ => 50 };
                if (pool.IsColorless(item.Id)) basePrice = Math.Round(basePrice * 1.15);
                if (item.OnSale) basePrice /= 2;
                Assert.InRange(item.Price, Math.Floor(basePrice * 0.94), Math.Ceiling(basePrice * 1.06));
            }
        }
    }

    [Fact]
    public void RelicPricesFollowTheirBasePriceWithinFifteenPercentAndNeverOfferOwnedOnes()
    {
        if (Data == null) return;
        RelicPool relics = Data.RelicPoolFor("ironclad");
        var owned = new HashSet<string> { "ANCHOR", "VAJRA", "LANTERN" };
        for (ulong seed = 1; seed <= 30; seed++)
            foreach (ShopItem item in ShopModel.Generate(Data.PoolFor("ironclad"), relics, 0, owned, new SimRng(seed)).Where(i => i.Kind == ShopKind.Relic))
            {
                Assert.DoesNotContain(item.Id, owned);
                Assert.InRange(item.Price, relics.BasePrice(item.Id) * 0.84, relics.BasePrice(item.Id) * 1.16);
            }
    }

    [Fact]
    public void IroncladShopsStillGetAPowerCardEvenThoughItHasNoCommonPowers()
    {
        if (Data == null) return;
        RewardPool pool = Data.PoolFor("ironclad");
        var odds = new RarityOdds(0, 0f);
        var rng = new SimRng(4);
        for (int i = 0; i < 200; i++)
            Assert.NotNull(pool.RollShopCard("Power", odds, rng, new HashSet<string>()));
    }
}
