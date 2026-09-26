namespace SpireMonteCarlo.Sim;

/// <summary>
/// Small deterministic RNG (SplitMix64). Every rollout owns its own instance, so simulations share no state
/// and can run on all cores; seeding two options with the same seed gives them the same luck (common random numbers).
/// </summary>
public sealed class SimRng
{
    private ulong _state;

    public SimRng(ulong seed) => _state = seed;

    public ulong NextU64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, maxExclusive).</summary>
    public int Next(int maxExclusive) => maxExclusive <= 1 ? 0 : (int)(((NextU64() >> 32) * (ulong)maxExclusive) >> 32);

    /// <summary>Uniform in [minInclusive, maxInclusive].</summary>
    public int NextInclusive(int minInclusive, int maxInclusive) => minInclusive + Next(maxInclusive - minInclusive + 1);

    public double NextDouble() => (NextU64() >> 11) * (1.0 / (1UL << 53));

    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>Derives an independent stream seed, e.g. per (rollout, fight).</summary>
    public static ulong Mix(ulong seed, ulong stream)
    {
        var r = new SimRng(seed ^ (stream * 0xD6E8FEB86659FD93UL));
        r.NextU64();
        return r.NextU64();
    }
}
