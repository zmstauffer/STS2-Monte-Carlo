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

    private readonly IReadOnlyDictionary<string, CodexCard> _codex;
    private readonly Dictionary<(string, bool), CardDef> _cache = new();
    private readonly HashSet<string> _unknown = new();

    public CardLibrary(IReadOnlyDictionary<string, CodexCard> codexCards) => _codex = codexCards;

    /// <summary>Card ids that were requested but Codex doesn't know (the game is newer than the export).</summary>
    public IReadOnlyCollection<string> UnknownIds => _unknown;

    public CardDef Get(string id, bool upgraded)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue((id, upgraded), out CardDef? cached)) return cached;
            CardDef def = _codex.TryGetValue(id, out CodexCard? card) ? Build(card, upgraded) : Unknown(id, upgraded);
            _cache[(id, upgraded)] = def;
            return def;
        }
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

        if (hpLoss is > 0) effects.Add(new Effect(EffectOp.LoseHp, hpLoss.Value));
        if (damage is > 0)
        {
            int h = isX ? -1 : Math.Max(1, hits);
            effects.Add(new Effect(target switch
            {
                "AllEnemies" => EffectOp.DamageAll,
                "RandomEnemy" => EffectOp.DamageRandom,
                _ => EffectOp.Damage,
            }, damage.Value, h));
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

        if (card.DescriptionRaw != null && ComplexText.IsMatch(card.DescriptionRaw)) approximate = true;

        return Overrides.Apply(new CardDef
        {
            Id = card.Id,
            Upgraded = upgraded,
            Kind = kind,
            Cost = cost,
            Exhaust = keywords.Contains("Exhaust"),
            Ethereal = keywords.Contains("Ethereal"),
            Innate = keywords.Contains("Innate"),
            Retain = keywords.Contains("Retain"),
            Effects = effects.ToArray(),
            Approximate = approximate,
        });
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
