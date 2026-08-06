using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Tests.Contracts;

/// <summary>
/// The bar the creator watches while their dropped cover art is read and paired, before any song
/// exists. Same monotonicity property the song pipeline's bar has, and pinned the same way — this
/// phase spans the browser, the web server and the Function, so all three have to agree on what a
/// given percentage means.
/// </summary>
[TestFixture]
public class CoverArtMatchProgressCalculatorTests
{
    /// <summary>Every step in the order a batch actually passes through them.</summary>
    private static readonly CoverArtMatchStep[] Pipeline =
    [
        CoverArtMatchStep.Staging,
        CoverArtMatchStep.Queued,
        CoverArtMatchStep.Reading,
        CoverArtMatchStep.ReadingText,
        CoverArtMatchStep.Pairing,
        CoverArtMatchStep.Completed
    ];

    [Test]
    public void Pipeline_IsContinuous_SoTheBarNeverJumpsOrGapsBetweenSteps()
    {
        for (var i = 0; i < Pipeline.Length - 1; i++)
        {
            var current = Pipeline[i];
            var next = Pipeline[i + 1];

            Assert.That(
                CoverArtMatchProgressCalculator.BandEnd(current),
                Is.EqualTo(CoverArtMatchProgressCalculator.BandStart(next)).Within(0.0001),
                $"{current} ends where {next} does not begin, which would show as a jump or a stall.");
        }
    }

    [Test]
    public void Pipeline_StartsAtZeroAndEndsAtOneHundred()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CoverArtMatchProgressCalculator.BandStart(CoverArtMatchStep.Staging), Is.Zero);
            Assert.That(
                CoverArtMatchProgressCalculator.BandEnd(CoverArtMatchStep.Completed),
                Is.EqualTo(100d));
        });
    }

    [Test]
    public void Pipeline_IsMonotonic_AcrossEveryStepAndSubPercent()
    {
        var previous = -1d;

        foreach (var step in Pipeline)
        {
            foreach (var stepPercent in new[] { 0d, 25d, 50d, 75d, 100d })
            {
                var overall = CoverArtMatchProgressCalculator.ToOverallPercent(step, stepPercent);
                Assert.That(
                    overall,
                    Is.GreaterThanOrEqualTo(previous),
                    $"{step} at {stepPercent}% went backwards from {previous}.");
                previous = overall;
            }
        }
    }

    [Test]
    public void ReadingText_HasTheWidestBand()
    {
        // It is N model calls and the only part of this phase that scales with how many files the
        // creator dropped, so it is where the bar has to have room to actually move.
        var readingText = CoverArtMatchProgressCalculator.BandEnd(CoverArtMatchStep.ReadingText)
            - CoverArtMatchProgressCalculator.BandStart(CoverArtMatchStep.ReadingText);

        foreach (var step in Pipeline.Where(step => step != CoverArtMatchStep.ReadingText))
        {
            var width = CoverArtMatchProgressCalculator.BandEnd(step)
                - CoverArtMatchProgressCalculator.BandStart(step);
            Assert.That(readingText, Is.GreaterThan(width), $"{step} is at least as wide as ReadingText.");
        }
    }

    [Test]
    public void Queued_HoldsStill()
    {
        // Nothing is happening while the message sits on the queue, and a bar that creeps during a
        // wait is a lie about progress.
        Assert.That(
            CoverArtMatchProgressCalculator.BandStart(CoverArtMatchStep.Queued),
            Is.EqualTo(CoverArtMatchProgressCalculator.BandEnd(CoverArtMatchStep.Queued)));
    }

    [TestCase(CoverArtMatchStep.Completed, true)]
    [TestCase(CoverArtMatchStep.Failed, true)]
    [TestCase(CoverArtMatchStep.ReadingText, false)]
    [TestCase(CoverArtMatchStep.Staging, false)]
    public void IsTerminal_IdentifiesTheStepsABatchCanNeverMoveOnFrom(CoverArtMatchStep step, bool expected)
        => Assert.That(CoverArtMatchProgressCalculator.IsTerminal(step), Is.EqualTo(expected));

    [Test]
    public void Failed_OutranksEveryInFlightStep()
    {
        // Same reason AudioProcessingStep.Failed is highest: a late in-flight update must never
        // overwrite a recorded failure.
        foreach (var step in Pipeline)
        {
            Assert.That((int)step, Is.LessThan((int)CoverArtMatchStep.Failed));
        }
    }

    [TestCase(-50d)]
    [TestCase(150d)]
    public void ToOverallPercent_ClampsAnOutOfRangeSubPercent(double stepPercent)
    {
        var overall = CoverArtMatchProgressCalculator.ToOverallPercent(
            CoverArtMatchStep.ReadingText, stepPercent);

        Assert.That(overall, Is.InRange(
            CoverArtMatchProgressCalculator.BandStart(CoverArtMatchStep.ReadingText),
            CoverArtMatchProgressCalculator.BandEnd(CoverArtMatchStep.ReadingText)));
    }

    [Test]
    public void TheReceivingPhaseSubBands_AreContinuousAndCoverTheWholeScale()
    {
        // The upload page feeds one 0-100 into ToBatchPercent for the whole receiving phase, and
        // three separate stages now write into it. If these did not join up, the bar would jump or
        // stall at a handover the creator can plainly see.
        Assert.Multiple(() =>
        {
            Assert.That(CoverArtMatchProgressCalculator.ToReceivingPercent(0d), Is.Zero);
            Assert.That(
                CoverArtMatchProgressCalculator.ToReceivingPercent(100d),
                Is.EqualTo(CoverArtMatchProgressCalculator.ReceivingFilesEnd));

            Assert.That(
                CoverArtMatchProgressCalculator.ToStagingImagesPercent(0d),
                Is.EqualTo(CoverArtMatchProgressCalculator.ReceivingFilesEnd));
            Assert.That(
                CoverArtMatchProgressCalculator.ToStagingImagesPercent(100d),
                Is.EqualTo(CoverArtMatchProgressCalculator.StagingImagesEnd));

            Assert.That(
                CoverArtMatchProgressCalculator.ToMatchingPercent(0d),
                Is.EqualTo(CoverArtMatchProgressCalculator.StagingImagesEnd));
            Assert.That(CoverArtMatchProgressCalculator.ToMatchingPercent(100d), Is.EqualTo(100d));
        });
    }
}
