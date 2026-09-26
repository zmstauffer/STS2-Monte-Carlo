namespace SpireMonteCarlo.Sim;

/// <summary>
/// Corrections to monsters whose moves the extractor reads only partly (the game's own move method has logic around its effects).
/// Each one is written from the decompiled class; the mechanics that go with them live in MonsterBehaviorsImpl.cs.
/// </summary>
public static class MonsterOverrides
{
    public static void Apply(MonsterDef def)
    {
        switch (def.Id)
        {
            case "THE_INSATIABLE":
                // Liquify Ground: a sandpit (4) on the player, and six Frantic Escapes shuffled in: three into the draw pile, three into the discard.
                Replace(def, "LIQUIFY_GROUND", m => m with
                {
                    Powers = new[] { new MovePower(PowerKind.Sandpit, true, 4) },
                    Adds = new[] { new CardAdd("FRANTIC_ESCAPE", AddPile.Draw, 3), new CardAdd("FRANTIC_ESCAPE", AddPile.Discard, 3) },
                });
                break;
            case "ENTOMANCER":
                // Pheromone Spit's effect depends on the Personal Hive it has (see EntomancerBehavior); the extractor saw all its branches at once.
                Replace(def, "PHEROMONE_SPIT", m => m with { Powers = Array.Empty<MovePower>() });
                break;
            case "KNOWLEDGE_DEMON":
                // Ponder heals 30 (the extractor could not resolve it); the curse move's effect is in KnowledgeDemonBehavior.
                break;
        }
    }

    private static void Replace(MonsterDef def, string moveId, Func<MoveDef, MoveDef> change)
    {
        if (def.Moves.TryGetValue(moveId, out MoveDef? move)) def.Moves[moveId] = change(move);
    }
}
