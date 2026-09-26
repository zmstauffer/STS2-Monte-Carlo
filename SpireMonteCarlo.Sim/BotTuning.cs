using System.Globalization;

namespace SpireMonteCarlo.Sim;

/// <summary>The bot's valuation weights in one place, so <c>advisor sim tune</c> can search for better ones. The defaults are what the bot ships with.</summary>
public sealed class BotTuning
{
    /// <summary>Value of one point of damage that reaches an enemy's HP (the unit everything else is measured in).</summary>
    public double Damage = 1.0;
    /// <summary>Value of one point of block that stops incoming damage.</summary>
    public double Block = 1.1;
    /// <summary>Value of block beyond what the attacks would deal.</summary>
    public double ExcessBlock = 0.1;
    /// <summary>Bonus for killing an enemy, times (4 + its intended damage).</summary>
    public double Kill = 1.0;
    /// <summary>Value of the enemy damage a partial hit removes, as a share of its intended damage.</summary>
    public double Threat = 0.5;
    public double Draw = 3.0;
    public double Energy = 3.5;
    public double Strength = 8;
    public double Dexterity = 6;
    public double Vulnerable = 0.5;
    public double Weak = 0.25;
    /// <summary>Multiplier on the value of every other power card.</summary>
    public double Powers = 1.0;
    /// <summary>1 to judge a turn by playing out the enemy phase (HP actually lost plus the enemies' HP times their price in future HP) instead of summing per-card values.</summary>
    public double Leaf = 1;
    /// <summary>Leaf mode: damage per turn the deck is expected to deal, which sets how much HP an enemy's remaining life costs us.</summary>
    public double Dpt = 22.5;
    /// <summary>Leaf mode: incoming damage per turn we expect to block away in later turns anyway.</summary>
    public double SpareBlock = 3;
    /// <summary>Leaf mode: how many enemy turns ahead the incoming damage is averaged over.</summary>
    public double Horizon = 3;
    /// <summary>Leaf mode: the least threat an enemy counts for, as a share of the damage its average attack move deals.</summary>
    public double BaseThreat = 0.6;
    /// <summary>Leaf mode: value of each point of enemy HP removed even when nothing it does hurts us, so plans still push the fight along.</summary>
    public double Progress = 0.2;
    /// <summary>Leaf mode: weight on the value of powers gained this turn.</summary>
    public double PowerGain = 1.0;

    public static BotTuning Default { get; } = FromEnvironment();

    private static BotTuning FromEnvironment()
    {
        var t = new BotTuning();
        string? spec = Environment.GetEnvironmentVariable("BOT_TUNE");
        if (!string.IsNullOrWhiteSpace(spec)) t.Apply(spec);
        return t;
    }

    public static IReadOnlyList<string> Names { get; } = typeof(BotTuning).GetFields().Where(f => f.FieldType == typeof(double)).Select(f => f.Name).ToList();

    public double Get(string name) => (double)typeof(BotTuning).GetField(name)!.GetValue(this)!;

    public void Set(string name, double value) => typeof(BotTuning).GetField(name)!.SetValue(this, value);

    /// <summary>Applies "Name=value,Name=value".</summary>
    public void Apply(string spec)
    {
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] kv = part.Split('=');
            Set(kv[0], double.Parse(kv[1], CultureInfo.InvariantCulture));
        }
    }

    public BotTuning Clone() => (BotTuning)MemberwiseClone();

    public override string ToString() => string.Join(",", Names.Select(n => $"{n}={Get(n).ToString("0.###", CultureInfo.InvariantCulture)}"));
}
