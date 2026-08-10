using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The exact base-name fallback the upload page uses when the Azure Function cannot pair a batch —
/// no queue configured, no answer inside the deadline, or an outright failure.
///
/// <para>
/// Model-driven matching is not covered here because it no longer lives here: it runs in the
/// Function, against a service these tests would have to fake wholesale to say anything about. What
/// matters on this side is that the degraded path is correct and total — every audio file gets a
/// row, every unmatched image is reported — because it is what stands between a model outage and a
/// creator's batch failing.
/// </para>
/// </summary>
[TestFixture]
public class FileMatchingServiceTests
{
    private FileMatchingService _service = null!;

    [SetUp]
    public void Setup() => _service = new FileMatchingService();

    [Test]
    public async Task OneSongAndOneImage_ArePairedWhateverTheyAreCalled()
    {
        // A creator who selected exactly two files has already said what they mean. Base-name
        // pairing would leave these two unmatched - an ordinary pair of filenames producing a bare
        // song and an orphaned image, for a reason nobody would recognise as a reason.
        var result = await _service.MatchFilesAsync(["track-final-v3.wav"], ["artwork.png"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs, Has.Count.EqualTo(1));
            Assert.That(result.Pairs[0].AudioFileName, Is.EqualTo("track-final-v3.wav"));
            Assert.That(result.Pairs[0].ImageFileName, Is.EqualTo("artwork.png"));
            Assert.That(result.UnmatchedImageFiles, Is.Empty);
        });
    }

    [Test]
    public async Task OneSongAndTwoImages_StillNeedTheNamesToAgree()
    {
        // The rule is "one and one", not "one song". With a choice to make, guessing is what paired
        // a headshot with an unrelated track.
        var result = await _service.MatchFilesAsync(["track.wav"], ["a.png", "b.png"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs[0].ImageFileName, Is.Null);
            Assert.That(result.UnmatchedImageFiles, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task TwoSongsAndOneImage_StillNeedTheNamesToAgree()
    {
        var result = await _service.MatchFilesAsync(["first.wav", "second.wav"], ["artwork.png"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs.Where(pair => pair.ImageFileName is not null), Is.Empty);
            Assert.That(result.UnmatchedImageFiles, Is.EqualTo(new[] { "artwork.png" }));
        });
    }

    [Test]
    public async Task NoImages_ReturnsEveryAudioFileWithNoImage()
    {
        var result = await _service.MatchFilesAsync(
            ["dark_night.mp3", "sunny_day.wav"], []);

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs, Has.Count.EqualTo(2));
            Assert.That(result.Pairs.All(pair => pair.ImageFileName is null), Is.True);
            Assert.That(result.UnmatchedImageFiles, Is.Empty);
        });
    }

    [Test]
    public async Task NoAudio_ReportsEveryImageAsUnmatched()
    {
        var result = await _service.MatchFilesAsync([], ["dark_night.jpg"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs, Is.Empty);
            Assert.That(result.UnmatchedImageFiles, Is.EqualTo(new[] { "dark_night.jpg" }));
        });
    }

    [Test]
    public async Task AnExactBaseNameMatch_IsPaired()
    {
        var result = await _service.MatchFilesAsync(["dark_night.mp3"], ["dark_night.jpg"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs, Has.Count.EqualTo(1));
            Assert.That(result.Pairs[0].AudioFileName, Is.EqualTo("dark_night.mp3"));
            Assert.That(result.Pairs[0].ImageFileName, Is.EqualTo("dark_night.jpg"));
            Assert.That(result.Pairs[0].NormalizedName, Is.EqualTo("Dark Night"));
            Assert.That(result.UnmatchedImageFiles, Is.Empty);
        });
    }

    [Test]
    public async Task MatchingIsCaseInsensitive()
    {
        var result = await _service.MatchFilesAsync(["DarkNight.mp3"], ["darknight.jpg"]);

        Assert.That(result.Pairs[0].ImageFileName, Is.EqualTo("darknight.jpg"));
    }

    [Test]
    public async Task AMasteredSuffixIsIgnored()
    {
        var result = await _service.MatchFilesAsync(["dark_night_mastered.mp3"], ["dark_night.jpg"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs[0].ImageFileName, Is.EqualTo("dark_night.jpg"));
            Assert.That(result.Pairs[0].NormalizedName, Is.EqualTo("Dark Night"));
        });
    }

    [Test]
    public async Task AnImageMatchingNothing_IsReportedAsUnmatched()
    {
        var result = await _service.MatchFilesAsync(
            ["dark_night.mp3"], ["dark_night.jpg", "unrelated_cover.png"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs[0].ImageFileName, Is.EqualTo("dark_night.jpg"));
            Assert.That(result.UnmatchedImageFiles, Is.EqualTo(new[] { "unrelated_cover.png" }));
        });
    }

    [Test]
    public async Task AnAudioFileMatchingNothing_StillGetsARow()
    {
        // Dropping it would silently lose a song the creator selected.
        var result = await _service.MatchFilesAsync(
            ["dark_night.mp3", "sunny_day.mp3"], ["dark_night.jpg"]);

        var unmatched = result.Pairs.Single(pair => pair.ImageFileName is null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs, Has.Count.EqualTo(2));
            Assert.That(unmatched.AudioFileName, Is.EqualTo("sunny_day.mp3"));
            Assert.That(unmatched.NormalizedName, Is.EqualTo("Sunny Day"));
        });
    }

    [Test]
    public async Task OneImageIsNeverPairedWithTwoSongs()
    {
        // Two songs normalizing to the same name is a creator mistake, but pairing both with the
        // same image would upload one file as two songs' art and leave the second row inconsistent.
        var result = await _service.MatchFilesAsync(
            ["dark_night.mp3", "dark-night.wav"], ["dark_night.jpg"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Pairs, Has.Count.EqualTo(2));
            Assert.That(result.Pairs.Count(pair => pair.ImageFileName == "dark_night.jpg"), Is.EqualTo(1));
            Assert.That(result.Pairs.Count(pair => pair.ImageFileName is null), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task NormalizedNamesAreTitleCased()
    {
        var result = await _service.MatchFilesAsync(["my_awesome_song.mp3"], ["my_awesome_song.jpg"]);

        Assert.That(result.Pairs[0].NormalizedName, Is.EqualTo("My Awesome Song"));
    }
}
