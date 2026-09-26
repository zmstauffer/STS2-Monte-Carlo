namespace SpireMonteCarlo.Sim;

/// <summary>
/// The events of Act 2 (Hive) and Act 3 (Glory) and the shared events the Act 1 file doesn't cover, written from the decompiled event
/// classes (v0.111). Only an event's first page is ever captured in a snapshot, so later pages (Colossal Flower's deeper digs, the Trial's
/// verdict, Tinker Time's rider) are played out inside the option with the choice a typical player makes. Approximations are noted where
/// they are made: the Crystal Sphere minigame, the Battleworn Dummy's three-turn damage race, and the Fake Merchant's custom shop.
/// </summary>
public static partial class EventLibrary
{
    public static readonly string[] Hive =
    {
        "AMALGAMATOR", "BUGSLAYER", "COLORFUL_PHILOSOPHERS", "COLOSSAL_FLOWER", "FIELD_OF_MAN_SIZED_HOLES", "INFESTED_AUTOMATON", "LOST_WISP",
        "SPIRIT_GRAFTER", "THE_LANTERN_KEY", "ZEN_WEAVER",
    };

    public static readonly string[] Glory =
    {
        "BATTLEWORN_DUMMY", "GRAVE_OF_THE_FORGOTTEN", "HUNGRY_FOR_MUSHROOMS", "REFLECTIONS", "ROUND_TEA_PARTY", "TRIAL", "TINKER_TIME",
    };

    // Colorful Philosophers offers cards of up to three other characters, keyed by the pool's energy colour name.
    private static readonly (string Key, string Character)[] OtherColors =
        { ("NECROBINDER", "necrobinder"), ("REGENT", "regent"), ("SILENT", "silent"), ("DEFECT", "defect") };

