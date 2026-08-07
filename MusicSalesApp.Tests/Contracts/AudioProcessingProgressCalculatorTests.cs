using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Contracts;

/// <summary>
/// The creator watches one bar per song run from 0 to 100 across three processes - the upload page,
/// the Azure Function and the API. These tests pin the property that makes that believable: it only
/// ever moves forwards.
/// </summary>
[TestFixture]
public class AudioProcessingProgressCalculatorTests
{
    /// <summary>Every step in the order a song actually passes through them.</summary>
    private static readonly AudioProcessingStep[] Pipeline =
    [
        AudioProcessingStep.Staging,
        AudioProcessingStep.Queued,
        AudioProcessingStep.Downloading,
        AudioProcessingStep.Analyzing,
        AudioProcessingStep.Transcoding,
        AudioProcessingStep.Verifying,
        AudioProcessingStep.Uploading,
        AudioProcessingStep.RenderingArtwork,
        AudioProcessingStep.Copying,
        AudioProcessingStep.SavingMetadata,
        AudioProcessingStep.GeneratingArtwork,
        AudioProcessingStep.Completed
    ];

    [Test]
    public void Pipeline_IsContinuous_SoTheBarNeverJumpsOrGapsBetweenSteps()
    {
        for (var i = 0; i < Pipeline.Length - 1; i++)
        {
            var current = Pipeline[i];
            var next = Pipeline[i + 1];

            Assert.That(
                AudioProcessingProgressCalculator.BandEnd(current),
                Is.EqualTo(AudioProcessingProgressCalculator.BandStart(next)).Within(0.0001),
                $"{current} ends where {next} does not begin, which would show as a jump or a stall.");
        }
    }

