using SpireMonteCarlo.Contracts;

namespace SpireMonteCarlo.Sim;

public sealed record FightLogEntry(string Encounter, int HpBefore, int HpAfter, int DeckSize);

public sealed record RolloutResult(bool Survived, int HpEnd, int MaxHp, int FightsWon, int FightsTotal, string? DiedTo, int UnmodelledFights, IReadOnlyList<string> Encounters, IReadOnlyList<FightLogEntry> Log, int ProbeFights = 0, int ProbeWins = 0);

/// <summary>
/// One simulated future of the current act, from the snapshot's position to the end of the act's boss fight.
/// The path across the map is sampled, fights use the game's real upcoming encounters when the snapshot has them,
/// and later card rewards, rest sites, and "?" rooms follow default policies. Two runs with the same seed see the
/// same luck, so different decks can be compared fairly.
/// </summary>
/// <summary>
/// The player's state a rollout starts from (HP in real, unscaled points). A decision option is this state after the choice:
/// the same snapshot with a different deck, HP, gold, relics, or first step on the map.
/// </summary>
public sealed class RolloutStart
{
    public required List<CardDef> Deck { get; init; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Gold { get; set; }
    public List<string> Relics { get; init; } = new();
    public List<string> Potions { get; init; } = new();
    public int RemovalsUsed { get; set; }

    /// <summary>Relics gained by the decision that still need their pickup effect (max HP, potion slots, upgrades).</summary>
    public List<string> AcquireOnStart { get; init; } = new();

    /// <summary>The node the player walks to first, instead of the route policy's choice.</summary>
    public MapCoordinate? ForcedNext { get; set; }

    public RolloutStart Copy() => new()
    {
        Deck = Deck.ToList(), Hp = Hp, MaxHp = MaxHp, Gold = Gold, Relics = Relics.ToList(), Potions = Potions.ToList(),
        RemovalsUsed = RemovalsUsed, AcquireOnStart = AcquireOnStart.ToList(), ForcedNext = ForcedNext,
    };
}

public sealed class ActRollout
{
    private const float MonsterBase = 0.1f, TreasureBase = 0.02f, ShopBase = 0.03f;
    private const float PotionBaseOdds = 0.4f;
    private const int PotionSlots = 3;

    private readonly SimData _data;
    private readonly RunSnapshot _snap;
    private readonly Dictionary<MapCoordinate, MapPointSnapshot> _points;
    private readonly BasicBot _bot = new();

    /// <summary>
    /// Multiplies the player's HP pool (current, max, and every heal) inside the simulation, so every HP amount stays in
    /// proportion: enemy damage, self-inflicted HP loss from cards, and healing. Tuned so a simulated Ironclad survives
    /// Act 1 about as often as real players (boss deaths near the real ~16%). It measures what the simulator does not
    /// model yet (potions, relics, events, better play): 1.0 would mean nothing is missing. Reported HP is converted back.
    /// </summary>
    public double PlayerHpScale { get; init; } = CalibratedPlayerHpScale;

    // Re-fit after relics (was 3.25), 80% start HP, and shops went in: 2.4 gives 56% Act 1 survival, 2.5 gives 64% (real ~65%)
    // with per-elite fatal rates near the real ones, 2.6 gives 69%. Bosses are still off: Lagavulin Matriarch and The Kin
    // too lethal, Waterfall Giant too easy. Still unmodelled: the Ancient boon, events, and most rare relics.
    public const double CalibratedPlayerHpScale = 2.5;

    /// <summary>
    /// The HP scale used in the next-act probe. Act 2 enemies hit harder than the Act 1 scale can absorb for a deck that has
    /// only Act 1's cards and relics, so the probe is fit separately: real Ironclad players lose to Act 2 elites 7-12% of the time.
    /// </summary>
    public double ProbeHpScale { get; init; } = CalibratedProbeHpScale;

    public const double CalibratedProbeHpScale = 1.5;
    private readonly RewardPool _pool;
    private readonly int _ascension;
    private readonly RelicPool _relicPool;
    private readonly string[] _weakPool, _normalPool, _elitePool, _bossPool, _nextElitePool;

    /// <summary>How many next-act elites the end-of-act deck fights in the probe.</summary>
    public const int ProbeFightCount = 3;

