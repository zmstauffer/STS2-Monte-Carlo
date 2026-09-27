using SpireMonteCarlo.Codex;

namespace SpireMonteCarlo.Sim;

/// <summary>
/// Real players' results with each relic, as a stand-in for the Elo relics don't have: for each act, the win rate of the A10 runs
/// that held the relic in that act (Codex /runs/scores/relics?act=N) minus the win rate of the average relic held in that act, in
/// run-win percentage points. Comparing within one act removes most of the survivor bias of the plain win rate (a relic found in Act 3
/// only shows up in runs that already survived two acts).
/// <para>
/// The data mixes characters, so a class relic is compared with its class's starter relic (every run of that class holds it; Burning
/// Blood alone would otherwise read 7 points below average, which is Ironclad's win rate, not the relic). Small samples are shrunk
/// toward 0 (<see cref="ShrinkPicks"/>).
/// </para>
/// </summary>
public sealed class RelicRatings
{
    /// <summary>Runs of weight that the prior of "an average relic" carries: a relic seen in 3000 runs keeps half its measured lift.</summary>
    public const double ShrinkPicks = 3000;

    private readonly Dictionary<int, Dictionary<string, double>> _lift = new();

    public RelicRatings(CodexCache cache, IReadOnlyList<CodexRelic> relics)
    {
        var poolOf = relics.ToDictionary(r => r.Id, r => r.Pool.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
        var starterOf = relics.Where(r => r.Rarity.StartsWith("Starter", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.Pool.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Id);
        foreach (int act in CodexCache.RelicActs)
        {
            Dictionary<string, RelicActScore>? scores = cache.LoadRelicAct(act);
            if (scores == null || scores.Count == 0) continue;
            long picks = scores.Values.Sum(s => (long)s.Picks);
            if (picks == 0) continue;
            double average = 100.0 * scores.Values.Sum(s => (long)s.Wins) / picks;
            var lift = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, s) in scores)
            {
                if (s.Picks == 0) continue;
                double baseline = average;
                if (poolOf.TryGetValue(id, out string? pool) && pool != "shared" && starterOf.TryGetValue(pool, out string? starter)
                    && starter != id && scores.TryGetValue(starter, out RelicActScore? st) && st.Picks > 0)
                    baseline = st.WinRate;
                lift[id] = (s.WinRate - baseline) * s.Picks / (s.Picks + ShrinkPicks);
            }
            _lift[act] = lift;
        }
    }

    /// <summary>True when the per-act scores are in the cache (<c>advisor codex update --relic-acts-only</c>).</summary>
    public bool Available => _lift.Count > 0;

    /// <summary>Run-win points above the average relic for runs holding this relic in this act (0 when unknown).</summary>
    public double Lift(string relicId, int act) =>
        _lift.TryGetValue(Math.Clamp(act, 1, 3), out var byId) && byId.TryGetValue(relicId, out double v) ? v : 0;
}
