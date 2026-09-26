using SpireMonteCarlo.Sim;
using Xunit;

namespace SpireMonteCarlo.Tests;

public class ExplainerTests
{
    private static OptionReport Option(string label, double survive, double future, double deltaVsBest = 0, double se = 0.002, double probe = 30, double boss = 0.05,
        double elite = 0.05, double later = 0, double hpEnd = 40) => new()
    {
        Label = label, SurvivalRate = survive, MeanFutureIfSurvived = future, DeltaVsBest = deltaVsBest, DeltaVsBestSe = se, ProbeHpLost = probe,
        BossDeathRate = boss, EliteDeathRate = elite, LongTermPoints = later, MeanHpEndIfSurvived = hpEnd, EliteHpLost = 20, BossHpLost = 40,
    };

    private static AdviceReport Report(params OptionReport[] options) => new() { Decision = "test", BaselineLabel = "Skip", Options = options };

    [Fact]
    public void TheGapToTheRunnerUpIsSplitIntoThisActTheRestOfTheRunAndLater()
    {
        var report = Report(Option("ANGER", 0.97, 0.40, later: -2), Option("Skip", 0.94, 0.38, deltaVsBest: -0.02, boss: 0.08));
        var lines = Explainer.Explain(report);
        Assert.StartsWith("ANGER beats Skip by 2.0 points:", lines[0]);
        Assert.Contains("from surviving this act (97% vs 94%)", lines[0]);
        Assert.Contains("from the deck and HP it leaves for the rest of the run", lines[0]);
        Assert.Contains("later in the run", lines[0]);
    }

    [Fact]
    public void TheMainDriverNamesTheKindOfFightTheDeathsMoveIn()
    {
        var report = Report(Option("DEFENSIVE", 0.90, 0.40, boss: 0.03), Option("Skip", 0.80, 0.40, deltaVsBest: -0.04, boss: 0.13));
        Assert.Contains(Explainer.Explain(report), l => l.StartsWith("Mostly fewer deaths to boss fights (3% vs 13% of futures)"));
    }

    [Fact]
    public void ADeckDrivenGapTalksAboutTheDeckTest()
    {
        var report = Report(Option("SCALING", 0.99, 0.45, probe: 25), Option("Skip", 0.99, 0.40, deltaVsBest: -0.05, probe: 29));
        Assert.Contains(Explainer.Explain(report), l => l.Contains("loses 4.0 HP less per test fight"));
    }

    [Fact]
    public void TiesAreCalledTooCloseToCall()
    {
        var report = Report(Option("A", 0.95, 0.40), Option("B", 0.95, 0.40, deltaVsBest: -0.001));
        Assert.StartsWith("A and B are too close to call", Explainer.Explain(report)[0]);
    }

    [Fact]
    public void AnOptionIsAboutEqualToTheBestWhenTheGapIsNoiseOrUnderHalfAPoint()
    {
        Assert.True(new OptionReport { Label = "best", DeltaVsBest = 0 }.AboutEqualToBest);
        Assert.True(new OptionReport { Label = "noisy", DeltaVsBest = -0.03, DeltaVsBestSe = 0.02 }.AboutEqualToBest);
        Assert.True(new OptionReport { Label = "tiny", DeltaVsBest = -0.004, DeltaVsBestSe = 0.001 }.AboutEqualToBest);
        Assert.False(new OptionReport { Label = "worse", DeltaVsBest = -0.05, DeltaVsBestSe = 0.01 }.AboutEqualToBest);
    }
}
