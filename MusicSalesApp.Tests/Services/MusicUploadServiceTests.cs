using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using SkiaSharp;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class MusicUploadServiceTests
{
    private Mock<IAzureStorageService> _storage = null!;
    private Mock<IMusicService> _music = null!;
    private Mock<ISongMetadataService> _metadata = null!;
    private Mock<IOpenGraphService> _openGraph = null!;
    private MusicUploadService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _storage = new Mock<IAzureStorageService>();
        _music = new Mock<IMusicService>();
        _metadata = new Mock<ISongMetadataService>();
        _openGraph = new Mock<IOpenGraphService>();
        _storage.Setup(service => service.EnsureContainerExistsAsync()).Returns(Task.CompletedTask);
        _storage.Setup(service => service.ExistsAsync(It.IsAny<string>())).ReturnsAsync(false);
        _storage.Setup(service => service.UploadAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _storage.Setup(service => service.DeleteAsync(It.IsAny<string>())).ReturnsAsync(true);
        _music.Setup(service => service.IsValidAudioFileAsync(It.IsAny<Stream>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _music.Setup(service => service.ValidateAudioDecodeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AudioDecodeResult.Playable(12.5));
        _metadata.Setup(service => service.ValidateUploadTargetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((SongMetadata)null);
        _metadata.Setup(service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()))
            .ReturnsAsync((SongMetadata item) => item);
        _metadata.Setup(service => service.UpsertAsync(It.IsAny<SongMetadata>()))
            .ReturnsAsync((SongMetadata item) => item);
        _service = new MusicUploadService(
            _storage.Object,
            _music.Object,
            _metadata.Object,
            _openGraph.Object,
            Mock.Of<ILogger<MusicUploadService>>());
    }

    [Test]
    public async Task InvalidFilename_PerformsNoWrites()
    {
        await using var audio = new MemoryStream([1, 2, 3]);
        Assert.ThrowsAsync<InvalidDataException>(() =>
            _service.UploadMusicWithoutAlbumArtAsync(audio, "Bad__Name.wav", creatorId: 1));
        _storage.Verify(service => service.UploadAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
        _metadata.Verify(service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()), Times.Never);
    }

    [Test]
    public async Task UnderscoreFilename_RetainsBlobNameAndStoresTitleWithSpaces()
    {
        var bytes = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        await using var audio = new MemoryStream(bytes);
        _music.Setup(service => service.IsMp3File("Night_Drive.mp3")).Returns(true);

        var result = await _service.UploadMusicWithoutAlbumArtAsync(
            audio, "Night_Drive.mp3", creatorId: 1);

        Assert.That(result.Mp3BlobPath, Is.EqualTo("Night_Drive/Night_Drive.mp3"));
        _metadata.Verify(service => service.UpsertValidatedUploadAsync(
            It.Is<SongMetadata>(item => item.SongTitle == "Night Drive"
                && item.OriginalAudioFileName == "Night_Drive.mp3")), Times.Once);
    }

    [Test]
    public async Task Mp3Upload_UsesOneBlobForOriginalAndPlayback()
    {
        var bytes = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        await using var audio = new MemoryStream(bytes);
        _music.Setup(service => service.IsMp3File("Boof.mp3")).Returns(true);

        var result = await _service.UploadMusicWithoutAlbumArtAsync(
            audio, "Boof.mp3", creatorId: 109);

        Assert.Multiple(() =>
        {
            Assert.That(result.OriginalAudioBlobPath, Is.EqualTo("Boof/Boof.mp3"));
            Assert.That(result.Mp3BlobPath, Is.EqualTo("Boof/Boof.mp3"));
            Assert.That(result.OriginalAudioFileSize, Is.EqualTo(bytes.Length));
        });
        _storage.Verify(service => service.UploadAsync(
            "Boof/Boof.mp3", It.IsAny<Stream>(), "audio/mpeg"), Times.Once);
        _metadata.Verify(service => service.UpsertValidatedUploadAsync(
            It.Is<SongMetadata>(item => item.SongTitle == "Boof"
                && item.OriginalAudioBlobPath == item.Mp3BlobPath
                && item.TrackLength == 12.5)), Times.Once);
    }

    [Test]
    public async Task WavUpload_WhenConverterDisposesItsInput_PreservesExactOriginalAndCreatesPlaybackMp3()
    {
        var original = new byte[] { 82, 73, 70, 70, 4, 0, 0, 0, 87, 65, 86, 69 };
        var converted = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        await using var audio = new MemoryStream(original);
        _music.Setup(service => service.IsMp3File("Night Drive.wav")).Returns(false);
        _music.Setup(service => service.ConvertToMp3Async(
                It.IsAny<Stream>(), "Night Drive.wav", It.IsAny<IProgress<double>>()))
            .Returns<Stream, string, IProgress<double>>((conversionInput, _, _) =>
            {
                conversionInput.Dispose();
                return Task.FromResult<Stream>(new MemoryStream(converted));
            });
        var uploaded = new Dictionary<string, byte[]>();
        _storage.Setup(service => service.UploadAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns<string, Stream, string>(async (path, stream, _) =>
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                uploaded[path] = copy.ToArray();
            });

        var result = await _service.UploadMusicWithoutAlbumArtAsync(
            audio, "Night Drive.wav", creatorId: 1);

        Assert.Multiple(() =>
        {
            Assert.That(uploaded["Night Drive/Night Drive.wav"], Is.EqualTo(original));
            Assert.That(uploaded["Night Drive/Night Drive.mp3"], Is.EqualTo(converted));
            Assert.That(result.OriginalAudioContentType, Is.EqualTo("audio/wav"));
        });
    }

    [Test]
    public async Task Mp3Upload_WhenValidatorsDisposeTheirStreams_StillUploadsSuccessfully()
    {
        var original = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        await using var audio = new MemoryStream(original);
        _music.Setup(service => service.IsMp3File("Closed Stream.mp3")).Returns(true);
        _music.Setup(service => service.IsValidAudioFileAsync(
                It.IsAny<Stream>(), "Closed Stream.mp3"))
            .Returns<Stream, string>((validationStream, _) =>
            {
                validationStream.Dispose();
                return Task.FromResult(true);
            });
        _music.Setup(service => service.ValidateAudioDecodeAsync(
                It.IsAny<Stream>(), "Closed Stream.mp3", It.IsAny<CancellationToken>()))
            .Returns<Stream, string, CancellationToken>((durationStream, _, _) =>
            {
                durationStream.Dispose();
                return Task.FromResult(AudioDecodeResult.Playable(42));
            });
        byte[] uploaded = null;
        _storage.Setup(service => service.UploadAsync(
                "Closed Stream/Closed Stream.mp3", It.IsAny<Stream>(), "audio/mpeg"))
            .Returns<string, Stream, string>(async (_, uploadStream, _) =>
            {
                using var copy = new MemoryStream();
                await uploadStream.CopyToAsync(copy);
                uploaded = copy.ToArray();
            });

        var result = await _service.UploadMusicWithoutAlbumArtAsync(
            audio,
            "Closed Stream.mp3",
            creatorId: 1);

        Assert.Multiple(() =>
        {
            Assert.That(uploaded, Is.EqualTo(original));
            Assert.That(result.TrackDuration, Is.EqualTo(42));
            Assert.That(audio.CanRead, Is.True, "The caller-owned stream should remain open.");
        });
    }

    [Test]
    public async Task MetadataFailure_DeletesEveryNewBlob()
    {
        await using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);
        _music.Setup(service => service.IsMp3File("Song.mp3")).Returns(true);
        _metadata.Setup(service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UploadMusicWithoutAlbumArtAsync(audio, "Song.mp3", creatorId: 1));

        _storage.Verify(service => service.DeleteAsync("Song/Song.mp3"), Times.Once);
    }

    [Test]
    public async Task ConversionFailure_PerformsNoStorageOrMetadataWrites()
    {
        await using var audio = new MemoryStream([82, 73, 70, 70]);
        _music.Setup(service => service.IsMp3File("Song.wav")).Returns(false);
        _music.Setup(service => service.ConvertToMp3Async(
                It.IsAny<Stream>(), "Song.wav", It.IsAny<IProgress<double>>()))
            .ThrowsAsync(new InvalidDataException("decoder failed"));

        Assert.ThrowsAsync<InvalidDataException>(() =>
            _service.UploadMusicWithoutAlbumArtAsync(audio, "Song.wav", creatorId: 1));
        _storage.Verify(service => service.EnsureContainerExistsAsync(), Times.Never);
        _metadata.Verify(service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()), Times.Never);
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
    public void UploadWithAlbumArt_NullOrInvalidArguments_AreRejectedBeforeWrites()
    {
        using var audio = new MemoryStream([1]);
        using var image = new MemoryStream([1]);

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<ArgumentNullException>(() => _service.UploadMusicWithAlbumArtAsync(
                null, "Song.mp3", image, "Song.png", creatorId: 1));
            Assert.ThrowsAsync<InvalidDataException>(() => _service.UploadMusicWithAlbumArtAsync(
                audio, "", image, "Song.png", creatorId: 1));
            Assert.ThrowsAsync<InvalidDataException>(() => _service.UploadMusicWithAlbumArtAsync(
                audio, "Song.mp3", image, "", creatorId: 1));
        });
        _storage.Verify(service => service.UploadAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
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

    [Test]
    public async Task PreviousOriginalDeleteFailsAfterMetadataCommit_DoesNotRollBackNewBlob()
    {
        await using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);
        _music.Setup(service => service.IsMp3File("Song.mp3")).Returns(true);
        _metadata.Setup(service => service.ValidateUploadTargetAsync(
                "Song/Song.mp3", "Song/Song.mp3", null, 1))
            .ReturnsAsync(new SongMetadata { OriginalAudioBlobPath = "Song/Song.wav" });
        _storage.Setup(service => service.DeleteAsync("Song/Song.wav"))
            .ThrowsAsync(new InvalidOperationException("blob under lease"));

        var result = await _service.UploadMusicWithoutAlbumArtAsync(audio, "Song.mp3", creatorId: 1);

        Assert.That(result.Mp3BlobPath, Is.EqualTo("Song/Song.mp3"));
        _metadata.Verify(service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()), Times.Once);
        _storage.Verify(service => service.DeleteAsync("Song/Song.mp3"), Times.Never);
    }

    [Test]
    public async Task ReplacingCoverArtWithDifferentExtension_DeletesStalePreviousCoverArtBlob()
    {
        await using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Blue);
        using var skImage = SKImage.FromBitmap(bitmap);
        using var pngData = skImage.Encode(SKEncodedImageFormat.Png, 100);
        await using var image = new MemoryStream(pngData.ToArray());

        _music.Setup(service => service.IsMp3File("Song.mp3")).Returns(true);
        _metadata.Setup(service => service.ValidateUploadTargetAsync(
                "Song/Song.mp3", "Song/Song.mp3", "Song/Song.png", 1))
            .ReturnsAsync(new SongMetadata { ImageBlobPath = "Song/Song.jpg" });

        var result = await _service.UploadMusicWithAlbumArtAsync(
            audio, "Song.mp3", image, "Song.png", creatorId: 1);

        Assert.That(result.ImageBlobPath, Is.EqualTo("Song/Song.png"));
        _storage.Verify(service => service.DeleteAsync("Song/Song.jpg"), Times.Once);
    }

    [Test]
    public async Task DecoderInfrastructureFailure_PerformsNoStorageOrMetadataWrites()
    {
        await using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);
        _music.Setup(service => service.IsMp3File("Song.mp3")).Returns(true);
        _music.Setup(service => service.ValidateAudioDecodeAsync(
                It.IsAny<Stream>(), "Song.mp3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AudioDecodeResult.Inconclusive("FfmpegUnavailable", "FFmpeg was not found."));

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UploadMusicWithoutAlbumArtAsync(audio, "Song.mp3", creatorId: 1));

        Assert.That(exception!.Message, Does.Contain("decoder was unavailable").IgnoreCase);
        _storage.Verify(service => service.EnsureContainerExistsAsync(), Times.Never);
        _storage.Verify(service => service.UploadAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
        _metadata.Verify(service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()), Times.Never);
    }

    [Test]
    public async Task ReplacementOwnedByAnotherCreator_IsRejectedBeforeStorageWrites()
    {
        await using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);
        _music.Setup(service => service.IsMp3File("Song.mp3")).Returns(true);
        _metadata.Setup(service => service.ValidateUploadTargetAsync(
                "Song/Song.mp3", "Song/Song.mp3", null, 7))
            .ThrowsAsync(new UnauthorizedAccessException("belongs to another creator"));

        Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.UploadMusicWithoutAlbumArtAsync(audio, "Song.mp3", creatorId: 7));

        _storage.Verify(service => service.EnsureContainerExistsAsync(), Times.Never);
        _storage.Verify(service => service.UploadAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task MetadataFailure_WhenReplacingExistingBlob_RestoresOriginalBytesAndContentType()
    {
        var incoming = new byte[] { (byte)'I', (byte)'D', (byte)'3', 9 };
        var existing = new byte[] { (byte)'I', (byte)'D', (byte)'3', 1, 2, 3 };
        await using var audio = new MemoryStream(incoming);
        _music.Setup(service => service.IsMp3File("Song.mp3")).Returns(true);
        _storage.Setup(service => service.ExistsAsync("Song/Song.mp3")).ReturnsAsync(true);
        _storage.Setup(service => service.GetFileInfoAsync("Song/Song.mp3"))
            .ReturnsAsync(new StorageFileInfo
            {
                Name = "Song/Song.mp3",
                Length = existing.Length,
                ContentType = "audio/legacy"
            });
        _storage.Setup(service => service.OpenReadAsync("Song/Song.mp3"))
            .ReturnsAsync(() => new MemoryStream(existing));
        _metadata.Setup(service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var uploads = new List<(byte[] Bytes, string ContentType)>();
        _storage.Setup(service => service.UploadAsync(
                "Song/Song.mp3", It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns<string, Stream, string>(async (_, stream, contentType) =>
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                uploads.Add((copy.ToArray(), contentType));
            });

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UploadMusicWithoutAlbumArtAsync(audio, "Song.mp3", creatorId: 1));

        Assert.Multiple(() =>
        {
            Assert.That(uploads, Has.Count.EqualTo(2));
            Assert.That(uploads[0].Bytes, Is.EqualTo(incoming));
            Assert.That(uploads[0].ContentType, Is.EqualTo("audio/mpeg"));
            Assert.That(uploads[1].Bytes, Is.EqualTo(existing));
            Assert.That(uploads[1].ContentType, Is.EqualTo("audio/legacy"));
        });
        _storage.Verify(service => service.DeleteAsync("Song/Song.mp3"), Times.Never);
    }
}
