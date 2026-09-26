namespace SpireMonteCarlo.Sim;

/// <summary>Deck and run edits the relic pickups share, written on the event state so events and relics edit the run the same way.</summary>
public static class RunEdits
{
    private static readonly HashSet<string> NotRandomCurses = new() { "ASCENDERS_BANE", "CURSE_OF_THE_BELL", "ENTHRALLED", "GUILTY" };

    /// <summary>Adds a curse to the deck; Darkstone Periapt pays 6 max HP for each one.</summary>
    public static void AddCurse(this EventState st, string id)
    {
        st.AddCard(id);
        if (st.Has("DARKSTONE_PERIAPT")) st.GainMaxHp(6);
    }

    /// <summary>Adds a random curse the game's modifiers could generate (not the special ones tied to a relic).</summary>
    public static void AddRandomCurse(this EventState st, ICollection<string>? already = null)
    {
        var curses = st.Data.CardIdsOfColor("curse").Where(id => !NotRandomCurses.Contains(id) && (already == null || !already.Contains(id))).ToList();
        if (curses.Count == 0) return;
        string pick = curses[st.Rng.Next(curses.Count)];
        already?.Add(pick);
        st.AddCurse(pick);
    }

    public static void RemoveCards(this EventState st, int count)
    {
        for (int i = 0; i < count; i++) st.RemoveWorstCard();
    }

    /// <summary>Transforms every Strike and Defend into random cards of the character's pool (Pandora's Box).</summary>
    public static void TransformAllBasics(this EventState st)
    {
        int count = st.Deck.Count(c => c.Id.StartsWith("STRIKE_") || c.Id.StartsWith("DEFEND_"));
        st.TransformWorst(count);
    }

    /// <summary>The player copies the card they like most (Dolly's Mirror): the best-liked card that isn't a starter.</summary>
    public static void DuplicateBest(this EventState st)
    {
        CardDef? best = null;
        double bestValue = double.MinValue;
        foreach (CardDef c in st.Deck)
        {
            if (c.Kind is CardKind.Curse or CardKind.Status) continue;
            bool basic = c.Id.StartsWith("STRIKE_") || c.Id.StartsWith("DEFEND_");
            double v = st.Pool.Elo(c.Id) - (basic ? 500 : 0);
            if (v > bestValue) { bestValue = v; best = c; }
        }
        if (best != null) st.Deck.Add(best);
    }

    /// <summary>A card reward with a fixed rarity: three cards, the player takes the one they like or none (Glass Eye).</summary>
    public static void TakeFromRarity(this EventState st, CardRarity rarity)
    {
        var offer = new List<string>();
        for (int i = 0; i < 3; i++) if (st.Pool.RollClass(rarity, st.Rng, offer) is { } id) offer.Add(id);
        int pick = PickPolicy.Choose(offer, st.Pool, st.Deck.Count, st.Rng);
        if (pick >= 0) st.AddCard(offer[pick]);
    }

    /// <summary>Three rare cards to choose from, one of which is always taken (Hefty Tablet).</summary>
    public static void TakeRare(this EventState st)
    {
        var offer = new List<string>();
        for (int i = 0; i < 3; i++) if (st.Pool.RollClass(CardRarity.Rare, st.Rng, offer) is { } id) offer.Add(id);
        if (offer.Count > 0) st.AddCard(offer.OrderByDescending(st.Pool.Elo).First());
    }

    /// <summary>Upgrades random cards of the deck (Sand Castle, Yummy Cookie, War Hammer).</summary>
    public static void UpgradeRandomCards(this EventState st, int count) => st.UpgradeRandom(count);

    /// <summary>Transforms cards (the worst ones) and upgrades the results (Astrolabe).</summary>
    public static void TransformAndUpgrade(this EventState st, int count)
    {
        int before = st.Deck.Count;
        for (int i = 0; i < count; i++)
        {
            if (!st.RemoveWorstCard()) break;
            string[] offer = st.Pool.GenerateOffer(RewardKind.Normal, new RarityOdds(st.Ascension, 0f), st.Rng, 1);
            if (offer.Length > 0) st.AddCard(offer[0], upgraded: true);
        }
    }

    /// <summary>Random potions into the free slots.</summary>
    public static void GainPotions(this EventState st, int count)
    {
        for (int i = 0; i < count; i++) st.GainRandomPotion();
    }

    /// <summary>Loses HP in real points, but never kills the player (a relic pickup would not be taken at 1 HP).</summary>
    public static void PayHp(this EventState st, double amount) => st.Hp = Math.Max(1, st.Hp - amount);

    public static void GainRandomRelics(this EventState st, int count)
    {
        for (int i = 0; i < count; i++) st.GainRandomRelic();
    }
}

