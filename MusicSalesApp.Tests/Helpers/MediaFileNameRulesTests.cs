using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Tests.Helpers;

[TestFixture]
public class MediaFileNameRulesTests
{
    [TestCase("Boof")]
    [TestCase("Song 2")]
    [TestCase("Night-Drive")]
    [TestCase("Night Drive (Remix)")]
    [TestCase("Été 東京 4")]
    public void ValidTitles_AreAccepted(string title)
        => Assert.That(MediaFileNameRules.IsValidTitle(title), Is.True);

    [TestCase("")]
    [TestCase(" Song")]
    [TestCase("Song ")]
    [TestCase("Song--Mix")]
    [TestCase("Song  Mix")]
    [TestCase("Song(Remix)")]
    [TestCase("Song ()")]
    [TestCase("Song ((Remix))")]
    [TestCase("Artist's Song")]
    [TestCase("Song_Name")]
    [TestCase("Song@Home")]
    [TestCase("Song.Remix")]
    public void InvalidTitles_AreRejected(string title)
        => Assert.That(MediaFileNameRules.IsValidTitle(title), Is.False);

    [TestCase("Boof.mp3")]
    [TestCase("Night Drive (Remix).WAV")]
    [TestCase("Été.flac")]
    [TestCase("Song-2.ogg")]
    [TestCase("Song_Name.mp3")]
    [TestCase("Song_Name (Radio_Edit).wav")]
    [TestCase("Song.m4a")]
    [TestCase("Song.aac")]
    [TestCase("Song.wma")]
    public void ValidAudioNames_AreAccepted(string fileName)
        => Assert.That(MediaFileNameRules.IsValidAudioFileName(fileName), Is.True);

    [TestCase("Song.mp3.tmp")]
    [TestCase("Song..mp3")]
    [TestCase("Song__mp3.mp3")]
    [TestCase("_Song.mp3")]
    [TestCase("Song_.mp3")]
    [TestCase("Song.mp4")]
    [TestCase("Song.mp3@")]
    public void InvalidAudioNames_AreRejected(string fileName)
        => Assert.That(MediaFileNameRules.IsValidAudioFileName(fileName), Is.False);

    [TestCase("Cover.jpg")]
    [TestCase("Cover Art.jpeg")]
    [TestCase("Cover (Square).PNG")]
    [TestCase("Cover_Art.png")]
    public void ValidImageNames_AreAccepted(string fileName)
        => Assert.That(MediaFileNameRules.IsValidImageFileName(fileName), Is.True);

    [TestCase("Cover.gif")]
    [TestCase("Cover.png.exe")]
    [TestCase("Cover_.jpg")]
    [TestCase("Cover__Art.jpg")]
    public void InvalidImageNames_AreRejected(string fileName)
        => Assert.That(MediaFileNameRules.IsValidImageFileName(fileName), Is.False);

    [Test]
    public void TitleLongerThan200Characters_IsRejected()
        => Assert.That(MediaFileNameRules.IsValidTitle(new string('A', 201)), Is.False);

    [Test]
    public void UnderscoresInFilenameBase_BecomeSpacesInSongTitle()
        => Assert.That(
            MediaFileNameRules.ToSongTitleFromBaseName("Night_Drive (Radio_Edit)"),
            Is.EqualTo("Night Drive (Radio Edit)"));

    [TestCase("Old_Title", "underscore '_' (U+005F)")]
    [TestCase("Artist's Song", "apostrophe \"'\" (U+0027)")]
    [TestCase("Song@Home", "at sign '@' (U+0040)")]
    [TestCase("Song.Remix", "period '.' (U+002E)")]
    public void TitleValidationMessage_DisplaysEachInvalidCharacter(string title, string expectedDetail)
    {
        var message = MediaFileNameRules.GetTitleValidationMessage(
            title,
            wasAcceptedUnderPreviousRules: true);

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.StartWith("This existing title was accepted under previous rules"));
            Assert.That(message, Does.Contain(expectedDetail));
        });
    }

    [Test]
    public void NewInvalidTitleMessage_DoesNotClaimItWasPreviouslyAccepted()
        => Assert.That(
            MediaFileNameRules.GetTitleValidationMessage("New_Title", wasAcceptedUnderPreviousRules: false),
            Does.StartWith("The song title is invalid"));

    [TestCase("Convoy & Crown.PNG", "ampersand '&' (U+0026)")]
    [TestCase("Artist's Song.wav", "apostrophe \"'\" (U+0027)")]
    [TestCase("Song@Home.mp3", "at sign '@' (U+0040)")]
    public void FileNameValidationMessage_DisplaysInvalidCharacters(string fileName, string expectedDetail)
        => Assert.That(
            MediaFileNameRules.GetFileNameValidationMessage(fileName),
            Does.Contain(expectedDetail));

    [Test]
    public void FileNameValidationMessage_ExplainsMultiplePeriodsAndUnsupportedExtension()
    {
        var message = MediaFileNameRules.GetFileNameValidationMessage("Song.mp3.tmp");

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("exactly one period"));
            Assert.That(message, Does.Contain("found 2"));
            Assert.That(message, Does.Contain("unsupported extension '.tmp'"));
        });
    }

    [TestCase("Song__Name.wav", "cannot be adjacent or repeated")]
    [TestCase("_Song.wav", "must begin with a letter or number")]
    [TestCase("Song(Remix).wav", "parentheses are allowed only once")]
    public void FileNameValidationMessage_ExplainsStructuralProblem(string fileName, string expectedDetail)
        => Assert.That(
            MediaFileNameRules.GetFileNameValidationMessage(fileName),
            Does.Contain(expectedDetail));

    [TestCase("Song_Name (Radio_Edit).wav")]
    [TestCase("Cover Art.png")]
    [TestCase("Été-2026.MP3")]
    public void ValidFileName_HasNoDiagnosticErrors(string fileName)
        => Assert.That(MediaFileNameRules.GetFileNameValidationErrors(fileName), Is.Empty);
}