    public ActRollout(SimData data, RunSnapshot snapshot)
    {
        _data = data;
        _snap = snapshot;
        _ascension = snapshot.Run.Ascension;
        _pool = data.PoolFor(snapshot.Run.Character);
        _relicPool = data.RelicPoolFor(snapshot.Run.Character);
        _points = (snapshot.Map ?? throw new ArgumentException("The snapshot has no map, so there is nothing to walk.")).Points
            .ToDictionary(p => new MapCoordinate(p.Col, p.Row));
        // Snapshots taken before the mod listed the boss node still name its coordinate.
        if (snapshot.Map.Boss is MapCoordinate boss && !_points.ContainsKey(boss))
            _points[boss] = new MapPointSnapshot { Col = boss.Col, Row = boss.Row, Type = "Boss" };

        string variant = (snapshot.Plan?.ActId is { Length: > 0 } id ? id : DefaultVariant(snapshot.Run.Act)).Replace("_", "");
        IEnumerable<EncounterDef> actEncounters = data.Encounters.All.Where(e => (e.Act ?? "").Replace(" ", "").Contains(variant, StringComparison.OrdinalIgnoreCase));
        _weakPool = actEncounters.Where(e => e.RoomType == "Monster" && e.IsWeak).Select(e => e.Id).ToArray();
        _normalPool = actEncounters.Where(e => e.RoomType == "Monster" && !e.IsWeak).Select(e => e.Id).ToArray();
        _elitePool = actEncounters.Where(e => e.RoomType == "Elite").Select(e => e.Id).ToArray();
        _bossPool = actEncounters.Where(e => e.RoomType == "Boss").Select(e => e.Id).ToArray();

        string nextVariant = snapshot.Run.Act < 3 ? DefaultVariant(snapshot.Run.Act + 1) : "";
        _nextElitePool = nextVariant == "" ? Array.Empty<string>()
            : data.Encounters.All.Where(e => e.RoomType == "Elite" && (e.Act ?? "").Replace(" ", "").Contains(nextVariant, StringComparison.OrdinalIgnoreCase)).Select(e => e.Id).ToArray();
    }

    private static string DefaultVariant(int act) => act switch { 1 => "Overgrowth", 2 => "Hive", _ => "Glory" };

    /// <summary>True when the snapshot lists the actual upcoming encounters (otherwise they are sampled).</summary>
    public bool HasExactPlan => _snap.Plan is { Normal.Count: > 0 } || _snap.Plan?.Boss != null;

    /// <summary>The snapshot's state with the given deck; a decision then changes it (heal, upgrade, buy, take a relic, pick a path) to make one option.</summary>
    public RolloutStart InitialStart(IReadOnlyList<CardDef> deck) => new()
    {
        Deck = deck.ToList(), Hp = _snap.Run.CurrentHp, MaxHp = _snap.Run.MaxHp, Gold = _snap.Run.Gold,
        Relics = _snap.Relics.ToList(), Potions = _snap.Potions.Select(p => p.Id).ToList(),
    };

    /// <summary>Where the player stands on the map: the node itself, or the act's start point before the first move.</summary>
    public MapCoordinate CurrentPoint => _snap.Map!.Current ?? StartPoint();

    /// <summary>The nodes the player can move to next.</summary>
    public IReadOnlyList<MapPointSnapshot> NextNodes => _points.TryGetValue(CurrentPoint, out MapPointSnapshot? here)
        ? here.Children.Where(_points.ContainsKey).Select(c => _points[c]).ToList() : new List<MapPointSnapshot>();

    public RolloutResult Run(IReadOnlyList<CardDef> startDeck, ulong seed) => Run(InitialStart(startDeck), seed);

