using SpireMonteCarlo.Contracts;

namespace SpireMonteCarlo.Sim;

/// <summary>
/// Turns a snapshot of any supported run-level decision into a list of options (each the run's state after that choice) and
/// compares them over the same simulated futures of the act. Supported: card reward, rest site, card upgrade, map path, shop,
/// and Ancient events that offer relics.
/// </summary>
public static class DecisionAdvisor
{
    public static bool Supports(RunSnapshot snapshot) => snapshot.Decision switch
    {
        DecisionType.CardReward or DecisionType.RestSite or DecisionType.CardUpgrade or DecisionType.Map or DecisionType.Shop => true,
        DecisionType.Event => snapshot.EventOptions.Any(o => o.Relic != null && !o.IsLocked),
        _ => false,
    };

    public static AdviceReport Evaluate(SimData data, RunSnapshot snapshot, int rollouts, ulong seed) => snapshot.Decision switch
    {
        DecisionType.CardReward => AdviceEngine.EvaluateCardReward(data, snapshot, rollouts, seed),
        DecisionType.RestSite => Rest(data, snapshot, rollouts, seed),
        DecisionType.CardUpgrade => Upgrade(data, snapshot, rollouts, seed),
        DecisionType.Map => Map(data, snapshot, rollouts, seed),
        DecisionType.Shop => Shop(data, snapshot, rollouts, seed),
        DecisionType.Event => AncientRelics(data, snapshot, rollouts, seed),
        _ => throw new NotSupportedException($"Decisions of type '{snapshot.Decision}' are not supported yet."),
    };

    private static List<CardDef> DeckOf(SimData data, RunSnapshot snapshot) => snapshot.Deck.Select(c => data.Cards.Get(c.Id, c.Upgraded)).ToList();

