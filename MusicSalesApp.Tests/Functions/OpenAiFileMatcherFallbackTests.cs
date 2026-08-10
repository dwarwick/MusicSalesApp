using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Functions.Matching;

namespace MusicSalesApp.Tests.Functions;

/// <summary>
/// The Function's own fallback pairing, used when no API key is configured or the model call fails.
///
/// <para>
/// Deliberately identical in behaviour to the web app's <c>FileMatchingService</c>, because either
/// can be the one that answers a given batch — the Function falls back internally, and the page
/// falls back when the Function never replies. A creator must not get a different pairing depending
/// on which layer gave up.
/// </para>
/// </summary>
[TestFixture]
public class OpenAiFileMatcherFallbackTests
{
    private static CoverArtMatchRequest Request(string[] audio, string[] images)
        => new()
        {
            BatchId = Guid.NewGuid(),
            CreatorId = 42,
            AudioFileNames = audio,
            Images = images.Select((name, index) => new CoverArtMatchCandidate
            {
                Index = index,
                FileName = name,
                BlobPath = $"batch/x/{index}{Path.GetExtension(name)}",
                ContentType = "image/jpeg"
            }).ToList()
        };

    [Test]
    public void OneSongAndOneImage_ArePairedWhateverTheyAreCalled()
    {
        // Base-name pairing would leave these unmatched, which is not an answer a creator who
        // selected exactly two files would recognise. Nothing else in this method reaches it: the
        // rule below needs the normalized names to agree.
        var result = IOpenAiFileMatcher.FallbackMatch(
            Request(["track-final-v3.wav"], ["artwork.png"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs, Has.Count.EqualTo(1));
            Assert.That(result.Pairs[0].ImageIndex, Is.Zero);
            Assert.That(result.UnmatchedImageIndexes, Is.Empty);
        });
    }

    [Test]
    public void TwoSongsAndOneImage_StillNeedTheNamesToAgree()
    {
        // The rule is "one and one", not "one image". With a choice to make, guessing is exactly
        // what produced a headshot paired with an unrelated track.
        var result = IOpenAiFileMatcher.FallbackMatch(
            Request(["first.wav", "second.wav"], ["artwork.png"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs.Where(pair => pair.ImageIndex is not null), Is.Empty);
            Assert.That(result.UnmatchedImageIndexes, Is.EqualTo(new[] { 0 }));
        });
    }

    [Test]
    public void OneSongAndTwoImages_StillNeedTheNamesToAgree()
    {
        var result = IOpenAiFileMatcher.FallbackMatch(
            Request(["track.wav"], ["a.png", "b.png"]));

        Assert.That(result.Pairs[0].ImageIndex, Is.Null);
    }

    [Test]
    public void AnExactBaseNameMatch_IsPaired()
    {
        var result = IOpenAiFileMatcher.FallbackMatch(
            Request(["dark_night.mp3"], ["dark_night.jpg"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs, Has.Count.EqualTo(1));
            Assert.That(result.Pairs[0].AudioIndex, Is.Zero);
            Assert.That(result.Pairs[0].ImageIndex, Is.Zero);
            Assert.That(result.Pairs[0].NormalizedName, Is.EqualTo("Dark Night"));
            Assert.That(result.UnmatchedImageIndexes, Is.Empty);
            Assert.That(result.UsedFallback, Is.True);
        });
    }

    [Test]
    public void AMasteredSuffixAndSeparatorsAreIgnored()
    {
        var result = IOpenAiFileMatcher.FallbackMatch(
            Request(["DARK-night_mastered.wav"], ["dark_night.jpg"]));

        Assert.That(result.Pairs[0].ImageIndex, Is.Zero);
    }

    [Test]
    public void EveryAudioFileGetsARow_EvenWithNoMatch()
    {
        // Dropping one would silently lose a song the creator selected.
        var result = IOpenAiFileMatcher.FallbackMatch(
            Request(["dark_night.mp3", "sunny_day.mp3"], ["dark_night.jpg"]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs, Has.Count.EqualTo(2));
            Assert.That(result.Pairs.Single(pair => pair.AudioIndex == 1).ImageIndex, Is.Null);
        });
    }

    [Test]
    public void UnmatchedImagesAreReportedByIndex()
    {
        var result = IOpenAiFileMatcher.FallbackMatch(
            Request(["dark_night.mp3"], ["dark_night.jpg", "unrelated.png"]));

        Assert.That(result.UnmatchedImageIndexes, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void OneImageIsNeverClaimedByTwoSongs()
    {
        var result = IOpenAiFileMatcher.FallbackMatch(
            Request(["dark_night.mp3", "dark-night.wav"], ["dark_night.jpg"]));

        Assert.That(result.Pairs.Count(pair => pair.ImageIndex == 0), Is.EqualTo(1));
    }

    [Test]
    public void TheBatchAndCreatorAreEchoedBack()
    {
        // The callback has no database row to look either up from - they round-trip through the
        // queue message, and the creator id is what names the SignalR group on the way back.
        var request = Request(["a.mp3"], ["a.jpg"]);

        var result = IOpenAiFileMatcher.FallbackMatch(request);

        Assert.Multiple(() =>
        {
            Assert.That(result.BatchId, Is.EqualTo(request.BatchId));
            Assert.That(result.CreatorId, Is.EqualTo(42));
        });
    }

    [Test]
    public void AnEmptyBatch_IsHandled()
    {
        var result = IOpenAiFileMatcher.FallbackMatch(Request([], []));

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs, Is.Empty);
            Assert.That(result.UnmatchedImageIndexes, Is.Empty);
        });
    }
}