    [Test]
    public void Pipeline_StartsAtZeroAndEndsAtOneHundred()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AudioProcessingProgressCalculator.BandStart(AudioProcessingStep.Staging), Is.Zero);
            Assert.That(
                AudioProcessingProgressCalculator.BandEnd(AudioProcessingStep.Completed),
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
                var overall = AudioProcessingProgressCalculator.ToOverallPercent(step, stepPercent);
                Assert.That(
                    overall,
                    Is.GreaterThanOrEqualTo(previous),
                    $"{step} at {stepPercent}% went backwards from {previous}.");
                previous = overall;
            }
        }
    }

    [Test]
    public void RenderingArtwork_HasABandWideEnoughForPerRungProgress()
    {
        // ProgressReporter drops any post that would not move the bar by a whole percent, so a band
        // narrower than one point per rung silently swallows every per-rung update and the step
        // becomes a single jump. Pinned because the property is invisible in the band table itself
        // and would be the first casualty of a future rebalance.
        var width = AudioProcessingProgressCalculator.BandEnd(AudioProcessingStep.RenderingArtwork)
            - AudioProcessingProgressCalculator.BandStart(AudioProcessingStep.RenderingArtwork);

        Assert.That(
            width,
            Is.GreaterThanOrEqualTo(ImageVariantSizes.CoverArt.Count),
            "RenderingArtwork's band is too narrow for one whole percent per rendition rung.");
    }

    [Test]
    public void ToOverallPercent_WithoutSubPercent_SitsAtTheBandStart()
    {
        Assert.That(
            AudioProcessingProgressCalculator.ToOverallPercent(AudioProcessingStep.Transcoding),
            Is.EqualTo(AudioProcessingProgressCalculator.BandStart(AudioProcessingStep.Transcoding)));
    }

    [Test]
    public void ToOverallPercent_MapsSubPercentIntoTheStepsOwnBand()
    {
        var start = AudioProcessingProgressCalculator.BandStart(AudioProcessingStep.Transcoding);
        var end = AudioProcessingProgressCalculator.BandEnd(AudioProcessingStep.Transcoding);

        var halfway = AudioProcessingProgressCalculator.ToOverallPercent(AudioProcessingStep.Transcoding, 50d);

        Assert.That(halfway, Is.EqualTo(start + ((end - start) / 2d)).Within(0.0001));
    }

    [TestCase(-50d)]
    [TestCase(150d)]
    public void ToOverallPercent_ClampsAnOutOfRangeSubPercent(double stepPercent)
    {
        // FFmpeg can report an out_time slightly past the probed duration, which would otherwise
        // push the bar beyond its band and into the next step's territory.
        var overall = AudioProcessingProgressCalculator.ToOverallPercent(
            AudioProcessingStep.Transcoding,
            stepPercent);

        Assert.That(overall, Is.InRange(
            AudioProcessingProgressCalculator.BandStart(AudioProcessingStep.Transcoding),
            AudioProcessingProgressCalculator.BandEnd(AudioProcessingStep.Transcoding)));
    }

    [Test]
    public void Queued_HoldsStill_BecauseNothingIsHappeningYet()
    {
        // A song waiting for a Function instance has no measurable progress. The band is a point,
        // so the bar parks rather than creeping and implying work that is not happening.
        Assert.That(
            AudioProcessingProgressCalculator.BandStart(AudioProcessingStep.Queued),
            Is.EqualTo(AudioProcessingProgressCalculator.BandEnd(AudioProcessingStep.Queued)));
    }

    [Test]
    public void IsAdvance_RejectsARepeatedStep()
    {
        // A queue retry replays the same step. Accepting it would restart that band's animation.
        Assert.That(
            AudioProcessingProgressCalculator.IsAdvance(
                AudioProcessingStep.Transcoding,
                AudioProcessingStep.Transcoding),
            Is.False);
    }

    [Test]
    public void IsAdvance_RejectsAnEarlierStep()
    {
        Assert.That(
            AudioProcessingProgressCalculator.IsAdvance(
                AudioProcessingStep.Uploading,
                AudioProcessingStep.Downloading),
            Is.False);
    }

    [Test]
    public void IsAdvance_AcceptsALaterStep()
    {
        Assert.That(
            AudioProcessingProgressCalculator.IsAdvance(
                AudioProcessingStep.Downloading,
                AudioProcessingStep.Uploading),
            Is.True);
    }

    [Test]
    public void Failed_OutranksEveryInFlightStep()
    {
        // A late progress ping must never resurrect a job that already failed, so Failed has to
        // sort above everything that can still be in flight.
        foreach (var step in Pipeline)
        {
            Assert.That(
                AudioProcessingProgressCalculator.IsAdvance(AudioProcessingStep.Failed, step),
                Is.False,
                $"{step} was treated as an advance over Failed.");
        }
    }

    [Test]
    public void IsTerminal_CoversBothEndStates()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AudioProcessingProgressCalculator.IsTerminal(AudioProcessingStep.Completed), Is.True);
            Assert.That(AudioProcessingProgressCalculator.IsTerminal(AudioProcessingStep.Failed), Is.True);
            Assert.That(AudioProcessingProgressCalculator.IsTerminal(AudioProcessingStep.Transcoding), Is.False);
            Assert.That(AudioProcessingProgressCalculator.IsTerminal(AudioProcessingStep.Queued), Is.False);
        });
    }

    // ---------------------------------------------------------------------
    // Batch bar: one figure for receiving every file, then processing every song.
    // ---------------------------------------------------------------------

    [Test]
    public void ToBatchPercent_BeforeAnySongExists_IsBoundedByTheReceivingShare()
    {
        // During receiving there are no songs yet, so the bar must not imply processing has begun.
        Assert.Multiple(() =>
        {
            Assert.That(AudioProcessingProgressCalculator.ToBatchPercent(0d, []), Is.Zero);
            Assert.That(
                AudioProcessingProgressCalculator.ToBatchPercent(100d, []),
                Is.EqualTo(AudioProcessingProgressCalculator.ReceivingShare));
        });
    }

    [Test]
    public void ToBatchPercent_WithReceivingDoneAndNoSongProgress_SitsExactlyAtTheHandover()
    {
        // The title-review pause lives here: files received, nothing processing yet.
        var percent = AudioProcessingProgressCalculator.ToBatchPercent(100d, [0d, 0d]);

        Assert.That(percent, Is.EqualTo(AudioProcessingProgressCalculator.ReceivingShare).Within(0.0001));
    }

    [Test]
    public void ToBatchPercent_WithEverythingFinished_IsExactlyOneHundred()
    {
        Assert.That(
            AudioProcessingProgressCalculator.ToBatchPercent(100d, [100d, 100d, 100d]),
            Is.EqualTo(100d).Within(0.0001));
    }

    [Test]
    public void ToBatchPercent_AveragesAcrossSongs_SoOneStragglerDoesNotHideTheRest()
    {
        // Nine done and one untouched should read as most of the way through, not as barely
        // started (min) or finished (max).
        var songs = Enumerable.Repeat(100d, 9).Append(0d).ToList();

        var percent = AudioProcessingProgressCalculator.ToBatchPercent(100d, songs);

        var expected = AudioProcessingProgressCalculator.ReceivingShare
            + (0.9d * AudioProcessingProgressCalculator.ProcessingShare);
        Assert.That(percent, Is.EqualTo(expected).Within(0.0001));
    }

    [Test]
    public void ToBatchPercent_NeverGoesBackwardsAsSongsAdvance()
    {
        var previous = -1d;

        foreach (var receiving in new[] { 0d, 50d, 100d })
        {
            foreach (var song in new[] { 0d, 25d, 50d, 75d, 100d })
            {
                var percent = AudioProcessingProgressCalculator.ToBatchPercent(
                    receiving,
                    receiving >= 100d ? [song] : []);

                Assert.That(percent, Is.GreaterThanOrEqualTo(previous));
                previous = percent;
            }
        }
    }

    [Test]
    public void ToBatchPercent_ClampsOutOfRangeInputs()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AudioProcessingProgressCalculator.ToBatchPercent(-20d, [50d]), Is.GreaterThanOrEqualTo(0d));
            Assert.That(AudioProcessingProgressCalculator.ToBatchPercent(150d, [150d]), Is.LessThanOrEqualTo(100d));
        });
    }

    [Test]
    public void ToBatchPercent_WithNullSongList_IsTreatedAsNoSongs()
    {
        Assert.That(
            AudioProcessingProgressCalculator.ToBatchPercent(100d, null),
            Is.EqualTo(AudioProcessingProgressCalculator.ReceivingShare));
    }

    [Test]
    public void ReceivingAndProcessingShares_SumToOneHundred()
    {
        Assert.That(
            AudioProcessingProgressCalculator.ReceivingShare + AudioProcessingProgressCalculator.ProcessingShare,
            Is.EqualTo(100d));
    }

    // ---------------------------------------------------------------------
    // Callback budget: two processes, deployed separately, that have to agree.
    // ---------------------------------------------------------------------

    [Test]
    public void TerminalCallback_OutlastsTheAssemblyItWaitsFor()
    {
        // The Function posts `complete` and holds the request open while the site copies blobs,
        // writes the song and builds the artwork. If it stops waiting first, its request is
        // abandoned mid-assembly and the queue redelivers on top of work still in flight - which is
        // exactly what a two-minute client timeout over a longer assembly produced.
        Assert.That(
            MediaProcessingTimeouts.TerminalCallback,
            Is.GreaterThan(MediaProcessingTimeouts.Assembly));
    }

    [Test]
    public void TerminalCallback_FitsInsideTheConsumptionExecutionCeiling()
    {
        // host.json pins functionTimeout to the Consumption maximum of 10 minutes, and that is a
        // hard kill. A callback allowed to wait longer than the whole invocation may run would be
        // killed mid-wait, which reads as a lost song rather than a failed callback.
        Assert.That(MediaProcessingTimeouts.TerminalCallback, Is.LessThan(TimeSpan.FromMinutes(10)));
    }

    [Test]
    public void ProgressCallback_IsFarShorterThanTheTerminalOne()
    {
        // Progress is decoration. An unresponsive site must cost a transcode seconds per step, not
        // the minutes the terminal callback is allowed.
        Assert.That(
            MediaProcessingTimeouts.ProgressCallback,
            Is.LessThan(MediaProcessingTimeouts.TerminalCallback));
    }
}
