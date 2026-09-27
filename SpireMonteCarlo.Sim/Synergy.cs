namespace SpireMonteCarlo.Sim;

/// <summary>A deck theme a card can feed (an enabler) or be rewarded by (a payoff).</summary>
public enum Theme { Exhaust, SelfDamage, Vulnerable, Block, Strength, Strike }

/// <summary>
/// Build-around picks: how well a card fits the deck it would join. Each card's enabler and payoff themes are read from its rules
/// (exhausting cards, losing HP, applying Vulnerable, gaining block or Strength, being a Strike; and the powers or scaling numbers
/// that reward those). A card scores for each payoff in the deck it feeds and for each enabler in the deck it pays off, so after
/// Dark Embrace the simulated player leans toward exhaust cards, and a deck full of them leans toward Feel No Pain, the way real
/// players build around what they have. Picks everywhere else still follow Codex Elo; this adds to it.
/// </summary>
public static class Synergy
{
    /// <summary>Elo added per unit of fit. A card that pays off a deck with three enablers (or feeds a deck with one payoff) fits by 1.</summary>
    public static double EloPerFit = double.Parse(Environment.GetEnvironmentVariable("SYNERGY_ELO") ?? "120", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The most Elo fit can add to one card.</summary>
    public const double MaxBonus = 300;

    private static readonly Theme[] Themes = Enum.GetValues<Theme>();

    /// <summary>How much the card feeds each theme (0 when it doesn't).</summary>
    public static double Enables(CardDef c, Theme t)
    {
        double v = 0;
        foreach (Effect e in c.Effects)
        {
            v += t switch
            {
                Theme.Exhaust => e.Op switch
                {
                    EffectOp.ExhaustRandomFromHand or EffectOp.ExhaustChosenFromHand => 1,
                    EffectOp.PlayTopOfDraw when e.Hits == 1 => 1,   // Havoc exhausts what it plays; Cascade and Catastrophe don't

                    EffectOp.ExhaustHand or EffectOp.ExhaustNonAttacksForBlock => 1.5,
                    EffectOp.BuffSelf when e.Power == PowerKind.Corruption => 2,
                    _ => 0,
                },
                Theme.SelfDamage => e.Op == EffectOp.LoseHp || (e.Op == EffectOp.BuffSelf && e.Power is PowerKind.InfernoSelfDamage or PowerKind.CrimsonSelfDamage) ? 1 : 0,
                Theme.Vulnerable => e.Op is EffectOp.DebuffEnemy or EffectOp.DebuffAll && e.Power == PowerKind.Vulnerable ? 1 : 0,
                Theme.Block => (e.Op is EffectOp.Block or EffectOp.BlockFlat && e.Amount >= 5) || (e.Op == EffectOp.BuffSelf && e.Power is PowerKind.Plating or PowerKind.Metallicize) ? 1 : 0,
                Theme.Strength => e.Op == EffectOp.BuffSelf && e.Power is PowerKind.Strength or PowerKind.Ritual or PowerKind.DemonForm or PowerKind.Rupture ? 1 : 0,
                _ => 0,
            };
        }
        if (t == Theme.Exhaust && c.Exhaust && c.Kind != CardKind.Status) v += 0.5;   // exhausts itself
        // A Strike adds only a little to each Strike payoff (+2 to a Perfected Strike), and every Strike card is one, so half a unit.
        if (t == Theme.Strike && c.IsStrike) v += 0.5;
        return Math.Min(v, 2);
    }

    /// <summary>How much the card is rewarded by each theme (0 when it isn't).</summary>
    public static double PaysOff(CardDef c, Theme t)
    {
        double v = 0;
        foreach (Effect e in c.Effects)
        {
            Source[] sources = { e.AmountSource, e.HitsSource };
            v += t switch
            {
                Theme.Exhaust => (e.Op == EffectOp.BuffSelf && e.Power is PowerKind.FeelNoPain or PowerKind.DarkEmbrace ? 1 : 0)
                    + (sources.Any(s => s is Source.ExhaustPileCount or Source.ExhaustedThisTurn or Source.ExhaustAtLeast or Source.HandExhausted) ? 1 : 0),
                Theme.SelfDamage => (e.Op == EffectOp.BuffSelf && e.Power is PowerKind.Rupture ? 1 : 0)
                    + (sources.Any(s => s is Source.LostHpThisTurn or Source.TimesHurtPlusOne) ? 1 : 0),
                Theme.Vulnerable => (e.Op == EffectOp.BuffSelf && e.Power is PowerKind.Vicious or PowerKind.Colossus or PowerKind.Cruelty ? 1 : 0)
                    + (e.Op == EffectOp.StrengthFromVulnerable || sources.Any(s => s is Source.TargetVulnerable or Source.TargetIsVulnerable) ? 1 : 0),
                Theme.Block => (e.Op == EffectOp.BuffSelf && e.Power is PowerKind.Juggernaut or PowerKind.Barricade ? 1 : 0)
                    + (e.Op == EffectOp.DoubleBlock || sources.Any(s => s == Source.Block) ? 1 : 0),
                // Strength adds to every hit: multi-hit attacks pay it off most, but any attack does a little (a Strength card in a deck of
                // Stomp, Perfected Strike and Setup Strike used to count as fitting nothing).
                Theme.Strength => (e.Op is EffectOp.Damage or EffectOp.DamageAll or EffectOp.DamageRandom ? (e.Hits >= 2 || e.HitsSource == Source.X ? 0.5 : 0.25) : 0)
                    + (sources.Any(s => s == Source.Strength) ? 1 : 0),
                Theme.Strike => (sources.Any(s => s == Source.StrikeCards) ? 1 : 0) + (e.Op == EffectOp.BuffSelf && e.Power == PowerKind.Hellraiser ? 1 : 0),
                _ => 0,
            };
        }
        if (t == Theme.Exhaust && c.EnergyWhenExhausted > 0) v += 1;
        return Math.Min(v, 2);
    }

    /// <summary>How well <paramref name="card"/> fits <paramref name="deck"/> (0 when it neither feeds nor is fed by anything in it).</summary>
    public static double Fit(CardDef card, IReadOnlyList<CardDef> deck)
    {
        double fit = 0;
        foreach (Theme t in Themes)
        {
            double enables = Enables(card, t), paysOff = PaysOff(card, t);
            if (enables == 0 && paysOff == 0) continue;
            double deckEnablers = 0, deckPayoffs = 0;
            foreach (CardDef c in deck)
            {
                // Starter cards are in everyone's deck, so Codex Elo already prices a card's fit with them (five Strikes would
                // otherwise make Perfected Strike a favourite in every deck).
                if (c.Rarity == "Basic") continue;
                deckEnablers += Enables(c, t);
                deckPayoffs += PaysOff(c, t);
            }
            // A payoff wants several enablers (three make a full unit, more help less); an enabler wants any payoff at all.
            fit += paysOff * Math.Min(deckEnablers, 6) / 3 + enables * Math.Min(deckPayoffs, 2);
        }
        return fit;
    }

    /// <summary>
    /// How much of a card's Codex rating above an average card applies in this deck (1 for most cards). A card that only pays off a
    /// theme (Feel No Pain) is rated by players who mostly took it into decks that feed it, so in a deck with little of that theme only
    /// the share it fits counts (a playtest, floor 23: Feel No Pain in a deck whose only exhaust card was Second Wind).
    /// </summary>
    public static double RatingShare(CardDef card, IReadOnlyList<CardDef> deck)
    {
        bool enablesAny = Themes.Any(t => Enables(card, t) > 0), paysAny = Themes.Any(t => PaysOff(card, t) > 0);
        return enablesAny || !paysAny ? 1 : Math.Min(1, Fit(card, deck));
    }

    /// <summary>The Elo a card gains from fitting the deck.</summary>
    public static double Bonus(CardDef card, IReadOnlyList<CardDef> deck) => Math.Min(MaxBonus, EloPerFit * Fit(card, deck));
}
