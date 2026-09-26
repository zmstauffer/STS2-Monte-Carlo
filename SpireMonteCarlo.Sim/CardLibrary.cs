using System.Text.Json;
using System.Text.RegularExpressions;
using SpireMonteCarlo.Codex;

namespace SpireMonteCarlo.Sim;

/// <summary>
/// Builds <see cref="CardDef"/>s from Codex card data. Most cards are just numbers (damage, block, draw, energy,
/// powers), so their effects are derived automatically and stay in sync when the game is patched. Cards whose text
/// says more than that are flagged <see cref="CardDef.Approximate"/> unless a hand-written override exists.
/// </summary>
public sealed class CardLibrary
{
    private static readonly Regex ComplexText = new(
        @"Fatal|for each|equal to|random|Whenever|At the (start|end)|Choose|Discover|Transform|Upgrade|Exhaust (a|your|all|the)|Copy|Double|Play the|Put |Add |from your (discard|exhaust|draw)|next turn|this combat|this turn|cannot|Retain|Innate|if |If |Shuffle|Heal|Max HP",
        RegexOptions.Compiled);

    private static readonly Regex EndOfTurnInHand = new(@"At the end of your turn, if this is in your (\[gold\])?Hand", RegexOptions.Compiled);

    private readonly IReadOnlyDictionary<string, CodexCard> _codex;
    private readonly Dictionary<(string, bool), CardDef> _cache = new();
    private readonly HashSet<string> _unknown = new();

    private readonly IReadOnlyDictionary<string, ExtractedCard> _game;
    private readonly Dictionary<string, IReadOnlyList<CardDef>> _pools = new();

    /// <param name="codexCards">Codex's card data (text, colors, fallback numbers).</param>
    /// <param name="gameCards">Cards extracted from the decompiled game; the numbers used for every card with a recipe.</param>
    public CardLibrary(IReadOnlyDictionary<string, CodexCard> codexCards, IReadOnlyDictionary<string, ExtractedCard>? gameCards = null)
    {
        _codex = codexCards;
        _game = gameCards ?? new Dictionary<string, ExtractedCard>();
    }

    /// <summary>Card ids that were requested but Codex doesn't know (the game is newer than the export).</summary>
    public IReadOnlyCollection<string> UnknownIds => _unknown;