    private static IEnumerable<EventDef> BuildLater()
    {
        // ---- Act 2: Hive ----
        yield return Ev("AMALGAMATOR", s => new[]
        {
            Opt("COMBINE_STRIKES", x => { x.RemoveBasics("STRIKE_", 2); x.AddCard("ULTIMATE_STRIKE"); }),
            Opt("COMBINE_DEFENDS", x => { x.RemoveBasics("DEFEND_", 2); x.AddCard("ULTIMATE_DEFEND"); }),
        }, s => "COMBINE_STRIKES", allowed: s => s.CountBasics("STRIKE_") >= 2 && s.CountBasics("DEFEND_") >= 2);

        yield return Ev("BUGSLAYER", s => new[]
        {
            Opt("EXTERMINATION", x => x.AddCard("EXTERMINATE")),
            Opt("SQUASH", x => x.AddCard("SQUASH")),
        }, s => "EXTERMINATION");

        // Three card rewards (a common, an uncommon and a rare, three cards each) from another character's pool.
        yield return Ev("COLORFUL_PHILOSOPHERS", s => OtherColors.Select(c => Opt(c.Key, x => x.TakeOtherColorRewards(c.Character))).ToArray(),
            s => OtherColors[s.Rng.Next(OtherColors.Length)].Key);

        // Dig for 35 / 75 / 135 gold at 5 / 6 damage per extra dig, or take 7 more damage for Pollinous Core. A typical player keeps
        // digging while the damage leaves them comfortable.
        yield return Ev("COLOSSAL_FLOWER", s => new[]
        {
            Opt("EXTRACT_CURRENT_PRIZE_1", x => x.GainGold(35)),
            Opt("REACH_DEEPER_1", x =>
            {
                x.LoseHp(5);
                if (x.Hp <= 25) { x.GainGold(75); return; }
                x.LoseHp(6);
                if (x.Hp > 30) { x.LoseHp(7); x.GainRelic("POLLINOUS_CORE"); } else x.GainGold(135);
            }),
        }, s => s.Hp > 35 ? "REACH_DEEPER_1" : "EXTRACT_CURRENT_PRIZE_1", allowed: s => s.Hp >= 19);

        yield return Ev("FIELD_OF_MAN_SIZED_HOLES", s => new[]
        {
            Opt("RESIST", x => { x.RemoveWorstCard(); x.RemoveWorstCard(); x.AddCard("NORMALITY"); }),
            Opt("ENTER_YOUR_HOLE", x => x.EnchantCards(Enchant.PerfectFit, 1)),
        }, s => "ENTER_YOUR_HOLE");

        yield return Ev("INFESTED_AUTOMATON", s => new[]
        {
            Opt("STUDY", x => x.AddRandomClassCard(id => x.Data.Cards.Get(id, false).Kind == CardKind.Power, defaultOdds: true)),
            Opt("TOUCH_CORE", x => x.AddRandomClassCard(id => x.Data.Cards.Get(id, false).Cost == 0, defaultOdds: false)),
        }, s => "STUDY");

        yield return Ev("LOST_WISP", s => new[]
        {
            Opt("CLAIM", x => { x.AddCard("DECAY"); x.GainRelic("LOST_WISP"); }),
            Opt("SEARCH", x => x.GainGold(60 + x.Rng.NextInclusive(-15, 15))),
        }, s => "CLAIM");

        yield return Ev("SPIRIT_GRAFTER", s => new[]
        {
            Opt("LET_IT_IN", x => { x.Heal(25); x.AddCard("METAMORPHOSIS"); }),
            Opt("REJECTION", x => { x.UpgradeBest(); x.LoseHp(10); }),
        }, s => s.HpFraction < 0.5 ? "LET_IT_IN" : "REJECTION");

        // Keeping the key means fighting the Mysterious Knight for the Lantern Key quest card (turned in at War Historian Repy, which
        // the rollout never reaches).
        yield return Ev("THE_LANTERN_KEY", s => new[]
        {
            Opt("RETURN_THE_KEY", x => x.GainGold(100)),
            Opt("KEEP_THE_KEY", x => { x.Fight("MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER"); x.AddCard("LANTERN_KEY"); x.Notes.Add("The Lantern Key's reward at War Historian Repy isn't modelled."); }),
        }, s => "RETURN_THE_KEY");

        yield return Ev("ZEN_WEAVER", s => new[]
        {
            Opt("BREATHING_TECHNIQUES", x => { x.LoseGold(50); x.AddCard("ENLIGHTENMENT"); x.AddCard("ENLIGHTENMENT"); }),
            Opt("EMOTIONAL_AWARENESS", x => { x.LoseGold(125); x.RemoveWorstCard(); }, enabled: s.Gold >= 125),
            Opt("ARACHNID_ACUPUNCTURE", x => { x.LoseGold(250); x.RemoveWorstCard(); x.RemoveWorstCard(); }, enabled: s.Gold >= 250),
        }, s => "EMOTIONAL_AWARENESS", allowed: s => s.Gold >= 125);

        // ---- Act 3: Glory ----
        // A dummy that does nothing and escapes after three turns: beat 75 / 150 / 300 HP in time for a potion / two random upgrades / a relic.
        yield return Ev("BATTLEWORN_DUMMY", s => new[]
        {
            Opt("SETTING_1", x => { if (x.BeatsDummy(75)) x.GainRandomPotion(); }),
            Opt("SETTING_2", x => { if (x.BeatsDummy(150)) x.UpgradeRandom(2); }),
            Opt("SETTING_3", x => { if (x.BeatsDummy(300)) x.GainRandomRelic(); }),
        }, s => s.ThreeTurnDamage() >= 330 ? "SETTING_3" : s.ThreeTurnDamage() >= 165 ? "SETTING_2" : "SETTING_1");

        yield return Ev("GRAVE_OF_THE_FORGOTTEN", s => new[]
        {
            Opt("CONFRONT", x => { x.AddCard("DECAY"); x.EnchantCards(Enchant.SoulsPower, 1); }),
            Opt("ACCEPT", x => x.GainRelic("FORGOTTEN_SOUL")),
        }, s => "ACCEPT");

        // Fragrant Mushroom's 15 HP and two upgrades come with the relic's pickup (RelicPickups).
        yield return Ev("HUNGRY_FOR_MUSHROOMS", s => new[]
        {
            Opt("BIG_MUSHROOM", x => x.GainRelic("BIG_MUSHROOM")),
            Opt("FRAGRANT_MUSHROOM", x => x.GainRelic("FRAGRANT_MUSHROOM")),
        }, s => "BIG_MUSHROOM");

        yield return Ev("REFLECTIONS", s => new[]
        {
            Opt("TOUCH_A_MIRROR", x => { x.DowngradeRandom(2); x.UpgradeRandom(4); }),
            Opt("SHATTER", x => { foreach (CardDef c in x.Deck.ToList()) x.Deck.Add(c); x.AddCard("BAD_LUCK"); }),
        }, s => "TOUCH_A_MIRROR");

        yield return Ev("ROUND_TEA_PARTY", s => new[]
        {
            Opt("ENJOY_TEA", x => { x.GainRelic("ROYAL_POISON"); x.Heal(x.MaxHp); }),
            Opt("PICK_FIGHT", x => { x.LoseHp(11); x.GainRandomRelic(); }),
        }, s => s.HpFraction < 0.5 ? "ENJOY_TEA" : "PICK_FIGHT", allowed: s => s.Hp >= 12);

        // Accepting draws one of three cases, each with a guilty and an innocent verdict; rejecting only offers to accept after all (or to
        // abandon the run), so it plays out the same way.
        yield return Ev("TRIAL", s => new[]
        {
            Opt("ACCEPT", x => x.StandTrial()),
            Opt("REJECT", x => x.StandTrial()),
        }, s => "ACCEPT");

        // One option, then a card type and a rider; Mad Science is modelled as its plain attack.
        yield return Ev("TINKER_TIME", s => new[]
        {
            Opt("CHOOSE_CARD_TYPE", x => { x.AddCard("MAD_SCIENCE"); x.Notes.Add("Mad Science is modelled as a plain attack, whatever type and rider are chosen."); }),
        }, s => "CHOOSE_CARD_TYPE");

        // ---- shared events (Act 2 and 3, some Act 1) ----
        yield return Ev("CRYSTAL_SPHERE", s => new[]
        {
            Opt("UNCOVER_FUTURE", x => { x.LoseGold(50 + x.Rng.NextInclusive(1, 49)); x.Divine(3); }),
            Opt("PAYMENT_PLAN", x => { x.AddCard("DEBT"); x.Divine(6); }),
        }, s => "UNCOVER_FUTURE", allowed: s => s.Act > 1 && s.Gold >= 100);

        yield return Ev("DOLL_ROOM", s => new[]
        {
            Opt("RANDOM", x => x.GainRelic(Dolls[x.Rng.Next(Dolls.Length)])),
            Opt("TAKE_SOME_TIME", x => { x.LoseHp(5); var two = Dolls.ToList(); x.Rng.Shuffle(two); x.GainRelic(BestDoll(two.Take(2))); }),
            Opt("EXAMINE", x => { x.LoseHp(15); x.GainRelic(BestDoll(Dolls)); }),
        }, s => "TAKE_SOME_TIME", allowed: s => s.Act == 2);

        // A custom shop of six of nine fake relics at 50 gold each (the snapshot shows no options); a typical player buys one.
        yield return Ev("FAKE_MERCHANT", s => new[]
        {
            Opt("BUY", x => { string relic = FakeRelics[x.Rng.Next(FakeRelics.Length)]; if (x.Gold >= 50 && !x.Has(relic)) { x.LoseGold(50); x.GainRelic(relic); } }),
            Opt("LEAVE", _ => { }),
        }, s => "BUY", allowed: s => s.Act > 1 && (s.Gold >= 100 || s.Potions.Contains("FOUL_POTION")));

        yield return Ev("POTION_COURIER", s => new[]
        {
            Opt("GRAB_POTIONS", x => { for (int i = 0; i < 3; i++) x.GainPotion("FOUL_POTION"); }),
            Opt("RANSACK", x => { if (PotionLibrary.Roll(x.Rng, PotionRarity.Uncommon) is { } p) x.GainPotion(p.Id); }),
        }, s => "RANSACK", allowed: s => s.Act > 1);

        yield return Ev("RANWID_THE_ELDER", s => new[]
        {
            Opt("POTION", x => { x.Potions.RemoveAt(x.Rng.Next(x.Potions.Count)); x.GainRandomRelic(); }, enabled: s.Potions.Count > 0),
            Opt("GOLD", x => { x.LoseGold(100); x.GainRandomRelic(); }),
            Opt("RELIC", x => { var t = x.TradableRelics(); x.RemoveRelic(t[x.Rng.Next(t.Count)]); x.GainRandomRelic(); x.GainRandomRelic(); }, enabled: s.TradableRelics().Count > 0),
        }, s => "POTION", allowed: s => s.Act > 1 && s.TradableRelics().Count > 0 && s.Gold >= 100 && s.Potions.Count > 0);

        // Three of the player's tradable relics, each offered in exchange for a random new one.
        yield return Ev("RELIC_TRADER", s => new[] { "TOP", "MIDDLE", "BOTTOM" }.Select((key, i) => Opt(key, x =>
        {
            var t = x.TradableRelics();
            x.Rng.Shuffle(t);
            if (i < t.Count) { x.RemoveRelic(t[i]); x.GainRandomRelic(); }
        })).ToArray(), s => "TOP", allowed: s => s.Act > 1 && s.TradableRelics().Count >= 5);

        yield return Ev("SELF_HELP_BOOK", s => new[]
        {
            Opt("READ_THE_BACK", x => x.EnchantBestOfKind(Enchant.Sharp, 2, CardKind.Attack), enabled: s.CanEnchantKind(Enchant.Sharp, CardKind.Attack)),
            Opt("READ_PASSAGE", x => x.EnchantBestOfKind(Enchant.Nimble, 2, CardKind.Skill), enabled: s.CanEnchantKind(Enchant.Nimble, CardKind.Skill)),
            Opt("READ_ENTIRE_BOOK", x => x.EnchantBestOfKind(Enchant.Swift, 2, CardKind.Power), enabled: s.CanEnchantKind(Enchant.Swift, CardKind.Power)),
        }, s => "READ_THE_BACK");

        yield return Ev("STONE_OF_ALL_TIME", s => new[]
        {
            Opt("LIFT", x => { x.Potions.RemoveAt(x.Rng.Next(x.Potions.Count)); x.GainMaxHp(10); }, enabled: s.Potions.Count > 0),
            Opt("PUSH", x => { x.LoseHp(6); x.EnchantCards(Enchant.Vigorous, 8); }),
        }, s => "LIFT", allowed: s => s.Act == 2 && s.Potions.Count > 0);

        yield return Ev("SYMBIOTE", s => new[]
        {
            Opt("APPROACH", x => x.EnchantCards(Enchant.Corrupted, 1)),
            Opt("KILL_WITH_FIRE", x => x.TransformWorst(1)),
        }, s => "KILL_WITH_FIRE", allowed: s => s.Act > 1);

        yield return Ev("TEA_MASTER", s => new[]
        {
            Opt("BONE_TEA", x => { x.LoseGold(50); x.GainRelic("BONE_TEA"); }, enabled: s.Gold >= 50),
            Opt("EMBER_TEA", x => { x.LoseGold(150); x.GainRelic("EMBER_TEA"); }, enabled: s.Gold >= 150),
            Opt("TEA_OF_DISCOURTESY", x => x.GainRelic("TEA_OF_DISCOURTESY")),
        }, s => "BONE_TEA", allowed: s => s.Act < 3 && s.Gold >= 150);

        // One option per potion (the first three), all with the same text key: the advisor numbers repeated keys POTION_0, POTION_1, ...
        yield return Ev("THE_FUTURE_OF_POTIONS", s => Enumerable.Range(0, Math.Min(3, s.Potions.Count))
            .Select(i => Opt($"POTION_{i}", x => x.TradePotionForCards(i))).ToArray(), s => "POTION_0", allowed: s => s.Potions.Count >= 2);

        // The game never draws it from the event list (it follows the Lantern Key quest).
        yield return Ev("WAR_HISTORIAN_REPY", s => Array.Empty<EventOptionDef>(), s => "", allowed: _ => false);

        yield return Ev("WELCOME_TO_WONGOS", s => new[]
        {
            Opt("BARGAIN_BIN", x => { x.LoseGold(100); x.GainRelicOfRarity(0); }, enabled: s.Gold >= 100),
            Opt("FEATURED_ITEM", x => { x.LoseGold(200); x.GainRelicOfRarity(2); }, enabled: s.Gold >= 200),
            Opt("MYSTERY_BOX", x => { x.LoseGold(300); x.GainRelic("WONGOS_MYSTERY_TICKET"); }, enabled: s.Gold >= 300),
            Opt("LEAVE", x => x.DowngradeRandom(1)),
        }, s => s.Gold >= 200 ? "FEATURED_ITEM" : "BARGAIN_BIN", allowed: s => s.Act == 2 && s.Gold >= 100);
    }

