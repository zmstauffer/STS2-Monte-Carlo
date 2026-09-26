using SpireMonteCarlo.Contracts;

namespace SpireMonteCarlo.Sim;

/// <summary>One fight of a rollout, HP in real (unscaled) points; HpLost is what the enemies took off (capped at the HP the player had), before any healing.</summary>
public sealed record FightLogEntry(string Encounter, int HpBefore, int HpAfter, int DeckSize, int HpLost = 0, int Turns = 0);

public sealed record RolloutResult(bool Survived, int HpEnd, int MaxHp, int FightsWon, int FightsTotal, string? DiedTo, int UnmodelledFights, IReadOnlyList<string> Encounters, IReadOnlyList<FightLogEntry> Log, int ProbeFights = 0, int ProbeWins = 0, int DeckSize = 0, int UpgradedCards = 0, int Relics = 0, RolloutStart? End = null);

// (End is the run as the next act starts, when the boss was beaten: deck, relics, potions, gold, and HP after the next Ancient's heal.)

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
    public double Hp { get; set; }
    public double MaxHp { get; set; }
    public int Gold { get; set; }
    public List<string> Relics { get; init; } = new();
    public List<string> Potions { get; init; } = new();
    public int RemovalsUsed { get; set; }

    /// <summary>Relics gained by the decision that still need their pickup effect (max HP, potion slots, upgrades).</summary>
    public List<string> AcquireOnStart { get; init; } = new();

    /// <summary>The node the player walks to first, instead of the route policy's choice.</summary>
    public MapCoordinate? ForcedNext { get; set; }

    /// <summary>An event option to play out at the start of each rollout, so its random parts (a relic roll, gold amounts) are averaged over the futures.</summary>
    public EventOptionDef? EventEffect { get; set; }

    /// <summary>Combats the decision itself forces (an event's fight), fought before the first step.</summary>
    public List<PendingFight> PendingFights { get; init; } = new();

    public RolloutStart Copy() => new()
    {
        Deck = Deck.ToList(), Hp = Hp, MaxHp = MaxHp, Gold = Gold, Relics = Relics.ToList(), Potions = Potions.ToList(),
        RemovalsUsed = RemovalsUsed, AcquireOnStart = AcquireOnStart.ToList(), ForcedNext = ForcedNext, PendingFights = PendingFights.ToList(), EventEffect = EventEffect,
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

    /// <summary>Whether a run from the very start of Act 1 takes a Neow boon first (advice for the Neow choice itself turns this off, since it evaluates the options).</summary>
    public bool DefaultNeow { get; set; } = true;

    /// <summary>Debugging: print each fight as it starts (finds a rollout that hangs).</summary>
    public static bool TraceFights { get; set; }

    /// <summary>
    /// Multiplies the player's HP pool (current, max, and every heal) inside the simulation, so every HP amount stays in
    /// proportion: enemy damage, self-inflicted HP loss from cards, and healing. Tuned so a simulated Ironclad survives
    /// Act 1 about as often as real players (boss deaths near the real ~16%). It measures what the simulator does not
    /// model yet (potions, relics, events, better play): 1.0 would mean nothing is missing. Reported HP is converted back.
    /// </summary>
    public double PlayerHpScale { get => _playerHpScale ?? CalibratedScaleFor(_snap.Run.Act); init => _playerHpScale = value; }
    private readonly double? _playerHpScale;

    /// <summary>The fitted scale for an act. Act 2 needs more than Act 1: its enemies hit harder than the decks the reward policy builds can answer.</summary>
    public static double CalibratedScaleFor(int act) => act <= 1 ? CalibratedPlayerHpScale : CalibratedPlayerHpScaleAct2;

    // Re-fit after the leaf-value planner (with draw and replan-energy values), Neow boons, generated maps (calibrate-run --maps 200) and the pre-boss rest:
    // 1.7 gives 64% Act 1 survival, 1.72 about 65% (real ~65%), 1.9 gives 74%. It was 2.5 with the older bot, so about a third of the old fudge was bot play.
    // Per fight the sim now matches real weak-fight damage but is lighter in elites and normal fights and still too lethal in bosses
    // (deaths split 3% normal / 12% elite / 21% boss against roughly 25/35/40% real), so the scale still hides unmodelled resources.
    public const double CalibratedPlayerHpScale = 1.72;

    // Act 2, for the runs that beat Act 1 (sim calibrate-run --maps 200 --act2): at 3.4 Act 2 survival is 57%, at 3.7 64%, at 4.0 68% (real ~61%).
    // The Decimillipede (26% vs 12% real), Insatiable (27% vs 16%) and Knowledge Demon (27% vs 20%) stay too lethal at any scale (the bot
    // can't line up the Decimillipede's segments, and the simulated decks are weaker than real Act 2 decks); Kaiser Crab (8% vs 24%) and the
    // Prisms are too easy. There is no Act 3 content yet.
    public const double CalibratedPlayerHpScaleAct2 = 3.5;

    /// <summary>
    /// The HP scale used in the next-act probe. Act 2 enemies hit harder than the Act 1 scale can absorb for a deck that has
    /// only Act 1's cards and relics, so the probe is fit separately: real Ironclad players lose to Act 2 elites 7-12% of the time.
    /// </summary>
    public double ProbeHpScale { get; init; } = CalibratedProbeHpScale;

    // Re-fit once the Act 2 monsters had their real mechanics (Entomancer's hive, Prism's Vital Spark, ...): 2.5 gives 83% wins,
    // 3.0 gives 91%, 3.5 gives 96% against the two elites left in the pool (real Act 2 elite fatal rates are 6-7%).
    public const double CalibratedProbeHpScale = 3.0;
    private readonly RewardPool _pool;
    private readonly int _ascension;
    private readonly RelicPool _relicPool;
    private readonly string _variant;
    private readonly string[] _weakPool, _normalPool, _elitePool, _bossPool, _nextElitePool;

    /// <summary>
    /// Elites left out of the probe: the bot can't play the Decimillipede (it would need to bring all three segments down together), so it
    /// loses to it however good the deck, which tells the probe nothing about the deck.
    /// </summary>
    private static readonly HashSet<string> ProbeSkips = new() { "DECIMILLIPEDE_ELITE" };

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
        _variant = variant;
        IEnumerable<EncounterDef> actEncounters = data.Encounters.All.Where(e => (e.Act ?? "").Replace(" ", "").Contains(variant, StringComparison.OrdinalIgnoreCase));
        _weakPool = actEncounters.Where(e => e.RoomType == "Monster" && e.IsWeak).Select(e => e.Id).ToArray();
        _normalPool = actEncounters.Where(e => e.RoomType == "Monster" && !e.IsWeak).Select(e => e.Id).ToArray();
        _elitePool = actEncounters.Where(e => e.RoomType == "Elite").Select(e => e.Id).ToArray();
        _bossPool = actEncounters.Where(e => e.RoomType == "Boss").Select(e => e.Id).ToArray();

        string nextVariant = snapshot.Run.Act < 3 ? DefaultVariant(snapshot.Run.Act + 1) : "";
        _nextElitePool = nextVariant == "" ? Array.Empty<string>()
            : data.Encounters.All.Where(e => e.RoomType == "Elite" && !ProbeSkips.Contains(e.Id) && (e.Act ?? "").Replace(" ", "").Contains(nextVariant, StringComparison.OrdinalIgnoreCase)).Select(e => e.Id).ToArray();
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
        List<string> eventQueue = _snap.Plan is { Events.Count: > 0 } ? _snap.Plan.Events.ToList() : SampleFrom(EventLibrary.Pool(_variant).ToArray(), setupRng);
        int eventDrawn = 0;
        int normalDrawn = 0, eliteDrawn = 0, fights = 0, won = 0, unmodelled = 0;
        var fought = new List<string>();
        var log = new List<FightLogEntry>();

        MapCoordinate at = _snap.Map.Current ?? StartPoint();
        string? diedTo = null;
        int probeFights = 0, probeWins = 0;
        RolloutStart? endState = null;

        var potions = start.Potions.Select(PotionLibrary.Find).OfType<PotionDef>().ToList();
        float potionOdds = PotionBaseOdds;
        int potionSlots = PotionSlots;

        int gold = start.Gold, removalsUsed = start.RemovalsUsed, monsterFights = 0;
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
            if (NeowBoons.Has(id)) ApplyBoon(id, rng);
            RelicKind kind = RelicRules.Parse(id);
            if (kind == RelicKind.Unknown) return;
            relics.Add(kind);
            switch (kind)
            {
                case RelicKind.Strawberry: GainMaxHp(7); break;
                case RelicKind.Pear: GainMaxHp(10); break;
                case RelicKind.Mango: GainMaxHp(14); break;
                case RelicKind.PotionBelt: potionSlots += 2; break;
                case RelicKind.OldCoin: gold += 300; break;
                case RelicKind.WarPaint: UpgradeRandom(CardKind.Skill, 2, rng); break;
                case RelicKind.Whetstone: UpgradeRandom(CardKind.Attack, 2, rng); break;
            }
        }

        // A card taken into the deck is upgraded on the way in by the matching Egg.
        void AddToDeck(string id)
        {
            CardDef card = _data.Cards.Get(id, false);
            if (card.UpgradedForm != null && DeckPolicies.EggUpgrades(card.Kind, owned)) card = _data.Cards.Get(id, true);
            deck.Add(card);
        }

        // A Neow boon does its pickup effect through the same event machinery as everything else that edits the deck.
        void ApplyBoon(string id, SimRng rng)
        {
            EventState st = MakeEventState(rng, 0);
            NeowBoons.Apply(id, st);
            ApplyEventState(st, "NEOW", rng);
        }

        void UpgradeAnyRandom(SimRng rng)
        {
            var candidates = Enumerable.Range(0, deck.Count).Where(i => !deck[i].Upgraded && deck[i].UpgradedForm != null).ToList();
            if (candidates.Count == 0) return;
            int pick = candidates[rng.Next(candidates.Count)];
            deck[pick] = _data.Cards.Get(deck[pick].Id, true);
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
        bool RemoveWorstCard() => DeckPolicies.RemoveWorst(deck);

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
                if (card != null) { gold -= card.Price; stock.Remove(card); AddToDeck(card.Id); continue; }

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

        // Events: the game draws them from a shuffled list, skipping any whose conditions aren't met; the player takes the
        // option a typical player would (see EventLibrary). Events the library doesn't have use up their slot and change nothing.
        EventState MakeEventState(SimRng rng, int step) => new()
        {
            Data = _data, Pool = _pool, RelicPool = _relicPool, Rng = rng, Deck = deck,
            Act = _snap.Run.Act, Ascension = _ascension, Floor = _snap.Run.TotalFloor + step,
            Hp = hp / PlayerHpScale, MaxHp = maxHp / PlayerHpScale, Gold = gold,
            Relics = owned.ToList(), Potions = potions.Select(p => p.Id).ToList(), PotionSlots = potionSlots,
        };

        bool RunPendingFight(PendingFight pending, SimRng rng)
        {
            if (!Fight(pending.Encounter, null)) return false;
            if (pending.RelicReward) Acquire(_relicPool.Roll(rng, owned), rng);
            if (pending.PotionReward && PotionLibrary.Roll(rng) is { } reward && potions.Count < potionSlots) potions.Add(reward);
            return true;
        }

        bool ApplyEventState(EventState st, string eventId, SimRng rng)
        {
            hp = (int)Math.Round(st.Hp * PlayerHpScale);
            maxHp = (int)Math.Round(st.MaxHp * PlayerHpScale);
            if (st.Hp > 0 && hp < 1) hp = 1;
            gold = st.Gold;
            potions = st.Potions.Select(PotionLibrary.Find).OfType<PotionDef>().ToList();
            foreach (string relic in st.PendingRelics) Acquire(relic, rng);
            if (hp <= 0) { diedTo = "EVENT_" + eventId; return false; }
            foreach (PendingFight pending in st.PendingFights)
                if (!RunPendingFight(pending, rng)) return false;
            return true;
        }

        bool Kills(EventOptionDef option, EventState st)
        {
            EventState trial = st.Copy();
            option.Apply(trial);
            return trial.Hp <= 0;
        }

        bool VisitEvent(SimRng rng, int step)
        {
            while (eventDrawn < eventQueue.Count)
            {
                string id = eventQueue[eventDrawn++];
                EventDef? def = EventLibrary.Find(id);
                if (def == null) return true;
                EventState st = MakeEventState(rng, step);
                if (!def.Allowed(st)) continue;
                var options = def.Options(st).Where(o => o.Enabled).ToList();
                string key = def.Default(st);
                EventOptionDef? chosen = options.FirstOrDefault(o => o.Key == key) ?? options.FirstOrDefault();
                // Nobody takes an option that kills them when another one doesn't.
                if (chosen != null && Kills(chosen, st))
                    chosen = options.FirstOrDefault(o => !Kills(o, st)) ?? chosen;
                chosen?.Apply(st);
                return ApplyEventState(st, id, rng);
            }
            return true;
        }

        // The end-of-act deck fights a few next-act elites, each from the same post-boss HP, so decks that scale (and survive
        // Act 1 only barely) are told apart from decks that just get through it.
        int PostBossHp() => hp + (int)Math.Round((maxHp - hp) * (_ascension >= 2 ? 0.8 : 1.0));   // the next act's Ancient heals 80% of the missing HP from A2

        void Probe()
        {
            if (_nextElitePool.Length == 0 || _snap.Run.Act >= 2) return;   // Act 3's monsters aren't modelled yet, so a probe of them would mean nothing
            int postBossHp = PostBossHp();
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

            if (TraceFights) Console.Error.WriteLine($"  fight {encounterId} lineup {string.Join("+", lineup)} hp {hp}/{maxHp} deck {string.Join(",", deck.Select(c => c.ToString()))} potions {string.Join(",", potions.Select(p => p.Id))} relics {string.Join(",", owned)}");
            int hpBefore = hp;
            int stakes = encounter.RoomType switch { "Boss" => 2, "Elite" => 1, _ => 0 };
            FightResult result = FightSimulator.Run(deck, hp, maxHp, lineup.Select(_data.Monsters.Get), _ascension, fightSeed, _bot, altStarts: encounter.AltStarts, services: _data.Services, potions: potions, stakes: stakes, relics: relics, hpScale: PlayerHpScale);
            log.Add(new FightLogEntry(encounterId, Real(hpBefore), result.Won ? Real(result.HpAfter) : 0, deck.Count, Real(Math.Min(result.HpLost, hpBefore)), result.Turns));
            if (!result.Won) { diedTo = encounterId; hp = 0; return false; }

            won++;
            if (encounter.RoomType == "Monster" && Own(RelicKind.FishingRod) && ++monsterFights % 3 == 0) UpgradeAnyRandom(new SimRng(SimRng.Mix(fightSeed, 0xF15)));
            potions = result.PotionsLeft?.ToList() ?? potions;
            foreach (string lost in result.LostCards ?? Array.Empty<string>())   // a thief got away with it
            {
                int i = deck.FindIndex(c => c.Id == lost);
                if (i >= 0) deck.RemoveAt(i);
            }
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
                // Prayer Wheel adds a second card reward after ordinary fights; White Star adds a rare-only one after elites.
                int screens = 1 + (kind == RewardKind.Normal && Own(RelicKind.PrayerWheel) ? 1 : 0);
                for (int screen = 0; screen < screens; screen++)
                {
                    string[] offer = _pool.GenerateOffer(kind, rarity, rewardRng);
                    int pick = PickPolicy.Choose(offer, _pool, deck.Count, rewardRng);
                    if (pick >= 0) AddToDeck(offer[pick]);
                }
                if (kind == RewardKind.Elite && Own(RelicKind.WhiteStar))
                {
                    var rares = new List<string>();
                    for (int i = 0; i < 3; i++) if (_pool.RollClass(CardRarity.Rare, rewardRng, rares) is { } rare) rares.Add(rare);
                    int pick = PickPolicy.Choose(rares, _pool, deck.Count, rewardRng);
                    if (pick >= 0) AddToDeck(rares[pick]);
                }
            }
            return true;
        }

        // Relics the decision itself hands out (an Ancient's choice, a shop purchase) take effect before anything else happens.
        if (start.EventEffect is { } chosenEvent)
        {
            var eventRng = new SimRng(SimRng.Mix(seed, 0xE7E));
            EventState st = MakeEventState(eventRng, 0);
            chosenEvent.Apply(st);
            if (!ApplyEventState(st, "CHOSEN", eventRng)) return Result(false);
        }
        foreach (string id in start.AcquireOnStart) Acquire(id, new SimRng(SimRng.Mix(seed, 0xA0C)));
        // A run that is just starting takes one of Neow's boons first, the way a typical player would.
        if (DefaultNeow && _snap.Run.Act == 1 && _snap.Map.Current == null && start.Relics.Count <= 1 && start.AcquireOnStart.Count == 0 && start.EventEffect == null)
        {
            var neowRng = new SimRng(SimRng.Mix(seed, 0x4E30));
            Acquire(NeowBoons.Choose(NeowBoons.Offer(neowRng), neowRng), neowRng);
        }
        foreach (PendingFight pending in start.PendingFights)
            if (!RunPendingFight(pending, new SimRng(SimRng.Mix(seed, 0xF16))))
                return Result(false);

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
                    // The rest site right before the boss is for healing; the others heal when the player is hurt and upgrade otherwise.
                    bool beforeBoss = _snap.Map!.Boss is MapCoordinate bossAt && node.Row >= bossAt.Row - 1;
                    if (hp < (beforeBoss ? 0.9 : 0.6) * maxHp)
                    {
                        hp = Math.Min(maxHp, hp + (int)(0.3 * maxHp) + (Own(RelicKind.RegalPillow) ? Scaled(15) : 0));
                        if (Own(RelicKind.StoneHumidifier)) GainMaxHp(5);
                    }
                    else if (Own(RelicKind.Shovel)) Acquire(_relicPool.Roll(pathRng, owned), pathRng);   // dig up a relic instead of upgrading
                    else UpgradeBest(deck);
                    break;
                case "Shop":
                    VisitShop(pathRng);
                    break;
                case "Event":
                    if (!VisitEvent(pathRng, step)) return Result(false);
                    break;
                case "Treasure":
                    Acquire(_relicPool.Roll(pathRng, owned), pathRng);
                    break;
                case "Boss":
                    foreach (string boss in bosses)
                        if (!Fight(boss, null)) return Result(false);
                    endState = new RolloutStart
                    {
                        Deck = deck.ToList(), Hp = PostBossHp() / PlayerHpScale, MaxHp = maxHp / PlayerHpScale, Gold = gold,
                        Relics = owned.ToList(), Potions = potions.Select(p => p.Id).ToList(),
                    };
                    Probe();
                    return Result(true);
            }
        }
        // Ran out of map without meeting a boss node (shouldn't happen): count it as reaching the end.
        return Result(true);

        RolloutResult Result(bool survived) => new(survived, Math.Max(0, Real(hp)), Real(maxHp), won, fights, diedTo, unmodelled, fought, log, probeFights, probeWins, deck.Count, deck.Count(c => c.Upgraded), relics.Count, endState);

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

    /// <summary>
    /// What a typical player thinks a room is worth on the way to the boss (HP-equivalents, rough). Early fights are wanted for their card
    /// rewards; elites are avoided until the deck has had time to grow, then taken for the relic.
    /// </summary>
    private static double RoomValue(string type, int row) => type switch
    {
        "Monster" => row <= 6 ? 1.0 : row <= 10 ? 0.0 : -0.5,
        "Elite" => row < 5 ? -8.0 : row < 9 ? 3.0 : 5.0,
        "RestSite" => 2.5,
        "Shop" => 1.0,
        "Treasure" => 1.5,
        "Unknown" => 0.3,
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
            return _pathValue[at] = RoomValue(point.Type, at.Row) + best;
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
            if (_points[c].Type == "Elite") v -= fraction < 0.7 ? 6 : fraction < 0.85 ? 2 : 0;
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
    private void UpgradeBest(List<CardDef> deck) => DeckPolicies.UpgradeBest(deck, _data, _pool);
}