/// <summary>
/// What each relic does the moment it is picked up (deck edits, max HP, gold, potions, more relics). Numbers come from the decompiled relic
/// classes (v0.111); a player's choices (which card to remove, which to copy) follow the same defaults the events use.
/// </summary>
public static class RelicPickups
{
    private static readonly Dictionary<string, Action<EventState>> Table = new()
    {
        ["BIG_MUSHROOM"] = st => st.GainMaxHp(20),
        ["BYRDPIP"] = st => st.AddCard("BYRD_SWOOP"),
        ["FAKE_LEES_WAFFLE"] = st => st.HealFraction(0.10),
        ["FAKE_MANGO"] = st => st.GainMaxHp(3),
        ["LEES_WAFFLE"] = st => { st.GainMaxHp(7); st.Heal(st.MaxHp); },
        ["FRAGRANT_MUSHROOM"] = st => { st.PayHp(15); st.UpgradeRandomCards(2); },
        ["DOLLYS_MIRROR"] = st => st.DuplicateBest(),
        ["CAULDRON"] = st => st.GainPotions(5),
        ["ORRERY"] = st => { for (int i = 0; i < 5; i++) st.TakeFromOffer(); },
        ["ASTROLABE"] = st => st.TransformAndUpgrade(3),
        ["CLAWS"] = st => { for (int i = 0; i < 6 && st.RemoveWorstCard(); i++) st.AddCard("MAUL"); },
        ["CALLING_BELL"] = st => { st.AddCurse("CURSE_OF_THE_BELL"); st.GainRandomRelics(3); },
        ["CURSED_PEARL"] = st => { st.AddCurse("GREED"); st.GainGold(333); },
        ["DISTINGUISHED_CAPE"] = st =>
        {
            st.LoseMaxHp(9);
            var used = new HashSet<string>();
            st.AddRandomCurse(used); st.AddRandomCurse(used);
            for (int i = 0; i < 3; i++) st.AddCard("APPARITION");
        },
        ["DUSTY_TOME"] = st =>
        {
            var ancient = st.Data.CardIdsOfColor(st.Pool.Character).Where(id => st.Data.Cards.RarityOf(id) == "Ancient").ToList();
            if (ancient.Count > 0) st.AddCard(ancient[st.Rng.Next(ancient.Count)], upgraded: true);
        },
        ["EMPTY_CAGE"] = st => st.RemoveCards(2),
        ["GLASS_EYE"] = st =>
        {
            st.TakeFromRarity(CardRarity.Common); st.TakeFromRarity(CardRarity.Common);
            st.TakeFromRarity(CardRarity.Uncommon); st.TakeFromRarity(CardRarity.Uncommon);
            st.TakeFromRarity(CardRarity.Rare);
        },
        ["HEFTY_TABLET"] = st => { st.TakeRare(); st.AddCurse("INJURY"); },
        ["JEWELRY_BOX"] = st => st.AddCard("APOTHEOSIS"),
        ["LARGE_CAPSULE"] = st =>
        {
            st.GainRandomRelics(2);
            st.AddCard("STRIKE_" + st.Pool.Character.ToUpperInvariant());
            st.AddCard("DEFEND_" + st.Pool.Character.ToUpperInvariant());
        },
        ["LEAFY_POULTICE"] = st => { st.LoseMaxHp(12); st.TransformWorst(2); },
        ["LOOMING_FRUIT"] = st => st.GainMaxHp(31),
        ["NEOWS_BONES"] = st =>
        {
            foreach (string id in NeowBoons.Offer(st.Rng)) NeowBoons.Apply(id, st);
            st.AddRandomCurse();
        },
        ["PAELS_HORN"] = st => { st.AddCard("RELAX"); st.AddCard("RELAX"); },
        ["PANDORAS_BOX"] = st => st.TransformAllBasics(),
        ["PRECARIOUS_SHEARS"] = st => { st.RemoveCards(2); st.PayHp(16); },
        ["PRESERVED_FOG"] = st => { st.RemoveCards(3); st.AddCurse("FOLLY"); },
        ["SAND_CASTLE"] = st => st.UpgradeRandomCards(6),
        ["SERE_TALON"] = st =>
        {
            st.LoseMaxHp(9);
            var used = new HashSet<string>();
            st.AddRandomCurse(used); st.AddRandomCurse(used);
            for (int i = 0; i < 3; i++) st.AddCard("WISH");
        },
        ["SIGNET_RING"] = st => st.GainGold(999),
        ["STORYBOOK"] = st => st.AddCard("BRIGHTEST_FLAME"),
        ["TANXS_WHISTLE"] = st => st.AddCard("WHISTLE"),
        ["YUMMY_COOKIE"] = st => st.UpgradeRandomCards(4),
        ["ARCHAIC_TOOTH"] = st =>
        {
            // The first starter with an ancient upgrade (Ironclad's Bash becomes Break); a deck without one gets Doubt instead.
            int bash = st.Deck.FindIndex(c => c.Id == "BASH");
            if (bash >= 0)
            {
                bool up = st.Deck[bash].Upgraded;
                st.Deck.RemoveAt(bash);
                st.AddCard("BREAK", up);
            }
            else st.AddCurse("DOUBT");
        },
        ["ALCHEMICAL_COFFER"] = st => { st.PotionSlots += 4; st.GainPotions(4); },
    };

    public static bool Has(string relicId) => Table.ContainsKey(relicId) || NeowBoons.Has(relicId);

    /// <summary>Applies the pickup effect of a relic to the run.</summary>
    public static void Apply(string relicId, EventState st)
    {
        if (Table.TryGetValue(relicId, out Action<EventState>? effect)) effect(st);
        else NeowBoons.Apply(relicId, st);
    }
}