    public CardDef Get(string id, bool upgraded)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue((id, upgraded), out CardDef? cached)) return cached;
            CardDef def = Special(id, upgraded)
                ?? FromRecipe(id, upgraded)
                ?? (_codex.TryGetValue(id, out CodexCard? card) ? Build(card, upgraded) : Unknown(id, upgraded));
            _cache[(id, upgraded)] = def;
            // Cards built from Codex numbers have an upgraded form too, so rest sites and Armaments-style effects can upgrade them.
            if (!upgraded && def.UpgradedForm == null && def.Kind is CardKind.Attack or CardKind.Skill or CardKind.Power
                && _codex.TryGetValue(id, out CodexCard? source) && source.Upgrade is { Count: > 0 })
                def.UpgradedForm = Get(id, true);
            return def;
        }
    }

    /// <summary>Cards the game defines in ways the Codex numbers can't express (status cards that do something when played).</summary>
    private static CardDef? Special(string id, bool upgraded) => id switch
    {
        // The Insatiable's escape hatch: playing it pushes the sandpit back one turn, and it costs one more every time.
        "FRANTIC_ESCAPE" => new CardDef
        {
            Id = id, Upgraded = upgraded, Kind = CardKind.Status, Cost = 1, CostsMoreEachPlay = true,
            Effects = new[] { new Effect(EffectOp.BuffSelf, 1, Power: PowerKind.Sandpit) },
        },
        _ => null,
    };

    public bool Contains(string id) => _codex.ContainsKey(id) || _game.ContainsKey(id);

    /// <summary>The card's rarity name from the game's class ("Common", "Ancient", "Curse", ...), or "" when unknown.</summary>
    public string RarityOf(string id) => _game.TryGetValue(id, out ExtractedCard? ex) ? ex.Rarity : _codex.TryGetValue(id, out CodexCard? c) ? c.Rarity : "";

    /// <summary>True when the card is built from a hand-checked recipe and the game's own numbers, so nothing about it is approximate.</summary>
    public bool HasRecipe(string id) => _game.TryGetValue(id, out ExtractedCard? ex) && CardRecipes.Get(id, false, VarsOf(ex, false)) != null;

    /// <summary>Cards a random-card effect (Stoke, Infernal Blade) can produce for this character: its pool without Basic, Ancient, Event, and ungeneratable cards.</summary>
    public IReadOnlyList<CardDef> CombatGenerationPool(string character)
    {
        lock (_pools)
        {
            if (_pools.TryGetValue(character, out IReadOnlyList<CardDef>? pool)) return pool;
            pool = _codex.Values
                .Where(c => string.Equals(c.Color, character, StringComparison.OrdinalIgnoreCase))
                .Where(c => _game.TryGetValue(c.Id, out ExtractedCard? ex) && ex.CanBeGeneratedInCombat && !ex.MultiplayerOnly
                            && ex.Rarity is not ("Basic" or "Ancient" or "Event"))
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => Get(c.Id, false))
                .ToList();
            _pools[character] = pool;
            return pool;
        }
    }

    private static Vars VarsOf(ExtractedCard ex, bool upgraded) => name =>
    {
        if (!ex.Vars.TryGetValue(name, out decimal value)) throw new KeyNotFoundException($"{ex.Id} has no variable '{name}'.");
        if (upgraded && ex.UpgradeVars.TryGetValue(name, out decimal delta)) value += delta;
        return (int)value;
    };

    private CardDef? FromRecipe(string id, bool upgraded)
    {
        if (!_game.TryGetValue(id, out ExtractedCard? ex)) return null;
        Recipe? recipe = CardRecipes.Get(id, upgraded, VarsOf(ex, upgraded));
        if (recipe == null) return null;

        var keywords = new HashSet<string>(ex.Keywords);
        if (upgraded)
        {
            keywords.UnionWith(ex.UpgradeAddKeywords);
            keywords.ExceptWith(ex.UpgradeRemoveKeywords);
        }
        CardKind kind = ex.Type switch
        {
            "Attack" => CardKind.Attack,
            "Power" => CardKind.Power,
            "Status" => CardKind.Status,
            "Curse" => CardKind.Curse,
            _ => CardKind.Skill,
        };
        int cost = ex.XCost ? CardDef.XCost
            : keywords.Contains("Unplayable") || ex.Cost < 0 ? CardDef.Unplayable
            : Math.Max(0, ex.Cost + (upgraded ? ex.UpgradeCost : 0));

        var def = new CardDef
        {
            Id = id,
            Upgraded = upgraded,
            Kind = kind,
            Cost = cost,
            Exhaust = keywords.Contains("Exhaust"),
            Ethereal = keywords.Contains("Ethereal"),
            Innate = keywords.Contains("Innate"),
            Retain = keywords.Contains("Retain"),
            IsStrike = ex.Tags.Contains("Strike"),
            Effects = recipe.Effects,
            DamageGrowthPerPlay = recipe.Growth,
            CheaperPerAttackPlayed = recipe.CheaperPerAttack,
            EnergyWhenExhausted = recipe.EnergyOnExhaust,
            PlaysFromExhaustPile = recipe.FromExhaustPile,
            UpgradedForm = !upgraded && kind is CardKind.Attack or CardKind.Skill or CardKind.Power ? FromRecipe(id, true) : null,
        };
        return def;
    }

    private CardDef Unknown(string id, bool upgraded)
    {
        _unknown.Add(id);
        return new CardDef { Id = id, Upgraded = upgraded, Kind = CardKind.Skill, Cost = 1, Approximate = true };
    }

    private static CardDef Build(CodexCard card, bool upgraded)
    {
        int? damage = card.DamageAmount, block = card.BlockAmount;
        int hits = card.Hits ?? 1;
        int? draw = card.CardsDrawAmount, energy = card.EnergyGainAmount, hpLoss = card.HpLossAmount;
        int cost = card.Cost ?? 0;
        bool approximate = card.Damage.ValueKind == JsonValueKind.String || card.Block.ValueKind == JsonValueKind.String;

        var powers = (card.PowersApplied ?? new()).Select(p => (kind: PowerRules.Parse(p.PowerKey ?? p.Power), key: (p.PowerKey ?? p.Power), amount: p.AmountValue)).ToList();

        if (upgraded && card.Upgrade != null)
        {
            foreach ((string key, JsonElement value) in card.Upgrade)
            {
                if (key == "description_changed") { approximate = true; continue; }
                if (!TryDelta(value, out int delta)) continue;
                switch (key)
                {
                    case "damage": damage = (damage ?? 0) + delta; break;
                    case "block": block = (block ?? 0) + delta; break;
                    case "cards": draw = (draw ?? 0) + delta; break;
                    case "energy": energy = (energy ?? 0) + delta; break;
                    case "cost": cost += delta; break;
                    case "repeat": hits += delta; break;
                    default:
                        int i = powers.FindIndex(p => MatchesPower(key, p.key));
                        if (i >= 0) powers[i] = (powers[i].kind, powers[i].key, powers[i].amount + delta);
                        break;
                }
            }
        }

        var keywords = new HashSet<string>(card.Keywords ?? new(), StringComparer.OrdinalIgnoreCase);
        CardKind kind = card.Type switch
        {
            "Attack" => CardKind.Attack,
            "Power" => CardKind.Power,
            "Status" => CardKind.Status,
            "Curse" => CardKind.Curse,
            _ => CardKind.Skill,
        };

        bool isX = card.IsXCost == true;
        if (isX) cost = CardDef.XCost;
        else if (keywords.Contains("Unplayable") || cost < 0)
            cost = CardDef.Unplayable;

        string target = card.Target ?? "Self";
        bool targetsEnemies = target is "AnyEnemy" or "AllEnemies" or "RandomEnemy";
        var effects = new List<Effect>();

        // Status cards like Burn and Infection: the number is what they cost you at the end of the turn, not an attack.
        int endTurnDamage = 0, endTurnHpLoss = 0;
        bool endOfTurnCard = card.DescriptionRaw != null && EndOfTurnInHand.IsMatch(card.DescriptionRaw);
        if (endOfTurnCard)
        {
            if (damage is > 0) { endTurnDamage = damage.Value; damage = null; }
            if (hpLoss is > 0) { endTurnHpLoss = hpLoss.Value; hpLoss = null; }
        }

        if (hpLoss is > 0) effects.Add(new Effect(EffectOp.LoseHp, hpLoss.Value));
        if (damage is > 0)
        {
            EffectOp op = target switch
            {
                "AllEnemies" => EffectOp.DamageAll,
                "RandomEnemy" => EffectOp.DamageRandom,
                _ => EffectOp.Damage,
            };
            effects.Add(isX
                ? new Effect(op, damage.Value, Hits: 0, HitsSource: Source.X)
                : new Effect(op, damage.Value, Math.Max(1, hits)));
        }
        if (block is > 0) effects.Add(new Effect(EffectOp.Block, block.Value));
        foreach ((PowerKind pk, string key, int amount) in powers)
        {
            if (pk == PowerKind.Unsupported) { approximate = true; continue; }
            if (PowerRules.IsDebuff(pk))
                effects.Add(new Effect(targetsEnemies ? (target == "AllEnemies" ? EffectOp.DebuffAll : EffectOp.DebuffEnemy) : EffectOp.DebuffSelf, amount, Power: pk));
            else
                effects.Add(new Effect(EffectOp.BuffSelf, amount, Power: pk));
        }
        if (energy is > 0) effects.Add(new Effect(EffectOp.Energy, energy.Value));
        if (draw is > 0) effects.Add(new Effect(EffectOp.Draw, draw.Value));

        if (card.DescriptionRaw != null && ComplexText.IsMatch(card.DescriptionRaw) && !endOfTurnCard) approximate = true;

        return new CardDef
        {
            EndTurnDamage = endTurnDamage,
            EndTurnHpLoss = endTurnHpLoss,
            Id = card.Id,
            Upgraded = upgraded,
            Kind = kind,
            Cost = cost,
            Exhaust = keywords.Contains("Exhaust"),
            Ethereal = keywords.Contains("Ethereal"),
            Innate = keywords.Contains("Innate"),
            Retain = keywords.Contains("Retain"),
            IsStrike = card.Tags?.Contains("Strike") == true,
            Effects = effects.ToArray(),
            Approximate = approximate,
        };
    }

    private static bool MatchesPower(string upgradeKey, string powerKey)
    {
        string p = powerKey.Replace("_", "").Replace(" ", "").ToLowerInvariant();
        return upgradeKey == p || upgradeKey == p + "power";
    }

    private static bool TryDelta(JsonElement value, out int delta)
    {
        delta = 0;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number: return value.TryGetInt32(out delta);
            case JsonValueKind.String: return int.TryParse(value.GetString(), out delta);
            default: return false;
        }
    }
}