    private static readonly string[] Dolls = { "DAUGHTER_OF_THE_WIND", "MR_STRUGGLES", "BING_BONG" };

    private static string BestDoll(IEnumerable<string> offered) =>
        offered.OrderBy(d => Array.IndexOf(DollPreference, d)).First();

    // No ratings exist for the dolls; Mr. Struggles (damage every turn) first, then Daughter of the Wind (block from attacks).
    private static readonly string[] DollPreference = { "MR_STRUGGLES", "DAUGHTER_OF_THE_WIND", "BING_BONG" };

    private static readonly string[] FakeRelics =
    {
        "FAKE_ANCHOR", "FAKE_BLOOD_VIAL", "FAKE_HAPPY_FLOWER", "FAKE_LEES_WAFFLE", "FAKE_MANGO", "FAKE_ORICHALCUM", "FAKE_SNECKO_EYE",
        "FAKE_STRIKE_DUMMY", "FAKE_VENERABLE_TEA_SET",
    };
}

/// <summary>Deck, relic and reward moves the later-act events need.</summary>
public static class LaterEventMoves
{
    public static int CountBasics(this EventState st, string prefix) => st.Deck.Count(c => c.Id.StartsWith(prefix) && c.Rarity == "Basic");

    /// <summary>Removes basic cards whose id starts with <paramref name="prefix"/> (Strikes or Defends), unupgraded ones first.</summary>
    public static void RemoveBasics(this EventState st, string prefix, int count)
    {
        for (int k = 0; k < count; k++)
        {
            int i = st.Deck.FindIndex(c => c.Id.StartsWith(prefix) && c.Rarity == "Basic" && !c.Upgraded);
            if (i < 0) i = st.Deck.FindIndex(c => c.Id.StartsWith(prefix) && c.Rarity == "Basic");
            if (i < 0) return;
            st.Deck.RemoveAt(i);
        }
    }

