using SpireMonteCarlo.Contracts;

namespace SpireMonteCarlo.Sim;

public sealed record FightLogEntry(string Encounter, int HpBefore, int HpAfter, int DeckSize);

public sealed record RolloutResult(bool Survived, int HpEnd, int MaxHp, int FightsWon, int FightsTotal, string? DiedTo, int UnmodelledFights, IReadOnlyList<string> Encounters, IReadOnlyList<FightLogEntry> Log);

/// <summary>
/// One simulated future of the current act, from the snapshot's position to the end of the act's boss fight.
/// The path across the map is sampled, fights use the game's real upcoming encounters when the snapshot has them,
/// and later card rewards, rest sites, and "?" rooms follow default policies. Two runs with the same seed see the
/// same luck, so different decks can be compared fairly.
/// </summary>
public sealed class ActRollout
{
    private const int BurningBloodHeal = 6;
    private const float MonsterBase = 0.1f, TreasureBase = 0.02f, ShopBase = 0.03f;

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

    public const double CalibratedPlayerHpScale = 2.0;
    private readonly RewardPool _pool;
    private readonly int _ascension;
    private readonly bool _burningBlood;
    private readonly string[] _weakPool, _normalPool, _elitePool, _bossPool;

    public ActRollout(SimData data, RunSnapshot snapshot)
    {
        _data = data;
        _snap = snapshot;
        _ascension = snapshot.Run.Ascension;
        _pool = data.PoolFor(snapshot.Run.Character);
        _burningBlood = snapshot.Relics.Contains("BURNING_BLOOD");
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
    }

    private static string DefaultVariant(int act) => act switch { 1 => "Overgrowth", 2 => "Hive", _ => "Glory" };

    /// <summary>True when the snapshot lists the actual upcoming encounters (otherwise they are sampled).</summary>
    public bool HasExactPlan => _snap.Plan is { Normal.Count: > 0 } || _snap.Plan?.Boss != null;

    public RolloutResult Run(IReadOnlyList<CardDef> startDeck, ulong seed)
    {
        var deck = startDeck.ToList();
        int hp = (int)Math.Round(_snap.Run.CurrentHp * PlayerHpScale), maxHp = (int)Math.Round(_snap.Run.MaxHp * PlayerHpScale);
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
            FightResult result = FightSimulator.Run(deck, hp, maxHp, lineup.Select(_data.Monsters.Get), _ascension, fightSeed, _bot, altStarts: encounter.AltStarts);
            log.Add(new FightLogEntry(encounterId, Real(hpBefore), result.Won ? Real(result.HpAfter) : 0, deck.Count));
            if (!result.Won) { diedTo = encounterId; hp = 0; return false; }

            won++;
            hp = Math.Min(maxHp, result.HpAfter + (_burningBlood ? (int)Math.Round(BurningBloodHeal * PlayerHpScale) : 0));
            if (reward is RewardKind kind)
            {
                var rewardRng = new SimRng(SimRng.Mix(fightSeed, 0xCA2D));
                string[] offer = _pool.GenerateOffer(kind, rarity, rewardRng);
                int pick = PickPolicy.Choose(offer, _pool, deck.Count, rewardRng);
                if (pick >= 0) deck.Add(_data.Cards.Get(offer[pick], false));
            }
            return true;
        }

        for (int step = 0; step < 40 && _points.TryGetValue(at, out MapPointSnapshot? here) && here.Children.Count > 0; step++)
        {
            var reachable = here.Children.Where(_points.ContainsKey).ToList();
            if (reachable.Count == 0) break;
            at = ChooseNext(reachable, hp, maxHp, pathRng);
            MapPointSnapshot node = _points[at];
            string type = node.Type;

            if (type == "Unknown")
            {
                type = RollUnknown(ref mOdds, ref tOdds, ref sOdds, pathRng);
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
                    if (hp < 0.5 * maxHp) hp = Math.Min(maxHp, hp + (int)(0.3 * maxHp));
                    else UpgradeBest(deck);
                    break;
                case "Boss":
                    foreach (string boss in bosses)
                        if (!Fight(boss, null)) return Result(false);
                    return Result(true);
            }
        }
        // Ran out of map without meeting a boss node (shouldn't happen): count it as reaching the end.
        return Result(true);

        RolloutResult Result(bool survived) => new(survived, Math.Max(0, Real(hp)), Real(maxHp), won, fights, diedTo, unmodelled, fought, log);

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
