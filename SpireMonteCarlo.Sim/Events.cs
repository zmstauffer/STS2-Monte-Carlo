namespace SpireMonteCarlo.Sim;

/// <summary>A combat an event forces on the player (the game's EnterCombatWithoutExitingEvent), with the rewards it adds.</summary>
public sealed record PendingFight(string Encounter, bool RelicReward, bool PotionReward);

/// <summary>
/// The run as an event sees it and changes it. HP is in real (unscaled) points; the rollout converts. Events only edit this;
/// the rollout copies the results back (relic pickups and forced fights are queued for it to carry out).
/// </summary>
public sealed class EventState
{
    public required SimData Data { get; init; }
    public required RewardPool Pool { get; init; }
    public required RelicPool RelicPool { get; init; }
    public required SimRng Rng { get; init; }
    public required List<CardDef> Deck { get; init; }

    /// <summary>1-based act, the Ascension level, and a rough count of floors climbed so far.</summary>
    public int Act { get; init; } = 1;
    public int Ascension { get; init; }
    public int Floor { get; init; }

    public double Hp { get; set; }
    public double MaxHp { get; set; }
    public int Gold { get; set; }
    public List<string> Relics { get; init; } = new();
    public List<string> Potions { get; init; } = new();
    public int PotionSlots { get; set; } = 3;

    /// <summary>Relics gained (the rollout applies their pickup effects) and combats to fight before moving on.</summary>
    public List<string> PendingRelics { get; } = new();
    public List<PendingFight> PendingFights { get; } = new();

    /// <summary>Effects the simulator doesn't model (enchantments, relics with no effect here), for reports.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>A copy to try an option on: same run, its own deck and lists, and a copy of the random stream.</summary>
    public EventState Copy() => new()
    {
        Data = Data, Pool = Pool, RelicPool = RelicPool, Rng = Rng.Clone(), Deck = Deck.ToList(), Act = Act, Ascension = Ascension, Floor = Floor,
        Hp = Hp, MaxHp = MaxHp, Gold = Gold, Relics = Relics.ToList(), Potions = Potions.ToList(), PotionSlots = PotionSlots,
    };

    public double HpFraction => MaxHp <= 0 ? 0 : Hp / MaxHp;
    public bool Has(string relic) => Relics.Contains(relic);

    // ---- HP and gold ----
    public void LoseHp(double amount) => Hp = Math.Max(0, Hp - amount);
    public void Heal(double amount) => Hp = Math.Min(MaxHp, Hp + amount);
    public void HealFraction(double fraction) => Heal(MaxHp * fraction);
    public void GainMaxHp(double amount) { MaxHp += amount; Hp += amount; }
    public void LoseMaxHp(double amount) { MaxHp = Math.Max(1, MaxHp - amount); Hp = Math.Min(Hp, MaxHp); }
    public void GainGold(int amount) => Gold += amount;
    public void LoseGold(int amount) => Gold = Math.Max(0, Gold - amount);

    // ---- cards ----
    public void AddCard(string id, bool upgraded = false)
    {
        if (Data.Cards.Contains(id))
        {
            CardDef card = Data.Cards.Get(id, upgraded);
            if (!upgraded && card.UpgradedForm != null && DeckPolicies.EggUpgrades(card.Kind, Relics)) card = Data.Cards.Get(id, true);
            Deck.Add(card);
        }
        else Notes.Add($"Card {id} isn't known, so it was not added.");
    }

    /// <summary>Removes the card a player would want gone most (a curse, then a Strike, then a Defend). False if none of those is in the deck.</summary>
    public bool RemoveWorstCard() => DeckPolicies.RemoveWorst(Deck);

    /// <summary>Removes a random card that isn't a Strike or Defend (Slippery Bridge takes whatever it shows you).</summary>
    public void RemoveRandomNonBasic()
    {
        var candidates = Enumerable.Range(0, Deck.Count).Where(i => !Deck[i].Id.StartsWith("STRIKE_") && !Deck[i].Id.StartsWith("DEFEND_")).ToList();
        if (candidates.Count > 0) Deck.RemoveAt(candidates[Rng.Next(candidates.Count)]);
        else RemoveWorstCard();
    }

