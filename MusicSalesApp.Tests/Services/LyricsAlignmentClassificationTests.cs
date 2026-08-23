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
/// <b>The structural checks are the only gate left.</b> Timings that run backwards, or end after the
/// track does, are broken rather than imprecise, and no amount of reviewing turns them into something
/// a player could use. Confidence used to sit alongside them as a quality gate; it no longer decides
/// anything, because it turned out to answer "did the aligner think it did well" rather than "are
/// these timings good", and the two disagree badly on real songs.
/// </para>
/// </summary>
[TestFixture]
public class LyricsAlignmentClassificationTests
{

    [Test]
    public void EvenAVeryConfidentResultWaitsForTheCreator()
    {
        // Alignment cannot publish, at any score. Machine alignment of sung vocals lands 150-300 ms
        // out on a good day and a listener notices at once, so the last word belongs to the person
        // whose song it is.
        var result = GoodResult(confidence: 0.91d);

        var classification = LyricsAlignmentCompletionService.Classify(result);

        Assert.Multiple(() =>
        {
            Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.NeedsReview));
            Assert.That(classification.FailureCode, Is.Null, "Waiting for review is not a failure.");
        });
    }

    [Test]
    public void NothingIsEverPublishedByTheAlignmentPipeline()
    {
        // The whole safety story of this change, asserted across the range rather than at a point:
        // there must be no confidence at all - including a perfect 1.0 - that puts timings in front
        // of a listener without the creator having heard them.
        foreach (var confidence in new[] { 0d, 0.25d, 0.5d, 0.7d, 0.99d, 1.0d })
        {
            var classification = LyricsAlignmentCompletionService.Classify(GoodResult(confidence));

            Assert.That(
                classification.Status,
                Is.Not.EqualTo(SongLyricsStatus.Published),
                $"Confidence {confidence} must not publish.");
        }
    }

    [Test]
    public void TheConfidenceChangesNothingTheCreatorReads()
    {
        // The score used to pick between two greetings, one of which warned the creator to expect
        // work. It is systematically pessimistic - alignments a creator would call perfect score in
        // the fifties - so the warning fired on good songs and read as a verdict on them. Success now
        // says the same thing at every score and lets the creator judge by ear.
        var messages = new[] { 0d, 0.42d, 0.7d, 0.91d, 1.0d }
            .Select(c => LyricsAlignmentCompletionService.Classify(GoodResult(c)).Message)
            .Distinct()
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.Count.EqualTo(1), "The score must not change the wording.");
            Assert.That(messages[0], Does.Contain("Publish"));
            Assert.That(messages[0], Does.Not.Contain("%"), "No score in creator-facing copy.");
        });
    }

    [Test]
    public void ALowConfidenceResultIsKeptForReviewRatherThanDiscarded()
    {
        // The creator may still want to inspect or export these, and they are the starting point for
        // any correction pass. Throwing away minutes of compute because a number was short of a
        // threshold would be the wrong trade.
        var result = GoodResult(confidence: 0.42d);

        var classification = LyricsAlignmentCompletionService.Classify(result);

        Assert.Multiple(() =>
        {
            Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.NeedsReview));
            Assert.That(classification.FailureCode, Is.Null, "Needing review is not a failure.");
        });
    }

    [Test]
    public void ASuccessfulRunNeverQuotesAScoreAtTheCreator()
    {
        // The point of the whole change, pinned here as well as in the wording test above: whatever
        // the aligner thought of itself, none of it reaches the creator's screen.
        var classification = LyricsAlignmentCompletionService.Classify(GoodResult(confidence: 0.52d));

        Assert.Multiple(() =>
        {
            Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.NeedsReview));
            Assert.That(classification.Message, Does.Not.Contain("52"));
            Assert.That(classification.Message, Does.Not.Contain("confiden"));
        });
    }

    [Test]
    public void AMissingConfidenceIsTreatedAsNoConfidence()
    {
        // A Function that reported no score must not be read as having reported a perfect one.
        var result = GoodResult(confidence: null);

        var classification = LyricsAlignmentCompletionService.Classify(result);

        Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.NeedsReview));
    }

    [Test]
    public void NothingMatchingIsAFailureEvenAtHighConfidence()
    {
        // Almost always the wrong lyrics pasted against the wrong song, which is worth saying rather
        // than burying under a percentage.
        var result = GoodResult(confidence: 0.99d);
        result.MatchedTokenCount = 0;

        var classification = LyricsAlignmentCompletionService.Classify(result);

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

        var classification = LyricsAlignmentCompletionService.Classify(result);

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

        var classification = LyricsAlignmentCompletionService.Classify(result);

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

        var classification = LyricsAlignmentCompletionService.Classify(result);

        Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.NeedsReview), "Tolerated, not failed.");
    }

    [Test]
    public void MostLinesGoingUnplacedIsAFailureRatherThanLowConfidence()
    {
        // A failed alignment wearing a plausible score: the words it did place may well be placed
        // confidently, which is exactly why the confidence gate alone would let this through.
        var result = GoodResult(confidence: 0.88d);
        result.LineCount = 40;
        result.LinesWithTimingCount = 9;

        var classification = LyricsAlignmentCompletionService.Classify(result);

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

        var classification = LyricsAlignmentCompletionService.Classify(result);

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

        var classification = LyricsAlignmentCompletionService.Classify(result);

        Assert.That(classification.Status, Is.EqualTo(SongLyricsStatus.NeedsReview), "Not a failure.");
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
