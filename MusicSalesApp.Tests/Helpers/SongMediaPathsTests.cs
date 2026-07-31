using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class SongMediaPathsTests
{
    private static readonly Guid MediaGuid = Guid.Parse("3f2a9c1e-5b7d-4e6f-8a90-1c2d3e4f5a6b");
    private const string Name = "3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b";

    [Test]
    public void Builders_ProduceTheDocumentedLayout()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SongMediaPaths.Folder(MediaGuid), Is.EqualTo(Name));
            Assert.That(SongMediaPaths.Playback(MediaGuid), Is.EqualTo($"{Name}/{Name}-music.mp3"));
            Assert.That(SongMediaPaths.OriginalAudio(MediaGuid, ".wav"), Is.EqualTo($"{Name}/{Name}-music-original.wav"));
            Assert.That(SongMediaPaths.CoverArt(MediaGuid, ".jpg"), Is.EqualTo($"{Name}/{Name}-coverart.jpg"));
            Assert.That(SongMediaPaths.OriginalCoverArt(MediaGuid, ".png"), Is.EqualTo($"{Name}/{Name}-coverart-original.png"));
            Assert.That(SongMediaPaths.FacebookImage(MediaGuid), Is.EqualTo($"{Name}/{Name}-fb.jpg"));
        });
    }

    [Test]
    public void GuidIsFormattedWithoutHyphens()
        => Assert.That(SongMediaPaths.Folder(MediaGuid), Does.Not.Contain("-"));

    [Test]
    public void OriginalAudio_ForAnMp3Source_CollapsesOntoThePlaybackBlob()
    {
        // An mp3 upload is both the original and the playback copy, so it is stored once.
        Assert.That(
            SongMediaPaths.OriginalAudio(MediaGuid, ".mp3"),
            Is.EqualTo(SongMediaPaths.Playback(MediaGuid)));
    }

    [TestCase(".wav")]
    [TestCase(".flac")]
    [TestCase(".m4a")]
    public void OriginalAudio_ForANonMp3Source_IsASeparateBlob(string extension)
        => Assert.That(
            SongMediaPaths.OriginalAudio(MediaGuid, extension),
            Is.Not.EqualTo(SongMediaPaths.Playback(MediaGuid)));

    [TestCase("WAV")]
    [TestCase(".WAV")]
    public void Extensions_AreNormalizedToLowerCaseWithALeadingDot(string extension)
        => Assert.That(
            SongMediaPaths.OriginalAudio(MediaGuid, extension),
            Is.EqualTo($"{Name}/{Name}-music-original.wav"));

    [Test]
    public void TryGetGuidFromPath_ReadsTheGuidFromTheFirstSegment()
    {
        Assert.That(SongMediaPaths.TryGetGuidFromPath(SongMediaPaths.Playback(MediaGuid), out var parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(MediaGuid));
    }

    [TestCase("Night Drive/Night Drive.mp3")]
    [TestCase("Night Drive.mp3")]
    [TestCase("")]
    [TestCase(null)]
    [TestCase("not-a-guid/whatever.mp3")]
    public void TryGetGuidFromPath_RejectsLegacyAndMalformedPaths(string blobPath)
        => Assert.That(SongMediaPaths.TryGetGuidFromPath(blobPath, out _), Is.False);

    [Test]
    public void TryGetGuidFromPath_AcceptsBackslashSeparators()
        => Assert.That(SongMediaPaths.TryGetGuidFromPath($@"{Name}\{Name}-music.mp3", out _), Is.True);

    [Test]
    public void FacebookImageFor_GuidSchemeArt_ResolvesToTheSongsFixedSharingImage()
        => Assert.That(
            SongMediaPaths.FacebookImageFor(SongMediaPaths.CoverArt(MediaGuid, ".jpg")),
            Is.EqualTo($"{Name}/{Name}-fb.jpg"));

    [Test]
    public void FacebookImageFor_LegacyArt_KeepsTheHistoricalUnderscoreSuffix()
    {
        // The "_fb" suffix and folder placement are unchanged; only the extension moved to JPEG.
        Assert.Multiple(() =>
        {
            Assert.That(SongMediaPaths.FacebookImageFor("Night Drive/Night Drive.jpg"),
                Is.EqualTo("Night Drive/Night Drive_fb.jpg"));
            Assert.That(SongMediaPaths.FacebookImageFor("Cover.png"), Is.EqualTo("Cover_fb.jpg"));
        });
    }

    [Test]
    public void FacebookImageCandidatesFor_ReturnsTheCurrentJpegAndTheSupersededPng()
    {
        // Sharing images moved from PNG to JPEG. Invalidation and the backfill both need the old
        // name so the orphaned PNG can be cleared rather than left in storage forever.
        Assert.Multiple(() =>
        {
            Assert.That(
                SongMediaPaths.FacebookImageCandidatesFor(SongMediaPaths.CoverArt(MediaGuid, ".jpg")),
                Is.EqualTo(new[] { $"{Name}/{Name}-fb.jpg", $"{Name}/{Name}-fb.png" }));

            Assert.That(
                SongMediaPaths.FacebookImageCandidatesFor("Night Drive/Night Drive.jpg"),
                Is.EqualTo(new[] { "Night Drive/Night Drive_fb.jpg", "Night Drive/Night Drive_fb.png" }));
        });
    }

    [TestCase("")]
    [TestCase(null)]
    public void FacebookImageCandidatesFor_BlankPath_IsEmpty(string coverArtPath)
        => Assert.That(SongMediaPaths.FacebookImageCandidatesFor(coverArtPath), Is.Empty);

    [TestCase("")]
    [TestCase(null)]
    public void FacebookImageFor_BlankInput_ReturnsEmpty(string coverArtPath)
        => Assert.That(SongMediaPaths.FacebookImageFor(coverArtPath), Is.Empty);

    [Test]
    public void ResolveCoverArtTarget_GuidSong_AlwaysWritesToTheSameFixedPath()
    {
        // Whatever the song currently points at, a GUID song's art has one home.
        Assert.Multiple(() =>
        {
            Assert.That(
                SongMediaPaths.ResolveCoverArtTarget(MediaGuid, "Legacy/Legacy.jpg", "Legacy/Legacy.mp3", "Title", ".png"),
                Is.EqualTo($"{Name}/{Name}-coverart.png"));
            Assert.That(
                SongMediaPaths.ResolveCoverArtTarget(MediaGuid, null, null, "Title", ".jpg"),
                Is.EqualTo($"{Name}/{Name}-coverart.jpg"));
        });
    }

    [Test]
    public void ResolveCoverArtTarget_LegacySong_ReusesTheExistingArtPathWithTheNewExtension()
        => Assert.That(
            SongMediaPaths.ResolveCoverArtTarget(null, "Night Drive/Night Drive.jpg", "Night Drive/Night Drive.mp3", "Night Drive", ".png"),
            Is.EqualTo("Night Drive/Night Drive.png"));

    [Test]
    public void ResolveCoverArtTarget_LegacySongWithNoArt_SitsBesideThePlaybackFile()
        => Assert.That(
            SongMediaPaths.ResolveCoverArtTarget(null, null, "Night Drive/Night Drive.mp3", "Night Drive", ".png"),
            Is.EqualTo("Night Drive/Night Drive.png"));

    [Test]
    public void ResolveCoverArtTarget_LegacySongWithNoArtOrAudio_FallsBackToTheTitle()
        => Assert.That(
            SongMediaPaths.ResolveCoverArtTarget(null, null, null, "Night Drive", ".png"),
            Is.EqualTo("Night Drive.png"));

    [Test]
    public void IsGuidScheme_DistinguishesTheTwoLayouts()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SongMediaPaths.IsGuidScheme(SongMediaPaths.Playback(MediaGuid)), Is.True);
            Assert.That(SongMediaPaths.IsGuidScheme("Night Drive/Night Drive.mp3"), Is.False);
        });
    }
}