    /// <summary>Removes the non-basic card the community likes least; what a player who keeps rerolling the bridge's offer ends up losing.</summary>
    public void RemoveLowestEloNonBasic()
    {
        int worst = -1;
        double lowest = double.MaxValue;
        for (int i = 0; i < Deck.Count; i++)
        {
            if (Deck[i].Id.StartsWith("STRIKE_") || Deck[i].Id.StartsWith("DEFEND_")) continue;
            double elo = Deck[i].Kind is CardKind.Curse or CardKind.Status ? 0 : Pool.Elo(Deck[i].Id);
            if (elo < lowest) { lowest = elo; worst = i; }
        }
        if (worst >= 0) Deck.RemoveAt(worst); else RemoveWorstCard();
    }

    public int TransformableBasicCards => Deck.Count(c => c.Id.StartsWith("STRIKE_") || c.Id.StartsWith("DEFEND_"));

    /// <summary>Transforms the worst basic cards into random cards of the character's pool.</summary>
    public void TransformWorst(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!RemoveWorstCard()) return;
            string[] offer = Pool.GenerateOffer(RewardKind.Normal, new RarityOdds(Ascension, 0f), Rng, 1);
            if (offer.Length > 0) AddCard(offer[0]);
        }
    }

    public void UpgradeBest() => DeckPolicies.UpgradeBest(Deck, Data, Pool);

    /// <summary>Upgrades the last Strike and the last Defend in the deck (Neow's Talisman).</summary>
    public void UpgradeBasics()
    {
        foreach (string prefix in new[] { "STRIKE_", "DEFEND_" })
        {
            int i = Deck.FindLastIndex(c => c.Id.StartsWith(prefix) && !c.Upgraded && c.UpgradedForm != null);
            if (i >= 0) Deck[i] = Data.Cards.Get(Deck[i].Id, true);
        }
    }

    /// <summary>Adds a random card of the character's pool with the given rarity (Arcane Scroll).</summary>
    public void AddRandomClassCard(CardRarity rarity)
    {
        string? id = Pool.RollClass(rarity, Rng);
        if (id != null) AddCard(id);
    }

    /// <summary>A normal card reward the player picks from like any other (Lost Coffer, Kaleidoscope).</summary>
    public void TakeFromOffer()
    {
        string[] offer = Pool.GenerateOffer(RewardKind.Normal, new RarityOdds(Ascension, 0f), Rng);
        int pick = PickPolicy.Choose(offer, Pool, Deck.Count, Rng);
        if (pick >= 0) AddCard(offer[pick]);
    }

    /// <summary>Two colorless cards to choose from (Lead Paperweight).</summary>
    public void TakeColorless(int count)
    {
        var offer = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string? id = Pool.RollColorless(Rng.NextDouble() < 0.7 ? CardRarity.Uncommon : CardRarity.Rare, Rng, offer);
            if (id != null) offer.Add(id);
        }
        int pick = PickPolicy.Choose(offer, Pool, Deck.Count, Rng);
        if (pick >= 0) AddCard(offer[pick]);
    }

    /// <summary>Two bundles of two commons and an uncommon; the player takes the one the community likes more (Scroll Boxes).</summary>
    public void TakeBundle()
    {
        var used = new List<string>();
        var bundles = new List<List<string>>();
        for (int b = 0; b < 2; b++)
        {
            var bundle = new List<string>();
            for (int i = 0; i < 2; i++) if (Pool.RollClass(CardRarity.Common, Rng, used) is { } common) { bundle.Add(common); used.Add(common); }
            if (Pool.RollClass(CardRarity.Uncommon, Rng, used) is { } uncommon) { bundle.Add(uncommon); used.Add(uncommon); }
            bundles.Add(bundle);
        }
        foreach (string id in bundles.OrderByDescending(b => b.Sum(Pool.Elo)).First()) AddCard(id);
    }

    public void UpgradeRandom(int count)
    {
        var candidates = Enumerable.Range(0, Deck.Count).Where(i => !Deck[i].Upgraded && Deck[i].UpgradedForm != null).ToList();
        Rng.Shuffle(candidates);
        foreach (int i in candidates.Take(count)) Deck[i] = Data.Cards.Get(Deck[i].Id, true);
    }

    // ---- relics, potions, fights ----
    public void GainRandomRelic()
    {
        string? id = RelicPool.Roll(Rng, Relics);
        if (id != null) GainRelic(id);
    }

    public void GainRelic(string id)
    {
        if (Relics.Contains(id)) return;
        Relics.Add(id);
        PendingRelics.Add(id);
        if (RelicRules.Parse(id) == RelicKind.Unknown) Notes.Add($"Relic {id} has effects the simulator doesn't model.");
    }

    public void GainRandomPotion()
    {
        PotionDef? potion = PotionLibrary.Roll(Rng);
        if (potion != null && Potions.Count < PotionSlots) Potions.Add(potion.Id);
    }

    public void Fight(string encounter, bool relicReward = false, bool potionReward = false) => PendingFights.Add(new PendingFight(encounter, relicReward, potionReward));
}

