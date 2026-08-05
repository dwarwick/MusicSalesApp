using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using SkiaSharp;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// What is left of this service after FFmpeg moved to Azure Functions: filename pairing for the
/// upload page, and the album-cover path. The song upload tests that used to live here moved to
/// <see cref="SongUploadJobServiceTests"/> and <see cref="MediaProcessingCompletionServiceTests"/>,
/// which is where staging and assembly now happen.
/// </summary>
[TestFixture]
public class MusicUploadServiceTests
{
    private Mock<IAzureStorageService> _storage = null!;
    private Mock<ISongMetadataService> _metadata = null!;
    private MusicUploadService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _storage = new Mock<IAzureStorageService>();
        _metadata = new Mock<ISongMetadataService>();
        _storage.Setup(service => service.EnsureContainerExistsAsync()).Returns(Task.CompletedTask);
        _storage.Setup(service => service.ExistsAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storage.Setup(service => service.UploadAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _storage.Setup(service => service.DeleteAsync(It.IsAny<string>())).ReturnsAsync(true);
        _metadata.Setup(service => service.UpsertAsync(It.IsAny<SongMetadata>()))
            .ReturnsAsync((SongMetadata item) => item);

        _service = new MusicUploadService(
            _storage.Object,
            _metadata.Object,
            Mock.Of<ILogger<MusicUploadService>>());
    }

    [TestCase("Song.mp3", "Song.jpg", true)]
    [TestCase("Song.mp3", "Other.jpg", false)]
    public void Pairing_UsesValidatedBasenames(string audio, string image, bool expected)
        => Assert.That(_service.ValidateFilePairing(audio, image), Is.EqualTo(expected));

    [TestCase("Song.mp3", "Song")]
    [TestCase("Song_Name.wav", "Song_Name")]
    [TestCase("Cover.JPEG", "Cover")]
    [TestCase("", "")]
    [TestCase(null, "")]
    public void GetNormalizedBaseName_HandlesSupportedNamesAndBlankInput(string fileName, string expected)
        => Assert.That(_service.GetNormalizedBaseName(fileName), Is.EqualTo(expected));

    [TestCase("song.MP3", "SONG.png", true)]
    [TestCase("Song.wav", "Song.jpeg", true)]
    [TestCase("", "Song.png", false)]
    [TestCase("Song.mp3", "", false)]
    public void Pairing_IsCaseInsensitiveAndRejectsBlankNames(string audio, string image, bool expected)
        => Assert.That(_service.ValidateFilePairing(audio, image), Is.EqualTo(expected));

    [Test]
    public void ValidateAllFilePairings_SupportedMixedAudioAndImages_AreMatched()
    {
        var result = _service.ValidateAllFilePairings([
            "Song.mp3", "Song.png", "Wave.wav", "Wave.jpg", "Lossless.flac", "Lossless.jpeg"
        ], requireAudioFile: true, requireCoverArt: true);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public void ValidateAllFilePairings_ReportsEveryUnmatchedSide()
    {
        var result = _service.ValidateAllFilePairings(
            ["Audio.mp3", "Different.png"],
            requireAudioFile: true,
            requireCoverArt: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.UnmatchedMp3Files, Is.EqualTo(new[] { "Audio.mp3" }));
            Assert.That(result.UnmatchedAlbumArtFiles, Is.EqualTo(new[] { "Different.png" }));
        });
    }

    [Test]
    public void ValidateAllFilePairings_OnlyCoverArt_IsAllowedOnlyWhenAudioIsOptional()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_service.ValidateAllFilePairings(["Cover.png"], requireAudioFile: false).IsValid, Is.True);
            Assert.That(_service.ValidateAllFilePairings([], requireAudioFile: false).IsValid, Is.False);
            Assert.That(_service.ValidateAllFilePairings(null, requireAudioFile: true).IsValid, Is.False);
        });
    }

    [Test]
    public async Task UploadAlbumCover_DecodablePng_StoresMetadataAndCorrectMimeType()
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Green);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        await using var stream = new MemoryStream(data.ToArray());

        var path = await _service.UploadAlbumCoverAsync(
            stream, "Cover.png", "Album Name", creatorId: 5);

        Assert.That(path, Is.EqualTo("Album Name/Cover_cover.png"));
        _storage.Verify(service => service.UploadAsync(
            "Album Name/Cover_cover.png", It.IsAny<Stream>(), "image/png"), Times.Once);
        _metadata.Verify(service => service.UpsertAsync(It.Is<SongMetadata>(item =>
            item.ImageBlobPath == path && item.IsAlbumCover && item.CreatorId == 5)), Times.Once);
    }

    [Test]
    public void UploadAlbumCover_InvalidArgumentsOrContent_PerformNoWrites()
    {
        using var corrupt = new MemoryStream([1, 2, 3]);
        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<ArgumentNullException>(() =>
                _service.UploadAlbumCoverAsync(null, "Cover.png", "Album"));
            Assert.ThrowsAsync<InvalidDataException>(() =>
                _service.UploadAlbumCoverAsync(corrupt, "Cover.gif", "Album"));
            Assert.ThrowsAsync<ArgumentException>(() =>
                _service.UploadAlbumCoverAsync(corrupt, "Cover.png", ""));
            Assert.ThrowsAsync<InvalidDataException>(() =>
                _service.UploadAlbumCoverAsync(corrupt, "Cover.png", "Album"));
        });
        _storage.Verify(service => service.UploadAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }
}
