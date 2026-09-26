namespace SpireMonteCarlo.Sim;

/// <summary>
/// Neow's start-of-run boons (the game's Neow event, v0.111): two of the positive relics plus one cursed one are offered, and the player takes one.
/// Each boon's pickup effect is written from the decompiled relic class. Boons with lasting effects (Booming Conch, Fishing Rod, Stone Humidifier)
/// are also <see cref="RelicKind"/>s that the combat engine and the rollout act on. The cursed options are not modelled: a typical player takes a positive one.
/// </summary>
public static class NeowBoons
{
    private sealed record Boon(string Id, double Value, Action<EventState> Apply);

    private static readonly Boon[] All =
    {
        new("ARCANE_SCROLL", 7.0, st => st.AddRandomClassCard(CardRarity.Rare)),
        new("BOOMING_CONCH", 4.0, _ => { }),
        new("FISHING_ROD", 5.0, _ => { }),
        new("GOLDEN_PEARL", 4.5, st => st.GainGold(150)),
        new("KALEIDOSCOPE", 4.5, st => { st.TakeFromOffer(); st.TakeFromOffer(); }),
        new("LEAD_PAPERWEIGHT", 5.0, st => st.TakeColorless(2)),
        new("LOST_COFFER", 6.5, st => { st.TakeFromOffer(); st.GainRandomPotion(); }),
        new("NEOWS_TORMENT", 4.0, st => st.AddCard("NEOWS_FURY")),
        new("NEW_LEAF", 4.5, st => st.TransformWorst(1)),
        new("PHIAL_HOLSTER", 4.5, st => { st.PotionSlots += 1; st.GainRandomPotion(); st.GainRandomPotion(); }),
        new("PRECISE_SCISSORS", 5.5, st => st.RemoveWorstCard()),
        new("SCROLL_BOXES", 6.0, st => st.TakeBundle()),
        new("WINGED_BOOTS", 3.0, _ => { }),
        // One of each pair is added to the offer at random.
        new("LAVA_ROCK", 3.0, _ => { }),
        new("SMALL_CAPSULE", 7.0, st => st.GainRandomRelic()),
        new("NUTRITIOUS_OYSTER", 6.0, st => st.GainMaxHp(11)),
        new("STONE_HUMIDIFIER", 3.5, _ => { }),
        new("NEOWS_TALISMAN", 5.5, st => st.UpgradeBasics()),
        new("POMANDER", 5.0, st => st.UpgradeBest()),
    };

    private static readonly Dictionary<string, Boon> ById = All.ToDictionary(b => b.Id);

    public static bool Has(string relicId) => ById.ContainsKey(relicId);

    /// <summary>The pickup effect of a boon (nothing for one that only acts later).</summary>
    public static void Apply(string relicId, EventState st)
    {
        if (ById.TryGetValue(relicId, out Boon? boon)) boon.Apply(st);
    }

    /// <summary>The two positive boons Neow offers, the way <c>Neow.GenerateInitialOptions</c> builds the list (without the cursed option, which a typical player skips).</summary>
    public static string[] Offer(SimRng rng)
    {
        var list = new List<string>
        {
            "ARCANE_SCROLL", "BOOMING_CONCH", "FISHING_ROD", "GOLDEN_PEARL", "KALEIDOSCOPE", "LEAD_PAPERWEIGHT", "LOST_COFFER", "NEOWS_TORMENT",
            "NEW_LEAF", "PHIAL_HOLSTER", "PRECISE_SCISSORS", "SCROLL_BOXES", "WINGED_BOOTS",
        };
        list.Add(rng.Next(2) == 0 ? "LAVA_ROCK" : "SMALL_CAPSULE");
        list.Add(rng.Next(2) == 0 ? "NUTRITIOUS_OYSTER" : "STONE_HUMIDIFIER");
        list.Add(rng.Next(2) == 0 ? "NEOWS_TALISMAN" : "POMANDER");
        rng.Shuffle(list);
        return list.Take(2).ToArray();
    }

    /// <summary>What a typical player takes: the offered boon they like best, with a little noise between close ones.</summary>
    public static string Choose(IReadOnlyList<string> offer, SimRng rng)
    {
        string best = offer[0];
        double bestScore = double.MinValue;
        foreach (string id in offer)
        {
            double score = ById[id].Value + (rng.NextDouble() - 0.5) * 2.0;
            if (score > bestScore) { bestScore = score; best = id; }
        }
        return best;
    }
}
