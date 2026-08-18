using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Tests.Contracts;

/// <summary>
/// The band table two processes share: the Python Function app posts progress against it and the web
/// app records and rebroadcasts it. Nothing else would notice the two disagreeing about what "40%"
/// means.
/// </summary>
[TestFixture]
public class LyricsAlignmentProgressCalculatorTests
{
    /// <summary>Every step except the two terminal ones, in the order an attempt passes through them.</summary>
    private static readonly LyricsAlignmentStep[] Sequence =
    [
        LyricsAlignmentStep.Submitted,
        LyricsAlignmentStep.Queued,
        LyricsAlignmentStep.Preparing,
        LyricsAlignmentStep.SeparatingVocals,
        LyricsAlignmentStep.Aligning,
        LyricsAlignmentStep.Mapping,
        LyricsAlignmentStep.WritingOutputs,
        LyricsAlignmentStep.Copying,
        LyricsAlignmentStep.Saving,
        LyricsAlignmentStep.Completed
    ];

    [Test]
    public void TheBandsAreContiguous()
    {
        // A gap would make the bar jump; an overlap would let it go backwards between two steps that
        // are both advances.
        for (var i = 1; i < Sequence.Length; i++)
        {
            Assert.That(
                LyricsAlignmentProgressCalculator.BandStart(Sequence[i]),
                Is.EqualTo(LyricsAlignmentProgressCalculator.BandEnd(Sequence[i - 1])),
                $"{Sequence[i]} does not begin where {Sequence[i - 1]} ends.");
        }
    }

    [Test]
    public void TheSequenceRunsFromZeroToOneHundred()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LyricsAlignmentProgressCalculator.BandStart(LyricsAlignmentStep.Submitted), Is.Zero);
            Assert.That(
                LyricsAlignmentProgressCalculator.BandEnd(LyricsAlignmentStep.Completed),
                Is.EqualTo(100d));
        });
    }

    [Test]
    public void TheOverallPercentNeverGoesBackwardsAcrossTheSequence()
    {
        var previous = -1d;

        foreach (var step in Sequence)
        {
            var percent = LyricsAlignmentProgressCalculator.ToOverallPercent(step);
            Assert.That(percent, Is.GreaterThanOrEqualTo(previous), $"{step} moved the bar backwards.");
            previous = percent;
        }
    }

    [Test]
    public void SeparationGetsTheWidestBand()
    {
        // Not cosmetic. Demucs has no natural progress signal, so the activity heartbeats on a timer;
        // a band narrower than the others would round every heartbeat to the same whole percent and
        // the bar would sit still for tens of minutes on the longest stage of the run.
        var separation = LyricsAlignmentProgressCalculator.BandEnd(LyricsAlignmentStep.SeparatingVocals)
            - LyricsAlignmentProgressCalculator.BandStart(LyricsAlignmentStep.SeparatingVocals);

        foreach (var step in Sequence.Where(s => s != LyricsAlignmentStep.SeparatingVocals))
        {
            var width = LyricsAlignmentProgressCalculator.BandEnd(step)
                - LyricsAlignmentProgressCalculator.BandStart(step);

            Assert.That(separation, Is.GreaterThan(width), $"{step} is at least as wide as separation.");
        }
    }

    [Test]
    public void StepPercentMovesThroughTheBandRatherThanPastIt()
    {
        var start = LyricsAlignmentProgressCalculator.BandStart(LyricsAlignmentStep.SeparatingVocals);
        var end = LyricsAlignmentProgressCalculator.BandEnd(LyricsAlignmentStep.SeparatingVocals);

        Assert.Multiple(() =>
        {
            Assert.That(
                LyricsAlignmentProgressCalculator.ToOverallPercent(LyricsAlignmentStep.SeparatingVocals, 0d),
                Is.EqualTo(start));
            Assert.That(
                LyricsAlignmentProgressCalculator.ToOverallPercent(LyricsAlignmentStep.SeparatingVocals, 100d),
                Is.EqualTo(end));
            Assert.That(
                LyricsAlignmentProgressCalculator.ToOverallPercent(LyricsAlignmentStep.SeparatingVocals, 50d),
                Is.EqualTo(start + ((end - start) / 2d)));
        });
    }

    [Test]
    public void AnOutOfRangeStepPercentIsClampedRatherThanEscapingTheBand()
    {
        // The heartbeat computes its own fraction from elapsed time against an estimate, so it can
        // and does overshoot on a slow instance.
        Assert.Multiple(() =>
        {
            Assert.That(
                LyricsAlignmentProgressCalculator.ToOverallPercent(LyricsAlignmentStep.SeparatingVocals, 250d),
                Is.EqualTo(LyricsAlignmentProgressCalculator.BandEnd(LyricsAlignmentStep.SeparatingVocals)));
            Assert.That(
                LyricsAlignmentProgressCalculator.ToOverallPercent(LyricsAlignmentStep.SeparatingVocals, -40d),
                Is.EqualTo(LyricsAlignmentProgressCalculator.BandStart(LyricsAlignmentStep.SeparatingVocals)));
        });
    }

    [Test]
    public void FailedOutranksEveryOtherStep()
    {
        // So a late in-flight ping can never overwrite a recorded failure. The same reason
        // AudioProcessingStep.Failed is the highest value there.
        foreach (var step in Sequence)
        {
            Assert.That(
                LyricsAlignmentProgressCalculator.IsAdvance(LyricsAlignmentStep.Failed, step),
                Is.False,
                $"{step} was treated as an advance on Failed.");
        }
    }

    [Test]
    public void OnlyCompletedAndFailedAreTerminal()
    {
        foreach (var step in Sequence.Where(s => s != LyricsAlignmentStep.Completed))
        {
            Assert.That(LyricsAlignmentProgressCalculator.IsTerminal(step), Is.False, $"{step} is not terminal.");
        }

        Assert.Multiple(() =>
        {
            Assert.That(LyricsAlignmentProgressCalculator.IsTerminal(LyricsAlignmentStep.Completed), Is.True);
            Assert.That(LyricsAlignmentProgressCalculator.IsTerminal(LyricsAlignmentStep.Failed), Is.True);
        });
    }
}