    public RolloutResult Run(RolloutStart start, ulong seed)
    {
        var deck = start.Deck.ToList();
        int hp = (int)Math.Round(start.Hp * PlayerHpScale), maxHp = (int)Math.Round(start.MaxHp * PlayerHpScale);
        var pathRng = new SimRng(SimRng.Mix(seed, 0xA11CE));
        var rarity = new RarityOdds(_ascension, _snap.Odds?.CardRarityOffset ?? -0.05f);
        float mOdds = _snap.Odds?.UnknownMonster ?? MonsterBase, tOdds = _snap.Odds?.UnknownTreasure ?? TreasureBase, sOdds = _snap.Odds?.UnknownShop ?? ShopBase;

        // Encounter sequences: the game's real ones if we have them, otherwise sampled (first three normals are the weak ones).
        var setupRng = new SimRng(SimRng.Mix(seed, 0x5E70));
        int visitedMonsters = _snap.Map!.Visited.Count(c => _points.TryGetValue(c, out MapPointSnapshot? p) && p.Type == "Monster");
        List<string> normals = _snap.Plan is { Normal.Count: > 0 } ? _snap.Plan.Normal.ToList() : SampleNormals(setupRng, visitedMonsters);
        List<string> elites = _snap.Plan is { Elite.Count: > 0 } ? _snap.Plan.Elite.ToList() : SampleFrom(_elitePool, setupRng);
        var bosses = new List<string>();
        if (_snap.Plan?.Boss != null) bosses.Add(_snap.Plan.Boss);
        else if (_bossPool.Length > 0) bosses.Add(_bossPool[setupRng.Next(_bossPool.Length)]);
        if (_snap.Plan?.SecondBoss != null && _ascension >= 10) bosses.Add(_snap.Plan.SecondBoss);
        int normalDrawn = 0, eliteDrawn = 0, fights = 0, won = 0, unmodelled = 0;
        var fought = new List<string>();
        var log = new List<FightLogEntry>();

        MapCoordinate at = _snap.Map.Current ?? StartPoint();
        string? diedTo = null;
        int probeFights = 0, probeWins = 0;

        var potions = start.Potions.Select(PotionLibrary.Find).OfType<PotionDef>().ToList();
        float potionOdds = PotionBaseOdds;
        int potionSlots = PotionSlots;

        int gold = start.Gold, removalsUsed = start.RemovalsUsed;
        var owned = new HashSet<string>(start.Relics);
        var relics = start.Relics.Select(RelicRules.Parse).Where(k => k != RelicKind.Unknown).ToList();
        bool Own(RelicKind k) => relics.Contains(k);
        int Scaled(int amount) => (int)Math.Round(amount * PlayerHpScale);

        void GainMaxHp(int amount)
        {
            int gain = Scaled(amount);
            maxHp += gain; hp += gain;
        }

        void UpgradeRandom(CardKind kind, int count, SimRng rng)
        {
            var candidates = Enumerable.Range(0, deck.Count).Where(i => !deck[i].Upgraded && deck[i].Kind == kind && deck[i].UpgradedForm != null).ToList();
            rng.Shuffle(candidates);
            foreach (int i in candidates.Take(count)) deck[i] = _data.Cards.Get(deck[i].Id, true);
        }

        // A relic found in an elite fight or a chest. Relics the simulator doesn't model are still used up but do nothing.
        void Acquire(string? id, SimRng rng)
        {
            if (id == null || !owned.Add(id)) return;
            RelicKind kind = RelicRules.Parse(id);
            if (kind == RelicKind.Unknown) return;
            relics.Add(kind);
            switch (kind)
            {
                case RelicKind.Strawberry: GainMaxHp(7); break;
                case RelicKind.Pear: GainMaxHp(10); break;
                case RelicKind.Mango: GainMaxHp(14); break;
                case RelicKind.PotionBelt: potionSlots += 2; break;
                case RelicKind.WarPaint: UpgradeRandom(CardKind.Skill, 2, rng); break;
                case RelicKind.Whetstone: UpgradeRandom(CardKind.Attack, 2, rng); break;
            }
        }

        // Potion drops follow PotionRewardOdds: the chance falls 10% after a drop and rises 10% after none.
        void RollPotionDrop(SimRng rng, bool elite)
        {
            bool drop = rng.NextDouble() < potionOdds + (elite ? 0.125f : 0f);
            potionOdds += drop ? -0.1f : 0.1f;
            if (!drop) return;
            PotionDef? found = PotionLibrary.Roll(rng);
            if (found == null) return;
            if (found.Id == "FRUIT_JUICE")
            {
                // Drunk on the spot: max HP goes up for good.
                int gain = (int)Math.Round(5 * PlayerHpScale);
                maxHp += gain; hp += gain;
            }
            else if (potions.Count < potionSlots) potions.Add(found);
        }

        // What a typical player does in a shop: remove a Strike/Defend (or a curse) first, then buy a relic that does something,
        // then cards the community would take over skipping, then potions if a slot is free.
        bool RemoveWorstCard()
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

        void VisitShop(SimRng rng)
        {
            List<ShopItem> stock = ShopModel.Generate(_pool, _relicPool, _ascension, owned, rng);
            for (int guard = 0; guard < 20; guard++)
            {
                int removal = ShopModel.RemovalPrice(_ascension, removalsUsed);
                if (gold >= removal && RemoveWorstCard()) { gold -= removal; removalsUsed++; continue; }

                ShopItem? relic = stock.Where(i => i.Kind == ShopKind.Relic && i.Price <= gold && RelicRules.Parse(i.Id) != RelicKind.Unknown).OrderByDescending(i => i.Price).FirstOrDefault();
                if (relic != null) { gold -= relic.Price; stock.Remove(relic); Acquire(relic.Id, rng); continue; }

                double bar = PickPolicy.SkipElo(deck.Count);
                ShopItem? card = stock.Where(i => i.Kind == ShopKind.Card && i.Price <= gold && _pool.HasElo(i.Id) && _pool.Elo(i.Id) >= bar).OrderByDescending(i => _pool.Elo(i.Id)).FirstOrDefault();
                if (card != null) { gold -= card.Price; stock.Remove(card); deck.Add(_data.Cards.Get(card.Id, false)); continue; }

                ShopItem? potion = potions.Count < potionSlots ? stock.Where(i => i.Kind == ShopKind.Potion && i.Price <= gold).OrderByDescending(i => i.Price).FirstOrDefault() : null;
                if (potion != null && PotionLibrary.Find(potion.Id) is { } bought)
                {
                    gold -= potion.Price;
                    stock.Remove(potion);
                    if (bought.Id == "FRUIT_JUICE") GainMaxHp(5); else potions.Add(bought);
                    continue;
                }
                break;
            }
        }

        // The end-of-act deck fights a few next-act elites, each from the same post-boss HP, so decks that scale (and survive
        // Act 1 only barely) are told apart from decks that just get through it.
        void Probe()
        {
            if (_nextElitePool.Length == 0) return;
            int postBossHp = hp + (int)Math.Round((maxHp - hp) * (_ascension >= 2 ? 0.8 : 1.0));   // the next act's Ancient heals 80% of the missing HP from A2
            double rescale = ProbeHpScale / PlayerHpScale;
            int probeMax = (int)Math.Round(maxHp * rescale), start = (int)Math.Round(postBossHp * rescale);
            var order = _nextElitePool.ToList();
            new SimRng(SimRng.Mix(seed, 0x9B0BE)).Shuffle(order);
            for (int i = 0; i < ProbeFightCount && i < order.Count; i++)
            {
                if (!_data.Encounters.Contains(order[i])) continue;
                EncounterDef encounter = _data.Encounters.Get(order[i]);
                ulong probeSeed = SimRng.Mix(seed, 0x9B0BE + (ulong)i + 1);
                string[] lineup = encounter.Generate(new SimRng(SimRng.Mix(probeSeed, 1)));
                if (lineup.Any(m => !_data.Monsters.Contains(m))) continue;
                FightResult r = FightSimulator.Run(deck, start, probeMax, lineup.Select(_data.Monsters.Get), _ascension, probeSeed, _bot, altStarts: encounter.AltStarts,
                    services: _data.Services, potions: potions.ToList(), stakes: 1, relics: relics, hpScale: ProbeHpScale);
                probeFights++;
                if (r.Won) probeWins++;
            }
        }

        bool Fight(string encounterId, RewardKind? reward)
        {
            fights++;
            fought.Add(encounterId);
            if (!_data.Encounters.Contains(encounterId)) { unmodelled++; return true; }
            EncounterDef encounter = _data.Encounters.Get(encounterId);
            ulong fightSeed = SimRng.Mix(seed, (ulong)fights * 7919);
            string[] lineup = encounter.Generate(new SimRng(SimRng.Mix(fightSeed, 1)));
            if (lineup.Any(m => !_data.Monsters.Contains(m))) { unmodelled++; return true; }

            int hpBefore = hp;
            int stakes = encounter.RoomType switch { "Boss" => 2, "Elite" => 1, _ => 0 };
            FightResult result = FightSimulator.Run(deck, hp, maxHp, lineup.Select(_data.Monsters.Get), _ascension, fightSeed, _bot, altStarts: encounter.AltStarts, services: _data.Services, potions: potions, stakes: stakes, relics: relics, hpScale: PlayerHpScale);
            log.Add(new FightLogEntry(encounterId, Real(hpBefore), result.Won ? Real(result.HpAfter) : 0, deck.Count));
            if (!result.Won) { diedTo = encounterId; hp = 0; return false; }

            won++;
            potions = result.PotionsLeft?.ToList() ?? potions;
            hp = Math.Min(maxHp, result.HpAfter);   // Burning Blood and Meat on the Bone already healed inside the combat
            {
                (int lo, int hi) = encounter.RoomType switch { "Elite" => (35, 45), "Boss" => (100, 100), _ => (10, 20) };
                double poverty = _ascension >= 3 ? 0.75 : 1.0;   // Ascension 3 (Poverty)
                gold += new SimRng(SimRng.Mix(fightSeed, 0x601D)).NextInclusive((int)(lo * poverty), (int)(hi * poverty));
            }
            if (reward is RewardKind dropKind) RollPotionDrop(new SimRng(SimRng.Mix(fightSeed, 0x9071)), dropKind == RewardKind.Elite);
            if (reward == RewardKind.Elite)
            {
                var relicRng = new SimRng(SimRng.Mix(fightSeed, 0x2E11C));
                Acquire(_relicPool.Roll(relicRng, owned), relicRng);
            }
            if (reward is RewardKind kind)
            {
                var rewardRng = new SimRng(SimRng.Mix(fightSeed, 0xCA2D));
                string[] offer = _pool.GenerateOffer(kind, rarity, rewardRng);
                int pick = PickPolicy.Choose(offer, _pool, deck.Count, rewardRng);
                if (pick >= 0) deck.Add(_data.Cards.Get(offer[pick], false));
            }
            return true;
        }

        // Relics the decision itself hands out (an Ancient's choice, a shop purchase) take effect before anything else happens.
        foreach (string id in start.AcquireOnStart) Acquire(id, new SimRng(SimRng.Mix(seed, 0xA0C)));

        for (int step = 0; step < 40 && _points.TryGetValue(at, out MapPointSnapshot? here) && here.Children.Count > 0; step++)
        {
            var reachable = here.Children.Where(_points.ContainsKey).ToList();
            if (reachable.Count == 0) break;
            at = step == 0 && start.ForcedNext is MapCoordinate forced && reachable.Contains(forced) ? forced : ChooseNext(reachable, hp, maxHp, pathRng);
            MapPointSnapshot node = _points[at];
            string type = node.Type;

            if (type == "Unknown")
            {
                type = RollUnknown(ref mOdds, ref tOdds, ref sOdds, pathRng);
                if (Own(RelicKind.Planisphere)) hp = Math.Min(maxHp, hp + Scaled(5));
            }

            switch (type)
            {
                case "Monster":
                    if (!Fight(NextOf(normals, ref normalDrawn, () => Sample(_normalPool, pathRng)), RewardKind.Normal)) return Result(false);
                    break;
                case "Elite":
                    if (!Fight(NextOf(elites, ref eliteDrawn, () => Sample(_elitePool, pathRng)), RewardKind.Elite)) return Result(false);
                    break;
                case "RestSite":
                    if (Own(RelicKind.EternalFeather)) hp = Math.Min(maxHp, hp + Scaled(3 * (deck.Count / 5)));
                    if (hp < 0.5 * maxHp) hp = Math.Min(maxHp, hp + (int)(0.3 * maxHp) + (Own(RelicKind.RegalPillow) ? Scaled(15) : 0));
                    else UpgradeBest(deck);
                    break;
                case "Shop":
                    VisitShop(pathRng);
                    break;
                case "Treasure":
                    Acquire(_relicPool.Roll(pathRng, owned), pathRng);
                    break;
                case "Boss":
                    foreach (string boss in bosses)
                        if (!Fight(boss, null)) return Result(false);
                    Probe();
                    return Result(true);
            }
        }
        // Ran out of map without meeting a boss node (shouldn't happen): count it as reaching the end.
        return Result(true);

        RolloutResult Result(bool survived) => new(survived, Math.Max(0, Real(hp)), Real(maxHp), won, fights, diedTo, unmodelled, fought, log, probeFights, probeWins);

        int Real(int scaled) => (int)Math.Round(scaled / PlayerHpScale);
    }