    /// <summary>
    /// A random card of the character's pool that passes <paramref name="filter"/>: with the usual reward rarity odds (Infested Automaton's
    /// Study) or uniformly over every card that passes (its Touch Core, which skips the rarity roll).
    /// </summary>
    public static void AddRandomClassCard(this EventState st, Func<string, bool> filter, bool defaultOdds)
    {
        var byRarity = Enum.GetValues<CardRarity>().ToDictionary(r => r, r => st.Pool.OfRarity(r).Where(id => st.Data.Cards.Contains(id) && filter(id)).ToList());
        List<string> candidates;
        if (defaultOdds)
        {
            CardRarity rarity = new RarityOdds(st.Ascension, 0f).Roll(RewardKind.Normal, st.Rng);
            candidates = byRarity[rarity].Count > 0 ? byRarity[rarity] : byRarity.Values.SelectMany(v => v).ToList();
        }
        else candidates = byRarity.Values.SelectMany(v => v).ToList();
        if (candidates.Count > 0) st.AddCard(candidates[st.Rng.Next(candidates.Count)]);
    }

    /// <summary>Colorful Philosophers: a common, an uncommon and a rare reward of three cards each from another character's pool.</summary>
    public static void TakeOtherColorRewards(this EventState st, string character)
    {
        RewardPool pool = st.Data.PoolFor(character);
        foreach (CardRarity rarity in Enum.GetValues<CardRarity>())
        {
            var offer = new List<string>();
            for (int i = 0; i < 3; i++)
                if (pool.RollClass(rarity, st.Rng, offer) is { } id) offer.Add(id);
            int pick = PickPolicy.Choose(offer, pool, st.Deck.Count, st.Rng);
            if (pick >= 0) st.AddCard(offer[pick]);
        }
    }

