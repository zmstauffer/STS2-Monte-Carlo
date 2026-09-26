using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

public class ExplainerTests
{
    private static OptionReport Option(string label, bool baseline, double survive, double probe, double boss = 0, double elite = 0, double eliteHp = 20, double bossHp = 40,
        double deltaValue = 0, double se = 0.01) => new()
    {
        Label = label, IsBaseline = baseline, SurvivalRate = survive, ProbeHpLost = probe, BossDeathRate = boss, EliteDeathRate = elite,
        EliteHpLost = eliteHp, BossHpLost = bossHp, MeanHpEndIfSurvived = 30, DeltaValue = deltaValue, DeltaValueSe = se,
    };

    private static AdviceReport Report(params OptionReport[] options) => new()
    {
        Decision = "test", BaselineLabel = options.First(o => o.IsBaseline).Label, Options = options,
    };

    [Fact]
    public void ATradeOffIsNamedWhenTheBestOptionIsWorseOnSomethingItCares()
    {
        var report = Report(
            Option("SCALING_CARD", false, survive: 0.66, probe: 18, boss: 0.20, deltaValue: 0.15),
            Option("Skip", true, survive: 0.70, probe: 26, boss: 0.15));
        var lines = Explainer.Explain(report);
        Assert.Contains(lines, l => l.Contains("beats \"Skip\"") && l.Contains("8 HP less per next-act test fight (18 versus 26)"));
        Assert.Contains(lines, l => l.StartsWith("The trade-off") && l.Contains("survives the act slightly less often"));
    }

    [Fact]
    public void WhenTheBaselineWinsTheExplanationSaysSo()
    {
        var report = Report(
            Option("Skip", true, survive: 0.70, probe: 0.9),
            Option("BAD_CARD", false, survive: 0.60, probe: 0.85, deltaValue: -0.2));
        var lines = Explainer.Explain(report);
        Assert.Contains(lines, l => l.StartsWith("None of the other options beat \"Skip\""));
        Assert.Contains(lines, l => l.Contains("BAD_CARD"));
    }

    [Fact]
    public void ABestOptionWithinTheNoiseIsNotOversold()
    {
        var report = Report(
            Option("MAYBE", false, survive: 0.71, probe: 0.9, deltaValue: 0.01, se: 0.02),
            Option("Skip", true, survive: 0.70, probe: 0.9));
        Assert.Contains(Explainer.Explain(report), l => l.Contains("within the noise"));
    }

    [Fact]
    public void DeathRatesAndHpCostsAreDescribedInPlainWords()
    {
        var report = Report(
            Option("DEFENSIVE", false, survive: 0.85, probe: 0.95, boss: 0.05, elite: 0.04, eliteHp: 12, deltaValue: 0.3),
            Option("Skip", true, survive: 0.70, probe: 0.92, boss: 0.20, elite: 0.10, eliteHp: 20));
        string text = string.Join(" ", Explainer.Explain(report));
        Assert.Contains("deaths to boss fights fall from 20% to 5%", text);
        Assert.Contains("elite fights cost about 8 HP less", text);
    }

    [Fact]
    public void AnOptionIsAboutEqualToTheBestWhenTheGapIsNoiseOrUnderOnePoint()
    {
        Assert.True(new OptionReport { Label = "best", DeltaVsBest = 0 }.AboutEqualToBest);
        Assert.True(new OptionReport { Label = "noisy", DeltaVsBest = -0.03, DeltaVsBestSe = 0.02 }.AboutEqualToBest);
        Assert.True(new OptionReport { Label = "tiny", DeltaVsBest = -0.008, DeltaVsBestSe = 0.001 }.AboutEqualToBest);
        Assert.False(new OptionReport { Label = "worse", DeltaVsBest = -0.05, DeltaVsBestSe = 0.01 }.AboutEqualToBest);
    }
}