    private MapCoordinate StartPoint()
    {
        MapPointSnapshot? ancient = _points.Values.FirstOrDefault(p => p.Type == "Ancient");
        MapPointSnapshot start = ancient ?? _points.Values.OrderBy(p => p.Row).ThenBy(p => p.Col).First();
        return new MapCoordinate(start.Col, start.Row);
    }

    private static string NextOf(List<string> sequence, ref int drawn, Func<string> sample) =>
        drawn < sequence.Count ? sequence[drawn++] : sample();

    private static string Sample(string[] pool, SimRng rng) => pool.Length == 0 ? "" : pool[rng.Next(pool.Length)];

    private List<string> SampleNormals(SimRng rng, int alreadyFought)
    {
        var weak = _weakPool.ToList(); rng.Shuffle(weak);
        var normal = _normalPool.ToList(); rng.Shuffle(normal);
        var all = weak.Take(3).Concat(normal).ToList();
        return all.Skip(Math.Min(alreadyFought, all.Count)).ToList();
    }

    private static List<string> SampleFrom(string[] pool, SimRng rng)
    {
        var list = pool.ToList();
        rng.Shuffle(list);
        return list;
    }

    /// <summary>What a typical player thinks a room is worth on the way to the boss (HP-equivalents, rough).</summary>
    private static double RoomValue(string type) => type switch
    {
        "Monster" => -1.0,
        "Elite" => -2.0,        // costs a lot of HP but pays a relic and a better card reward
        "RestSite" => 2.5,
        "Shop" => 1.0,
        "Treasure" => 1.5,
        "Unknown" => 0.0,
        _ => 0.0,
    };