/// <summary>Deck-editing rules shared by rest sites, shops, and events.</summary>
public static class DeckPolicies
{
    /// <summary>Molten, Toxic and Frozen Egg upgrade every Attack, Skill or Power the player takes into the deck.</summary>
    public static bool EggUpgrades(CardKind kind, IEnumerable<string> relics) => kind switch
    {
        CardKind.Attack => relics.Contains("MOLTEN_EGG"),
        CardKind.Skill => relics.Contains("TOXIC_EGG"),
        CardKind.Power => relics.Contains("FROZEN_EGG"),
        _ => false,
    };

    /// <summary>Removes a curse or status first, then a Strike, then a Defend; false if the deck has none of those.</summary>
    public static bool RemoveWorst(List<CardDef> deck)
    {
        int worst = -1, worstRank = int.MaxValue;
        for (int i = 0; i < deck.Count; i++)
        {
            int rank = deck[i].Kind is CardKind.Curse or CardKind.Status ? 0 : deck[i].Id.StartsWith("STRIKE_") ? 1 : deck[i].Id.StartsWith("DEFEND_") ? 2 : int.MaxValue;
            if (rank < worstRank) { worstRank = rank; worst = i; }
        }
        if (worst < 0) return false;
        deck.RemoveAt(worst);
        return true;
    }

    /// <summary>Upgrades the card the community likes best (by Elo), Bash first among the starting cards.</summary>
    public static void UpgradeBest(List<CardDef> deck, SimData data, RewardPool pool)
    {
        int best = -1;
        double bestScore = double.MinValue;
        for (int i = 0; i < deck.Count; i++)
        {
            CardDef card = deck[i];
            if (card.Upgraded || card.Kind is CardKind.Status or CardKind.Curse) continue;
            double score = card.Id == "BASH" ? 1700 : pool.HasElo(card.Id) ? pool.Elo(card.Id) : 1000;
            if (score > bestScore) { bestScore = score; best = i; }
        }
        if (best >= 0) deck[best] = data.Cards.Get(deck[best].Id, true);
    }
}

public sealed record EventOptionDef(string Key, Action<EventState> Apply, bool Enabled = true);

/// <summary>One event: what it needs to be offered, the options it shows, and which one a typical player takes.</summary>
public sealed class EventDef
{
    public required string Id { get; init; }
    public Func<EventState, bool> Allowed { get; init; } = _ => true;
    public required Func<EventState, IReadOnlyList<EventOptionDef>> Options { get; init; }

    /// <summary>The key of the option a typical player would pick (used when the rollout plays the event for them).</summary>
    public required Func<EventState, string> Default { get; init; }
}

/// <summary>
/// The events of Act 1 (Overgrowth and Underdocks) and the shared events that can turn up there, written from the decompiled
/// event classes (v0.111): the numbers, the conditions under which the game offers them, and what each option does. Multi-page
/// events run a simple default continuation. Enchantments and a few relics have no effect in the simulator and are noted.
/// </summary>
public static class EventLibrary
{
    private static readonly Dictionary<string, EventDef> ById = Build().ToDictionary(e => e.Id);

    public static EventDef? Find(string id) => ById.GetValueOrDefault(id);

    public static IReadOnlyCollection<EventDef> All => ById.Values;

    /// <summary>The event ids the game can draw in an act variant (its own list plus the shared events), which decides how often each shows up.</summary>
    public static IReadOnlyList<string> Pool(string actVariant) => actVariant.ToUpperInvariant() switch
    {
        "OVERGROWTH" => Overgrowth.Concat(Shared).ToList(),
        "UNDERDOCKS" => Underdocks.Concat(Shared).ToList(),
        _ => Shared,
    };

    private static readonly string[] Overgrowth =
    {
        "AROMA_OF_CHAOS", "BYRDONIS_NEST", "DENSE_VEGETATION", "JUNGLE_MAZE_ADVENTURE", "LUMINOUS_CHOIR", "MORPHIC_GROVE", "SAPPHIRE_SEED",
        "SUNKEN_STATUE", "TABLET_OF_TRUTH", "UNREST_SITE", "WELLSPRING", "WHISPERING_HOLLOW", "WOOD_CARVINGS",
    };