    public static void DowngradeRandom(this EventState st, int count)
    {
        var upgraded = Enumerable.Range(0, st.Deck.Count).Where(i => st.Deck[i].Upgraded).ToList();
        st.Rng.Shuffle(upgraded);
        foreach (int i in upgraded.Take(count)) st.Deck[i] = st.Data.Cards.Get(st.Deck[i].Id, false);
    }

    /// <summary>
    /// A rough count of the damage the deck deals in three turns against a target that never fights back: 5 cards drawn a turn, 3 energy
    /// spent on the attacks with the most damage per energy (listed damage x hits; no Strength, draw or Vulnerable), averaged over 20
    /// shuffles on a fixed seed so the same deck always gets the same estimate. Used by the Battleworn Dummy.
    /// </summary>
    public static double ThreeTurnDamage(this EventState st)
    {
        var attacks = st.Deck.Select(c => (Cost: c.Cost < 0 ? 3 : c.Cost,
            Damage: c.Effects.Where(e => e.Op is EffectOp.Damage or EffectOp.DamageAll or EffectOp.DamageRandom).Sum(e => (double)e.Amount * Math.Max(1, e.Hits)))).ToList();
        var rng = new SimRng(0xD0DD);
        double total = 0;
        const int trials = 20;
        for (int t = 0; t < trials; t++)
        {
            var order = Enumerable.Range(0, attacks.Count).ToList();
            rng.Shuffle(order);
            int next = 0;
            for (int turn = 0; turn < 3; turn++)
            {
                var hand = new List<int>();
                for (int k = 0; k < 5 && attacks.Count > 0; k++)
                {
                    if (next == order.Count) { rng.Shuffle(order); next = 0; }
                    hand.Add(order[next++]);
                }
                int energy = 3;
                foreach (int i in hand.Where(i => attacks[i].Damage > 0).OrderByDescending(i => attacks[i].Damage / Math.Max(attacks[i].Cost, 0.5)))
                    if (attacks[i].Cost <= energy) { energy -= attacks[i].Cost; total += attacks[i].Damage; }
            }
        }
        return total / trials;
    }

