namespace SpireMonteCarlo.Sim;

/// <summary>Hand-written corrections for cards whose behavior the automatic Codex-derived effects can't express.</summary>
internal static class Overrides
{
    public static CardDef Apply(CardDef card)
    {
        switch (card.Id)
        {
            case "BODY_SLAM":
                return With(card, new[] { new Effect(EffectOp.DamageEqualBlock, 0) }, approximate: false);
            default:
                return card;
        }
    }

    private static CardDef With(CardDef card, Effect[] effects, bool approximate) => new()
    {
        Id = card.Id,
        Upgraded = card.Upgraded,
        Kind = card.Kind,
        Cost = card.Cost,
        Exhaust = card.Exhaust,
        Ethereal = card.Ethereal,
        Innate = card.Innate,
        Retain = card.Retain,
        Effects = effects,
        EndTurnDamage = card.EndTurnDamage,
        EndTurnHpLoss = card.EndTurnHpLoss,
        Approximate = approximate,
    };
}