    private static readonly string[] Underdocks =
    {
        "ABYSSAL_BATHS", "DROWNING_BEACON", "ENDLESS_CONVEYOR", "PUNCH_OFF", "SPIRALING_WHIRLPOOL", "SUNKEN_STATUE", "SUNKEN_TREASURY",
        "DOORS_OF_LIGHT_AND_DARK", "TRASH_HEAP", "WATERLOGGED_SCRIPTORIUM",
    };

    private static readonly string[] Shared =
    {
        "BRAIN_LEECH", "CRYSTAL_SPHERE", "DOLL_ROOM", "FAKE_MERCHANT", "POTION_COURIER", "RANWID_THE_ELDER", "RELIC_TRADER", "ROOM_FULL_OF_CHEESE",
        "SELF_HELP_BOOK", "SLIPPERY_BRIDGE", "STONE_OF_ALL_TIME", "SYMBIOTE", "TEA_MASTER", "THE_FUTURE_OF_POTIONS", "THE_LEGENDS_WERE_TRUE",
        "THIS_OR_THAT", "WAR_HISTORIAN_REPY", "WELCOME_TO_WONGOS",
    };

    private static EventOptionDef Opt(string key, Action<EventState> apply, bool enabled = true) => new(key, apply, enabled);

    private static EventDef Ev(string id, Func<EventState, IReadOnlyList<EventOptionDef>> options, Func<EventState, string> pick, Func<EventState, bool>? allowed = null) =>
        new() { Id = id, Options = options, Default = pick, Allowed = allowed ?? (_ => true) };

    private static int Between(EventState s, int lo, int hi) => s.Rng.NextInclusive(lo, hi);