    /// <summary>Whether a try at the dummy deals <paramref name="hp"/> damage in three turns (the estimate varies by about a quarter).</summary>
    public static bool BeatsDummy(this EventState st, double hp)
    {
        st.Notes.Add("The Battleworn Dummy's fight is estimated from the deck's damage per energy, not simulated.");
        return st.ThreeTurnDamage() * (0.75 + 0.5 * st.Rng.NextDouble()) >= hp;
    }

    /// <summary>The Trial: one of three cases at random, with the verdict a typical player gives.</summary>
    public static void StandTrial(this EventState st)
    {
        switch (st.Rng.Next(3))
        {
            case 0:   // the merchant: guilty = Regret and two relics
                st.AddCard("REGRET");
                st.GainRandomRelic();
                st.GainRandomRelic();
                break;
            case 1:   // the noble: guilty = heal 10 (innocent = Regret and 300 gold, little use in Act 3)
                st.Heal(10);
                break;
            default:  // the nondescript: guilty = Doubt and two card rewards
                st.AddCard("DOUBT");
                st.TakeFromOffer();
                st.TakeFromOffer();
                break;
        }
    }

    /// <summary>
    /// The Crystal Sphere minigame, roughly: each item is uncovered only when all of its cells are cleared, so small items (gold, common
    /// potions) come out far more often than big ones (the relic covers 4x4). Divinations x 0.6 items are revealed, drawn with weight 1/area.
    /// </summary>
    public static void Divine(this EventState st, int divinations)
    {
        st.Notes.Add("The Crystal Sphere minigame is approximated (items revealed by size, not played).");
        var items = new List<(string Kind, double Weight)>
        {
            ("RELIC", 1 / 16.0), ("POTION", 1 / 3.0), ("POTION", 1 / 3.0), ("POTION", 1 / 4.0),
            ("CARD_COMMON", 1 / 4.0), ("CARD_UNCOMMON", 1 / 4.0), ("CARD_RARE", 1 / 4.0), ("CURSE", 1 / 4.0),
            ("GOLD_10", 1), ("GOLD_10", 1), ("GOLD_10", 1), ("GOLD_10", 1), ("GOLD_10", 1), ("GOLD_30", 0.5), ("GOLD_30", 0.5),
        };
        int reveal = (int)Math.Round(divinations * 0.6);
        for (int k = 0; k < reveal && items.Count > 0; k++)
        {
            double r = st.Rng.NextDouble() * items.Sum(i => i.Weight);
            int pick = 0;
            while (pick < items.Count - 1 && (r -= items[pick].Weight) > 0) pick++;
            string kind = items[pick].Kind;
            items.RemoveAt(pick);
            switch (kind)
            {
                case "RELIC": st.GainRandomRelic(); break;
                case "POTION": st.GainRandomPotion(); break;
                case "CURSE": st.AddCard("DOUBT"); break;
                case "GOLD_10": st.GainGold(10); break;
                case "GOLD_30": st.GainGold(30); break;
                default:
                    var rarity = Enum.Parse<CardRarity>(kind["CARD_".Length..], ignoreCase: true);
                    var offer = new List<string>();
                    for (int i = 0; i < 3; i++) if (st.Pool.RollClass(rarity, st.Rng, offer) is { } id) offer.Add(id);
                    int choice = PickPolicy.Choose(offer, st.Pool, st.Deck.Count, st.Rng);
                    if (choice >= 0) st.AddCard(offer[choice]);
                    break;
            }
        }
    }

