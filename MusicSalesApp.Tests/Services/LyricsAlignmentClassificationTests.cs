using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The decision that stands between an alignment run and what a listener hears.
///
/// <para>
/// It lives server-side rather than in the Function on purpose - the Function reports measurements,
/// this judges them - and the payoff is exactly this file: the whole judgement can be exercised with
/// no Python runtime, no Function app, no storage account and no database.
/// </para>
///
/// <para>
/// <b>Two gates, and conflating them is the mistake this guards against.</b> Confidence is a quality
/// gate: below the threshold the timings are held back but kept, because they may still be worth a
/// look and are the starting point for any correction. The structural checks are a different kind of
/// thing - timings that run backwards, or end after the track does, are broken rather than
/// imprecise, and no amount of reviewing turns them into something a player could use.
/// </para>
/// </summary>
[TestFixture]
public class LyricsAlignmentClassificationTests
{
    private const double Threshold = 0.7d;

    [Test]
    public void AConfidentWellFormedResultIsPublished()
    {
        var result = GoodResult(confidence: 0.91d);

        var classification = LyricsAlignmentCompletionService.Classify(result, Threshold);

        Assert.Multiple(() =>
        {
            Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.Published));
            Assert.That(classification.FailureCode, Is.Null);
        });
    }

    [Test]
    public void ALowConfidenceResultIsKeptForReviewRatherThanDiscarded()
    {
        // The creator may still want to inspect or export these, and they are the starting point for
        // any correction pass. Throwing away minutes of compute because a number was short of a
        // threshold would be the wrong trade.
        var result = GoodResult(confidence: 0.42d);

        var classification = LyricsAlignmentCompletionService.Classify(result, Threshold);

        Assert.Multiple(() =>
        {
            Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.NeedsReview));
            Assert.That(classification.FailureCode, Is.Null, "Needing review is not a failure.");
        });
    }

    [Test]
    public void ConfidenceExactlyAtTheThresholdPublishes()
    {
        // The threshold is admin-tunable, so somebody will eventually set it to the exact value a
        // song scored. "At least this confident" is the intended reading.
        var classification = LyricsAlignmentCompletionService.Classify(
            GoodResult(confidence: Threshold), Threshold);

        Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.Published));
    }

    [Test]
    public void AMissingConfidenceIsTreatedAsNoConfidence()
    {
        // A Function that reported no score must not be read as having reported a perfect one.
        var result = GoodResult(confidence: null);

        var classification = LyricsAlignmentCompletionService.Classify(result, Threshold);

        Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.NeedsReview));
    }

    [Test]
    public void NothingMatchingIsAFailureEvenAtHighConfidence()
    {
        // Almost always the wrong lyrics pasted against the wrong song, which is worth saying rather
        // than burying under a percentage.
        var result = GoodResult(confidence: 0.99d);
        result.MatchedTokenCount = 0;

        var classification = LyricsAlignmentCompletionService.Classify(result, Threshold);

        Assert.Multiple(() =>
        {
            Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.Failed));
            Assert.That(
                classification.FailureCode,
                Is.EqualTo(LyricsAlignmentFailureCodes.NoTokensMatched));
        });
    }

    [Test]
    public void NonMonotonicTimingsFailEvenAtHighConfidence()
    {
        // A word that starts before the one before it is structurally broken. A player binary
        // searching this array would land anywhere.
        var result = GoodResult(confidence: 0.95d);
        result.IsMonotonic = false;

        var classification = LyricsAlignmentCompletionService.Classify(result, Threshold);

        Assert.Multiple(() =>
        {
            Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.Failed));
            Assert.That(
                classification.FailureCode,
                Is.EqualTo(LyricsAlignmentFailureCodes.TimingsNotMonotonic));
        });
    }

    [Test]
    public void TimingsRunningWellPastTheTrackFail()
    {
        var result = GoodResult(confidence: 0.95d);
        result.DurationMs = 200_000;
        result.LastWordEndMs = 260_000;

        var classification = LyricsAlignmentCompletionService.Classify(result, Threshold);

        Assert.Multiple(() =>
        {
            Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.Failed));
            Assert.That(
                classification.FailureCode,
                Is.EqualTo(LyricsAlignmentFailureCodes.TimingsExceedDuration));
        });
    }

    [Test]
    public void ASmallOvershootPastTheTrackEndIsTolerated()
    {
        // A final syllable can genuinely ring past the last decoded sample, and ffprobe's duration
        // is itself approximate. Failing on a few hundred milliseconds would reject good alignments.
        var result = GoodResult(confidence: 0.95d);
        result.DurationMs = 200_000;
        result.LastWordEndMs = 202_000;

        var classification = LyricsAlignmentCompletionService.Classify(result, Threshold);

        Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.Published));
    }

    [Test]
    public void MostLinesGoingUnplacedIsAFailureRatherThanLowConfidence()
    {
        // A failed alignment wearing a plausible score: the words it did place may well be placed
        // confidently, which is exactly why the confidence gate alone would let this through.
        var result = GoodResult(confidence: 0.88d);
        result.LineCount = 40;
        result.LinesWithTimingCount = 9;

        var classification = LyricsAlignmentCompletionService.Classify(result, Threshold);

        Assert.Multiple(() =>
        {
            Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.Failed));
            Assert.That(
                classification.FailureCode,
                Is.EqualTo(LyricsAlignmentFailureCodes.AlignmentFailed));
        });
    }

    [Test]
    public void AStructuralFailureBeatsALowConfidenceScore()
    {
        // Both gates trip. The result must be Failed rather than NeedsReview - there is nothing to
        // review, and offering it as reviewable would waste the creator's time.
        var result = GoodResult(confidence: 0.10d);
        result.IsMonotonic = false;

        var classification = LyricsAlignmentCompletionService.Classify(result, Threshold);

        Assert.Multiple(() =>
        {
            Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.Failed));
            Assert.That(classification.FailureCode, Is.Not.Null);
        });
    }

    [Test]
    public void AZeroDurationDoesNotFailAnOtherwiseGoodResult()
    {
        // ffprobe occasionally reports nothing for a stream it can still decode. The overshoot check
        // has to opt out rather than divide by a duration it does not have.
        var result = GoodResult(confidence: 0.95d);
        result.DurationMs = 0;
        result.LastWordEndMs = 180_000;

        var classification = LyricsAlignmentCompletionService.Classify(result, Threshold);

        Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.Published));
    }

    private static LyricsAlignmentResult GoodResult(double? confidence) => new()
    {
        JobId = Guid.NewGuid(),
        Outcome = LyricsAlignmentOutcome.Aligned,
        Confidence = confidence,
        LyricTokenCount = 300,
        MatchedTokenCount = 280,
        InterpolatedTokenCount = 20,
        LineCount = 40,
        LinesWithTimingCount = 40,
        IsMonotonic = true,
        DurationMs = 214_000,
        LastWordEndMs = 210_000
    };
}