    private static IEnumerable<EventDef> Build()
    {
        // ---- Overgrowth ----
        yield return Ev("AROMA_OF_CHAOS", s => new[]
        {
            Opt("MAINTAIN_CONTROL", x => x.UpgradeBest()),
            Opt("LET_GO", x => x.TransformWorst(1)),
        }, s => "MAINTAIN_CONTROL");

        yield return Ev("BYRDONIS_NEST", s => new[]
        {
            Opt("EAT", x => x.GainMaxHp(7)),
            Opt("TAKE", x => x.AddCard("BYRDONIS_EGG")),
        }, s => "EAT");

        yield return Ev("DENSE_VEGETATION", s => new[]
        {
            Opt("TRUDGE_ON", x => { x.LoseHp(8); x.GainGold(Between(x, 61, 99)); }),
            Opt("REST", x => { x.HealFraction(0.3); x.Fight("DENSE_VEGETATION_EVENT_ENCOUNTER"); }),
        }, s => s.HpFraction < 0.4 ? "REST" : "TRUDGE_ON");

        yield return Ev("JUNGLE_MAZE_ADVENTURE", s => new[]
        {
            Opt("SOLO_QUEST", x => { x.LoseHp(18); x.GainGold(150 + Between(x, -15, 15)); }),
            Opt("JOIN_FORCES", x => x.GainGold(50 + Between(x, -15, 15))),
        }, s => s.Hp > 45 ? "SOLO_QUEST" : "JOIN_FORCES");

        yield return Ev("LUMINOUS_CHOIR", s => new[]
        {
            Opt("OFFER_TRIBUTE", x => { x.LoseGold(149 - Between(x, 0, 49)); x.GainRandomRelic(); }, enabled: s.Gold >= 100),
            Opt("REACH_INTO_THE_FLESH", x => { x.RemoveWorstCard(); x.RemoveWorstCard(); x.AddCard("SPORE_MIND"); }),
        }, s => s.Gold >= 149 ? "OFFER_TRIBUTE" : "REACH_INTO_THE_FLESH", allowed: s => s.Gold >= 100);

        yield return Ev("MORPHIC_GROVE", s => new[]
        {
            Opt("LONER", x => x.GainMaxHp(5)),
            Opt("GROUP", x => { x.LoseGold(x.Gold); x.TransformWorst(2); }),
        }, s => s.Gold < 250 ? "GROUP" : "LONER", allowed: s => s.Gold >= 100 && s.TransformableBasicCards >= 2);

        yield return Ev("SAPPHIRE_SEED", s => new[]
        {
            Opt("EAT", x => { x.Heal(9); x.UpgradeBest(); }),
            Opt("PLANT", x => x.Notes.Add("The Sown enchantment isn't modelled.")),
        }, s => "EAT");

        yield return Ev("SUNKEN_STATUE", s => new[]
        {
            Opt("GRAB_SWORD", x => x.GainRelic("SWORD_OF_STONE")),
            Opt("DIVE_INTO_WATER", x => { x.GainGold(111 + Between(x, -10, 10)); x.LoseHp(7); }),
        }, s => "DIVE_INTO_WATER");

        yield return Ev("TABLET_OF_TRUTH", s => new[]
        {
            Opt("DECIPHER_1", x => { x.LoseMaxHp(3); x.UpgradeRandom(1); }, enabled: s.MaxHp > 3),
            Opt("SMASH", x => x.Heal(20)),
        }, s => s.HpFraction < 0.5 ? "SMASH" : "DECIPHER_1");

        yield return Ev("UNREST_SITE", s => new[]
        {
            Opt("KILL", x => { x.LoseMaxHp(8); x.GainRandomRelic(); }),
            Opt("REST", x => { x.Heal(x.MaxHp); x.AddCard("POOR_SLEEP"); }),
        }, s => s.HpFraction < 0.5 ? "REST" : "KILL", allowed: s => s.HpFraction <= 0.7);

        yield return Ev("WELLSPRING", s => new[]
        {
            Opt("BOTTLE", x => x.GainRandomPotion()),
            Opt("BATHE", x => { x.RemoveWorstCard(); x.AddCard("GUILTY"); }),
        }, s => "BOTTLE");

        yield return Ev("WHISPERING_HOLLOW", s => new[]
        {
            Opt("GOLD", x => { x.LoseGold(35 + Between(x, -9, 9)); x.GainRandomPotion(); x.GainRandomPotion(); }, enabled: s.Gold >= 44),
            Opt("HUG", x => { x.TransformWorst(1); x.LoseHp(9); }),
        }, s => s.Gold >= 44 ? "GOLD" : "HUG", allowed: s => s.Gold >= 44);

        yield return Ev("WOOD_CARVINGS", s => new[]
        {
            Opt("BIRD", x => { if (x.RemoveWorstCard()) x.AddCard("PECK"); }),
            Opt("SNAKE", x => x.Notes.Add("The Slither enchantment isn't modelled.")),
            Opt("TORUS", x => { if (x.RemoveWorstCard()) x.AddCard("TORIC_TOUGHNESS"); }),
        }, s => "BIRD", allowed: s => s.TransformableBasicCards > 0);

        // ---- Underdocks ----
        yield return Ev("ABYSSAL_BATHS", s => new[]
        {
            Opt("IMMERSE", x => { x.GainMaxHp(2); x.LoseHp(3); }),
            Opt("ABSTAIN", x => x.Heal(10)),
        }, s => s.HpFraction < 0.5 ? "ABSTAIN" : "IMMERSE");

        yield return Ev("DROWNING_BEACON", s => new[]
        {
            Opt("BOTTLE", x => x.Notes.Add("Glowwater Potion isn't modelled.")),
            Opt("CLIMB", x => { x.LoseMaxHp(13); x.GainRelic("FRESNEL_LENS"); }),
        }, s => "BOTTLE");

        yield return Ev("PUNCH_OFF", s => new[]
        {
            Opt("NAB", x => { x.AddCard("INJURY"); x.GainRandomRelic(); }),
            Opt("I_CAN_TAKE_THEM", x => x.Fight("PUNCH_OFF_EVENT_ENCOUNTER", relicReward: true, potionReward: true)),
        }, s => s.HpFraction > 0.8 ? "I_CAN_TAKE_THEM" : "NAB", allowed: s => s.Floor >= 6);

        yield return Ev("SPIRALING_WHIRLPOOL", s => new[]
        {
            Opt("OBSERVE", x => x.Notes.Add("The Spiral enchantment isn't modelled.")),
            Opt("DRINK", x => x.HealFraction(0.33)),
        }, s => s.HpFraction < 0.7 ? "DRINK" : "OBSERVE");

        yield return Ev("SUNKEN_TREASURY", s => new[]
        {
            Opt("FIRST_CHEST", x => x.GainGold(60 + Between(x, -8, 7))),
            Opt("SECOND_CHEST", x => { x.GainGold(333 + Between(x, -30, 30)); x.AddCard("GREED"); }),
        }, s => "SECOND_CHEST");

        yield return Ev("DOORS_OF_LIGHT_AND_DARK", s => new[]
        {
            Opt("LIGHT", x => x.UpgradeRandom(2)),
            Opt("DARK", x => x.RemoveWorstCard()),
        }, s => "LIGHT");

        yield return Ev("TRASH_HEAP", s => new[]
        {
            Opt("DIVE_IN", x => { x.LoseHp(8); x.GainRelic(new[] { "DARKSTONE_PERIAPT", "DREAM_CATCHER", "HAND_DRILL", "MAW_BANK", "THE_BOOT" }[x.Rng.Next(5)]); }),
            Opt("GRAB", x => { x.GainGold(100); x.AddCard(new[] { "CALTROPS", "CLASH", "DISTRACTION", "DUAL_WIELD", "ENTRENCH", "HELLO_WORLD", "OUTMANEUVER", "REBOUND", "RIP_AND_TEAR", "STACK" }[x.Rng.Next(10)]); }),
        }, s => "GRAB", allowed: s => s.Hp > 5);

        yield return Ev("WATERLOGGED_SCRIPTORIUM", s => new[]
        {
            Opt("BLOODY_INK", x => x.GainMaxHp(6)),
            Opt("TENTACLE_QUILL", x => { x.LoseGold(55); x.Notes.Add("The Steady enchantment isn't modelled."); }, enabled: s.Gold >= 55),
            Opt("PRICKLY_SPONGE", x => { x.LoseGold(99); x.Notes.Add("The Steady enchantment isn't modelled."); }, enabled: s.Gold >= 99),
        }, s => "BLOODY_INK", allowed: s => s.Gold >= 55);

        // ---- shared events that can appear in Act 1 ----
        yield return Ev("BRAIN_LEECH", s => new[]
        {
            Opt("SHARE_KNOWLEDGE", x => { string[] five = x.Pool.GenerateOffer(RewardKind.Normal, new RarityOdds(x.Ascension, 0f), x.Rng, 5); if (five.Length > 0) x.AddCard(five.OrderByDescending(x.Pool.Elo).First()); }),
            Opt("RIP", x => { x.LoseHp(5); string[] offer = x.Pool.GenerateOffer(RewardKind.Normal, new RarityOdds(x.Ascension, 0f), x.Rng); int pick = PickPolicy.Choose(offer, x.Pool, x.Deck.Count, x.Rng); if (pick >= 0) x.AddCard(offer[pick]); }),
        }, s => "SHARE_KNOWLEDGE", allowed: s => s.Act < 3);

        yield return Ev("ROOM_FULL_OF_CHEESE", s => new[]
        {
            Opt("GORGE", x => { string[] eight = x.Pool.GenerateOffer(RewardKind.Normal, new RarityOdds(x.Ascension, 0f), x.Rng, 8); foreach (string id in eight.OrderByDescending(x.Pool.Elo).Take(2)) x.AddCard(id); }),
            Opt("SEARCH", x => { x.LoseHp(14); x.GainRelic("CHOSEN_CHEESE"); }),
        }, s => "GORGE", allowed: s => s.Act < 3);

        yield return Ev("THE_LEGENDS_WERE_TRUE", s => new[]
        {
            Opt("NAB_THE_MAP", x => x.AddCard("SPOILS_MAP")),
            Opt("SLOWLY_FIND_AN_EXIT", x => { x.LoseHp(8); x.GainRandomPotion(); }),
        }, s => "SLOWLY_FIND_AN_EXIT", allowed: s => s.Act == 1 && s.Hp >= 10);

        yield return Ev("THIS_OR_THAT", s => new[]
        {
            Opt("PLAIN", x => { x.LoseHp(6); x.GainGold(Between(x, 41, 68)); }),
            Opt("ORNATE", x => { x.GainRandomRelic(); x.AddCard("CLUMSY"); }),
        }, s => "ORNATE");

        yield return Ev("SLIPPERY_BRIDGE", s => new[]
        {
            Opt("OVERCOME", x => x.RemoveRandomNonBasic()),
            Opt("HOLD_ON_0", x => { x.LoseHp(3); x.RemoveLowestEloNonBasic(); }),
        }, s => "HOLD_ON_0", allowed: s => s.Floor > 6);
    }
}