    public static void GainPotion(this EventState st, string id)
    {
        if (PotionLibrary.Find(id) != null && st.Potions.Count < st.PotionSlots) st.Potions.Add(id);
    }

    /// <summary>Relics that events can trade away: not the starter relic, not ones whose worth was a pickup effect, not event or Ancient relics.</summary>
    public static List<string> TradableRelics(this EventState st) => st.Relics.Where(r => st.RelicPool.IsTradable(r) && !RelicPickups.Has(r)).ToList();

    public static void RemoveRelic(this EventState st, string id)
    {
        if (st.Relics.Remove(id)) st.RemovedRelics.Add(id);
    }

    /// <summary>A relic of one rarity (0 common, 1 uncommon, 2 rare) the player doesn't own (Wongo's bargain bin and featured item).</summary>
    public static void GainRelicOfRarity(this EventState st, int rarity)
    {
        if (st.RelicPool.RollRarity(rarity, st.Rng, st.Relics) is { } id) st.GainRelic(id);
    }

    public static bool CanEnchantKind(this EventState st, Enchant e, CardKind kind) => st.Deck.Any(c => c.Kind == kind && Enchantments.CanEnchant(c, e));

    /// <summary>Enchants the best-liked card of one kind (Self-Help Book).</summary>
    public static void EnchantBestOfKind(this EventState st, Enchant e, int amount, CardKind kind)
    {
        int best = Enumerable.Range(0, st.Deck.Count).Where(i => st.Deck[i].Kind == kind && Enchantments.CanEnchant(st.Deck[i], e))
            .OrderByDescending(i => st.Pool.Elo(st.Deck[i].Id)).DefaultIfEmpty(-1).First();
        if (best >= 0) st.Deck[best] = Enchantments.Apply(st.Deck[best], e, amount);
    }

    /// <summary>
    /// The Future of Potions: gives up potion <paramref name="index"/> for a reward of three upgraded cards of the matching rarity (common,
    /// uncommon, rare) and a random type (commons never give a Power).
    /// </summary>
    public static void TradePotionForCards(this EventState st, int index)
    {
        if (index >= st.Potions.Count) return;
        PotionDef? potion = PotionLibrary.Find(st.Potions[index]);
        st.Potions.RemoveAt(index);
        CardRarity rarity = potion?.Rarity switch { PotionRarity.Rare => CardRarity.Rare, PotionRarity.Uncommon => CardRarity.Uncommon, _ => CardRarity.Common };
        string[] types = rarity == CardRarity.Common ? new[] { "Attack", "Skill" } : new[] { "Attack", "Skill", "Power" };
        string type = types[st.Rng.Next(types.Length)];
        var offer = new List<string>();
        for (int i = 0; i < 3; i++)
            if (st.Pool.RollTyped(type, rarity, st.Rng, offer) is { } id) offer.Add(id);
        int pick = PickPolicy.Choose(offer, st.Pool, st.Deck.Count, st.Rng);
        if (pick >= 0) st.AddCard(offer[pick], upgraded: st.Data.Cards.Get(offer[pick], false).UpgradedForm != null);
    }
}
