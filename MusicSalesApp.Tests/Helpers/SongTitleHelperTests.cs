using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class SongTitleHelperTests
{
    private static readonly Guid MediaGuid = Guid.Parse("3f2a9c1e-5b7d-4e6f-8a90-1c2d3e4f5a6b");

    [TestCase("Night_Drive.mp3", "Night Drive")]
    [TestCase("Night Drive.wav", "Night Drive")]
    [TestCase("my song's @ mix v1.2 (remix)!.mp3", "my song's @ mix v1.2 (remix)!")]
    [TestCase("Été.flac", "Été")]
    [TestCase("", "")]
    [TestCase(null, "")]
    public void FromFileName_StripsTheExtensionAndConvertsUnderscores(string fileName, string expected)
        => Assert.That(SongTitleHelper.FromFileName(fileName), Is.EqualTo(expected));

    [TestCase("Night Drive")]
    [TestCase("Artist's Song @ 2x & More!")]
    [TestCase("Song..with..dots")]
    [TestCase("Été / Hiver")]
    [TestCase("song__name")]
    public void PunctuationAndUnicode_AreAcceptedInTitles(string title)
        => Assert.That(SongTitleHelper.IsValidTitle(title), Is.True, title);

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void BlankTitles_AreRejected(string title)
        => Assert.That(SongTitleHelper.IsValidTitle(title), Is.False);

    [Test]
    public void TitleLength_IsBoundedAtTwoHundredCharacters()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SongTitleHelper.IsValidTitle(new string('a', 200)), Is.True);
            Assert.That(SongTitleHelper.IsValidTitle(new string('a', 201)), Is.False);
        });
    }

    [Test]
    public void GetTitleValidationErrors_ReportsTheActualLength()
        => Assert.That(
            SongTitleHelper.GetTitleValidationErrors(new string('a', 250)).Single(),
            Does.Contain("250").And.Contain("200"));

    [Test]
    public void GetEffectiveTitle_PrefersTheStoredTitle()
        => Assert.That(
            SongTitleHelper.GetEffectiveTitle("Real Title", "Night_Drive/Night_Drive.mp3"),
            Is.EqualTo("Real Title"));

    [Test]
    public void GetEffectiveTitle_LegacySongWithNoTitle_DerivesFromTheBlobPath()
        => Assert.That(
            SongTitleHelper.GetEffectiveTitle(null, "Night_Drive/Night_Drive.mp3"),
            Is.EqualTo("Night Drive"));

    [Test]
    public void GetEffectiveTitle_GuidSongWithNoTitle_DoesNotSurfaceTheGuid()
    {
        // Deriving here would show a listener something like "3f2a...-music" as the song name.
        Assert.That(
            SongTitleHelper.GetEffectiveTitle(null, SongMediaPaths.Playback(MediaGuid)),
            Is.Empty);
    }

    [Test]
    public void GetEffectiveTitle_SkipsGuidPathsButStillUsesALegacyFallback()
        => Assert.That(
            SongTitleHelper.GetEffectiveTitle(
                null,
                SongMediaPaths.Playback(MediaGuid),
                "Night_Drive/Night_Drive.mp3"),
            Is.EqualTo("Night Drive"));
}