    private Dictionary<MapCoordinate, double>? _pathValue;

    /// <summary>Best total room value from a node to the end of the map (memoized; the map never changes).</summary>
    private double PathValue(MapCoordinate at)
    {
        _pathValue ??= new Dictionary<MapCoordinate, double>();
        lock (_pathValue)
        {
            if (_pathValue.TryGetValue(at, out double cached)) return cached;
            MapPointSnapshot point = _points[at];
            double best = 0;
            var reachable = point.Children.Where(_points.ContainsKey).ToList();
            if (reachable.Count > 0) best = reachable.Max(PathValue);
            return _pathValue[at] = RoomValue(point.Type) + best;
        }
    }

    /// <summary>
    /// A planning player's route: mostly follows the branch with the best overall mix of rests, shops and fights ahead,
    /// with some randomness (players differ), and steers away from elites when hurt.
    /// </summary>
    private MapCoordinate ChooseNext(List<MapCoordinate> children, int hp, int maxHp, SimRng rng)
    {
        if (children.Count == 1) return children[0];
        double fraction = (double)hp / maxHp;
        double Score(MapCoordinate c)
        {
            double v = PathValue(c);
            if (_points[c].Type == "Elite" && fraction < 0.7) v -= 4;
            if (_points[c].Type == "RestSite" && fraction < 0.6) v += 2;
            return v;
        }
        const double temperature = 1.5;
        double top = children.Max(Score);
        var weights = children.Select(c => Math.Exp((Score(c) - top) / temperature)).ToList();
        double roll = rng.NextDouble() * weights.Sum();
        for (int i = 0; i < children.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0) return children[i];
        }
        return children[^1];
    }