    /// <summary>One entry per distinct upgradable card, at the first copy in the deck.</summary>
    private static IEnumerable<(int Index, CardDef Card)> UpgradeCandidates(List<CardDef> deck)
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < deck.Count; i++)
            if (!deck[i].Upgraded && deck[i].UpgradedForm != null && deck[i].Kind is not (CardKind.Status or CardKind.Curse) && seen.Add(deck[i].Id))
                yield return (i, deck[i]);
    }

    private static RolloutStart Upgraded(SimData data, RolloutStart start, int index)
    {
        RolloutStart s = start.Copy();
        s.Deck[index] = data.Cards.Get(s.Deck[index].Id, true);
        return s;
    }

    // ---- rest site ----

    private static AdviceReport Rest(SimData data, RunSnapshot snapshot, int rollouts, ulong seed)
    {
        var rollout = new ActRollout(data, snapshot);
        List<CardDef> deck = DeckOf(data, snapshot);
        RolloutStart start = rollout.InitialStart(deck);

        RolloutStart rest = start.Copy();
        int heal = (int)(0.3 * rest.MaxHp) + (snapshot.Relics.Contains("REGAL_PILLOW") ? 15 : 0);
        rest.Hp = Math.Min(rest.MaxHp, rest.Hp + heal);
        var options = new List<DecisionOption> { new($"Rest (heal {rest.Hp - start.Hp} HP)", null, rest) };
        foreach ((int index, CardDef card) in UpgradeCandidates(deck))
            options.Add(new DecisionOption($"Upgrade {card.Id}", null, Upgraded(data, start, index)));
        return AdviceEngine.Evaluate(data, snapshot, rollout, options, rollouts, seed);
    }

    private static AdviceReport Upgrade(SimData data, RunSnapshot snapshot, int rollouts, ulong seed)
    {
        var rollout = new ActRollout(data, snapshot);
        List<CardDef> deck = DeckOf(data, snapshot);
        RolloutStart start = rollout.InitialStart(deck);
        var options = new List<DecisionOption> { new("Upgrade nothing", null, start) };
        foreach ((int index, CardDef card) in UpgradeCandidates(deck))
            options.Add(new DecisionOption($"Upgrade {card.Id}", null, Upgraded(data, start, index)));
        return AdviceEngine.Evaluate(data, snapshot, rollout, options, rollouts, seed);
    }

    // ---- map ----

    private static AdviceReport Map(SimData data, RunSnapshot snapshot, int rollouts, ulong seed)
    {
        var rollout = new ActRollout(data, snapshot);
        RolloutStart start = rollout.InitialStart(DeckOf(data, snapshot));
        var options = new List<DecisionOption>();
        foreach (MapPointSnapshot node in rollout.NextNodes.OrderBy(n => n.Col))
        {
            RolloutStart s = start.Copy();
            s.ForcedNext = new MapCoordinate(node.Col, node.Row);
            options.Add(new DecisionOption($"{node.Type} (column {node.Col}, row {node.Row})", null, s));
        }
        if (options.Count == 0) throw new InvalidOperationException("There are no nodes to move to from here.");
        return AdviceEngine.Evaluate(data, snapshot, rollout, options, rollouts, seed);
    }

    // ---- Ancient relic choice ----

    private static AdviceReport AncientRelics(SimData data, RunSnapshot snapshot, int rollouts, ulong seed)
    {
        var rollout = new ActRollout(data, snapshot);
        RolloutStart start = rollout.InitialStart(DeckOf(data, snapshot));
        var options = new List<DecisionOption>();
        var notes = new List<string>();
        foreach (EventOptionSnapshot option in snapshot.EventOptions.Where(o => o.Relic != null && !o.IsLocked))
        {
            RolloutStart s = start.Copy();
            s.AcquireOnStart.Add(option.Relic!);
            bool modelled = RelicRules.Parse(option.Relic!) != RelicKind.Unknown;
            options.Add(new DecisionOption(option.Relic! + (modelled ? "" : " (not modelled)"), null, s));
            if (!modelled) notes.Add($"{option.Relic} has effects the simulator doesn't model, so it is scored as if it did nothing.");
        }
        return AdviceEngine.Evaluate(data, snapshot, rollout, options, rollouts, seed, notes);
    }

    // ---- shop ----

    private sealed record Item(string Label, int Price, Action<RolloutStart> Apply, string? CardId = null);

    private const int PotionSlots = 3;

    private static AdviceReport Shop(SimData data, RunSnapshot snapshot, int rollouts, ulong seed)
    {
        var rollout = new ActRollout(data, snapshot);
        List<CardDef> deck = DeckOf(data, snapshot);
        RolloutStart start = rollout.InitialStart(deck);
        RewardPool pool = data.PoolFor(snapshot.Run.Character);
        int gold = snapshot.Run.Gold;
        var notes = new List<string>();
        var items = new List<Item>();

        // Cards: the few the community likes best (Codex Elo), since bundling every card would be far too many combinations.
        foreach (CardSnapshot card in snapshot.Offer.Cards.Where(c => c.Price > 0 && c.Price <= gold).OrderByDescending(c => pool.Elo(c.Id)).Take(5))
        {
            CardDef def = data.Cards.Get(card.Id, card.Upgraded);
            items.Add(new Item($"card {card.Id} ({card.Price}g)", card.Price, s => s.Deck.Add(def), card.Id));
        }
        foreach (RelicOffer relic in snapshot.Offer.Relics.Where(r => r.Price <= gold))
        {
            if (RelicRules.Parse(relic.Id) == RelicKind.Unknown) { notes.Add($"Relic {relic.Id} ({relic.Price}g) has effects the simulator doesn't model, so it isn't evaluated."); continue; }
            items.Add(new Item($"relic {relic.Id} ({relic.Price}g)", relic.Price, s => s.AcquireOnStart.Add(relic.Id)));
        }
        foreach (PotionSnapshot potion in snapshot.Offer.Potions.Where(p => p.Price <= gold))
        {
            if (PotionLibrary.Find(potion.Id) == null) { notes.Add($"Potion {potion.Id} ({potion.Price}g) isn't modelled, so it isn't evaluated."); continue; }
            items.Add(potion.Id == "FRUIT_JUICE"
                ? new Item($"potion {potion.Id} ({potion.Price}g)", potion.Price, s => { s.MaxHp += 5; s.Hp += 5; })
                : new Item($"potion {potion.Id} ({potion.Price}g)", potion.Price, s => { if (s.Potions.Count < PotionSlots) s.Potions.Add(potion.Id); }));
        }
        if (snapshot.Offer.CardRemovalPrice is int removal && removal <= gold)
        {
            // Removing a Strike or a Defend, or a curse, are the removals worth comparing.
            foreach (string id in deck.Where(c => c.Id.StartsWith("STRIKE_") || c.Id.StartsWith("DEFEND_") || c.Kind is CardKind.Curse or CardKind.Status).Select(c => c.Id).Distinct())
                items.Add(new Item($"remove {id} ({removal}g)", removal, s => { int i = s.Deck.FindIndex(c => c.Id == id); if (i >= 0) s.Deck.RemoveAt(i); s.RemovalsUsed++; }));
        }

        DecisionOption Bundle(IReadOnlyList<Item> bundle)
        {
            RolloutStart s = start.Copy();
            foreach (Item item in bundle) item.Apply(s);
            s.Gold -= bundle.Sum(i => i.Price);
            return new DecisionOption(string.Join(" + ", bundle.Select(i => i.Label)) + (bundle.Count > 1 ? $"  [{bundle.Sum(i => i.Price)}g]" : ""), null, s);
        }

        var nothing = new DecisionOption("Buy nothing", null, start);
        if (items.Count == 0) return AdviceEngine.Evaluate(data, snapshot, rollout, new[] { nothing }, rollouts, seed, notes);

        // Stage 1: every single item, on fewer futures, to find the ones worth combining.
        var singles = items.Select(i => Bundle(new[] { i })).ToList();
        AdviceReport first = AdviceEngine.Evaluate(data, snapshot, rollout, new[] { nothing }.Concat(singles).ToList(), Math.Max(100, rollouts / 2), seed, notes);
        var byLabel = first.Options.ToDictionary(o => o.Label);
        OptionReport Report(Item i) => byLabel[Bundle(new[] { i }).Label];
        var promising = items.Where(i => Report(i).DeltaValue > -Report(i).DeltaValueSe).OrderByDescending(i => Report(i).DeltaValue).Take(6).ToList();

        // Stage 2: bundles of the promising items that fit in the gold, plus every single, on the full number of futures.
        var options = new List<DecisionOption> { nothing };
        options.AddRange(singles);
        for (int size = 2; size <= 3; size++)
            foreach (var combo in Combinations(promising, size))
                if (combo.Sum(i => i.Price) <= gold && !ConflictingRemovals(combo)) options.Add(Bundle(combo));
        return AdviceEngine.Evaluate(data, snapshot, rollout, options, rollouts, seed, notes);
    }

    private static bool ConflictingRemovals(IEnumerable<Item> combo) => combo.Count(i => i.Label.StartsWith("remove ")) > 1;

    private static IEnumerable<List<T>> Combinations<T>(IReadOnlyList<T> source, int size)
    {
        var idx = new int[size];
        if (source.Count < size) yield break;
        for (int i = 0; i < size; i++) idx[i] = i;
        while (true)
        {
            yield return idx.Select(i => source[i]).ToList();
            int k = size - 1;
            while (k >= 0 && idx[k] == source.Count - size + k) k--;
            if (k < 0) yield break;
            idx[k]++;
            for (int j = k + 1; j < size; j++) idx[j] = idx[j - 1] + 1;
        }
    }
}