    /// <summary>Rolls what a "?" room turns out to be, updating the odds counters exactly as the game does.</summary>
    private static string RollUnknown(ref float monster, ref float treasure, ref float shop, SimRng rng)
    {
        double r = rng.NextDouble();
        string rolled = r <= monster ? "Monster" : r <= monster + treasure ? "Treasure" : r <= monster + treasure + shop ? "Shop" : "Event";
        monster = rolled == "Monster" ? MonsterBase : monster + MonsterBase;
        treasure = rolled == "Treasure" ? TreasureBase : treasure + TreasureBase;
        shop = rolled == "Shop" ? ShopBase : shop + ShopBase;
        return rolled;
    }

    /// <summary>Default rest-site upgrade: the card that is best liked (by Elo), Bash first among the starting cards.</summary>
    private void UpgradeBest(List<CardDef> deck)
    {
        int best = -1;
        double bestScore = double.MinValue;
        for (int i = 0; i < deck.Count; i++)
        {
            CardDef card = deck[i];
            if (card.Upgraded || card.Kind is CardKind.Status or CardKind.Curse) continue;
            double score = card.Id == "BASH" ? 1700 : _pool.HasElo(card.Id) ? _pool.Elo(card.Id) : 1000;
            if (score > bestScore) { bestScore = score; best = i; }
        }
        if (best >= 0) deck[best] = _data.Cards.Get(deck[best].Id, true);
    }
}
