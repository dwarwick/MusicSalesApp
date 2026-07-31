using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
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
    private Mock<IImageVariantCoordinator> _imageVariants = null!;
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
        _imageVariants = new Mock<IImageVariantCoordinator>();
        _imageVariants.Setup(coordinator => coordinator.RefreshCoverArtVariantsAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _service = new MusicUploadService(
            _storage.Object,
            _music.Object,
            _metadata.Object,
            _openGraph.Object,
            _imageVariants.Object,
            Mock.Of<ILogger<MusicUploadService>>());
    }

    [Test]
    public void BlankTitle_PerformsNoWrites()
    {
        using var audio = new MemoryStream([1, 2, 3]);
        Assert.ThrowsAsync<InvalidDataException>(() =>
            _service.UploadMusicWithoutAlbumArtAsync(audio, "Night Drive.wav", "   ", creatorId: 1));
        _storage.Verify(service => service.UploadAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
        _metadata.Verify(service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()), Times.Never);
    }

    [Test]
    public void TitleOverMaxLength_PerformsNoWrites()
    {
        using var audio = new MemoryStream([1, 2, 3]);
        Assert.ThrowsAsync<InvalidDataException>(() =>
            _service.UploadMusicWithoutAlbumArtAsync(
                audio, "Night Drive.wav", new string('a', 201), creatorId: 1));
        _storage.Verify(service => service.UploadAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void UnsupportedExtension_PerformsNoWrites()
    {
        using var audio = new MemoryStream([1, 2, 3]);
        Assert.ThrowsAsync<InvalidDataException>(() =>
            _service.UploadMusicWithoutAlbumArtAsync(audio, "notes.txt", "Notes", creatorId: 1));
        _storage.Verify(service => service.UploadAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task UnconventionalFilename_IsAcceptedAndStoredUnderGuidPaths()
    {
        // The whole point of the change: a filename that the old character whitelist rejected
        // outright now uploads fine, because it never becomes a storage path.
        var bytes = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        await using var audio = new MemoryStream(bytes);
        const string fileName = "my song's @ mix v1.2 (remix)!.mp3";
        _music.Setup(service => service.IsMp3File(fileName)).Returns(true);

        var result = await _service.UploadMusicWithoutAlbumArtAsync(
            audio, fileName, "My Song's @ Mix v1.2 (Remix)!", creatorId: 1);

        Assert.Multiple(() =>
        {
            Assert.That(result.MediaGuid, Is.Not.EqualTo(Guid.Empty));
            Assert.That(result.Mp3BlobPath, Is.EqualTo(SongMediaPaths.Playback(result.MediaGuid)));
            Assert.That(result.Mp3BlobPath, Does.StartWith(SongMediaPaths.Folder(result.MediaGuid) + "/"));
        });
        _metadata.Verify(service => service.UpsertValidatedUploadAsync(
            It.Is<SongMetadata>(item => item.SongTitle == "My Song's @ Mix v1.2 (Remix)!"
                && item.OriginalAudioFileName == fileName
                && item.MediaGuid == result.MediaGuid)), Times.Once);
    }

    [Test]
    public async Task UploadWithAlbumArt_RebuildsTheImageRenditionsForTheSavedSong()
    {
        var bytes = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        await using var audio = new MemoryStream(bytes);
        await using var art = new MemoryStream(CreateSquarePng());
        _music.Setup(service => service.IsMp3File("Night Drive.mp3")).Returns(true);
        _metadata.Setup(service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()))
            .ReturnsAsync((SongMetadata item) => { item.Id = 4242; return item; });

        await _service.UploadMusicWithAlbumArtAsync(
            audio, "Night Drive.mp3", art, "cover.png", "Night Drive", creatorId: 1);

        _imageVariants.Verify(coordinator => coordinator.RefreshCoverArtVariantsAsync(
            4242, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task UploadWithoutAlbumArt_DoesNotAttemptToBuildRenditions()
    {
        var bytes = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        await using var audio = new MemoryStream(bytes);
        _music.Setup(service => service.IsMp3File("Night Drive.mp3")).Returns(true);

        await _service.UploadMusicWithoutAlbumArtAsync(audio, "Night Drive.mp3", "Night Drive", creatorId: 1);

        _imageVariants.Verify(coordinator => coordinator.RefreshCoverArtVariantsAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task WhenRenditionGenerationFails_TheUploadStillSucceeds()
    {
        // Renditions are derived data the admin backfill can rebuild. They are generated after the
        // rollback ledger is released precisely so an image-processing failure cannot undo blobs
        // that already committed.
        var bytes = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        await using var audio = new MemoryStream(bytes);
        await using var art = new MemoryStream(CreateSquarePng());
        _music.Setup(service => service.IsMp3File("Night Drive.mp3")).Returns(true);
        _imageVariants.Setup(coordinator => coordinator.RefreshCoverArtVariantsAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _service.UploadMusicWithAlbumArtAsync(
            audio, "Night Drive.mp3", art, "cover.png", "Night Drive", creatorId: 1);

        Assert.That(result.ImageBlobPath, Is.Not.Null);
        _storage.Verify(service => service.DeleteAsync(result.Mp3BlobPath), Times.Never);
    }

    private static byte[] CreateSquarePng()
    {
        using var bitmap = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Test]
    public async Task SuppliedTitle_IsStoredVerbatimAndIgnoresFilename()
    {
        var bytes = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        await using var audio = new MemoryStream(bytes);
        _music.Setup(service => service.IsMp3File("Night_Drive.mp3")).Returns(true);

        await _service.UploadMusicWithoutAlbumArtAsync(
            audio, "Night_Drive.mp3", "Something Else Entirely", creatorId: 1);

        _metadata.Verify(service => service.UpsertValidatedUploadAsync(
            It.Is<SongMetadata>(item => item.SongTitle == "Something Else Entirely"
                && item.OriginalAudioFileName == "Night_Drive.mp3")), Times.Once);
    }

    [Test]
    public async Task PreValidatedPlayback_IsUsedWithoutTranscodingOrDecodingAgain()
    {
        // The upload page transcodes and decodes the whole batch up front to prove it is
        // uploadable. Repeating either here doubled the FFmpeg work for every file.
        var original = new byte[] { 82, 73, 70, 70, 4, 0, 0, 0, 87, 65, 86, 69 };
        var converted = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        await using var audio = new MemoryStream(original);
        await using var playback = new MemoryStream(converted);
        _music.Setup(service => service.IsMp3File("Night Drive.wav")).Returns(false);

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
            audio,
            "Night Drive.wav",
            "Night Drive",
            creatorId: 1,
            validatedPlayback: playback,
            validatedDuration: 42);

        Assert.Multiple(() =>
        {
            Assert.That(uploaded[SongMediaPaths.Playback(result.MediaGuid)], Is.EqualTo(converted));
            Assert.That(uploaded[SongMediaPaths.OriginalAudio(result.MediaGuid, ".wav")], Is.EqualTo(original));
            Assert.That(result.TrackDuration, Is.EqualTo(42));
        });
        _music.Verify(service => service.ConvertToMp3Async(
            It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<IProgress<double>>()), Times.Never);
        _music.Verify(service => service.ValidateAudioDecodeAsync(
            It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void PreValidatedPlaybackThatIsNotAnMp3_IsRejectedBeforeAnyWrite()
    {
        // A truncated or wrong hand-off must not reach storage just because the caller said so.
        using var audio = new MemoryStream([82, 73, 70, 70, 4, 0, 0, 0, 87, 65, 86, 69]);
        using var playback = new MemoryStream([0, 1, 2, 3, 4, 5, 6, 7]);
        _music.Setup(service => service.IsMp3File("Night Drive.wav")).Returns(false);

        Assert.ThrowsAsync<InvalidDataException>(() => _service.UploadMusicWithoutAlbumArtAsync(
            audio,
            "Night Drive.wav",
            "Night Drive",
            creatorId: 1,
            validatedPlayback: playback,
            validatedDuration: 42));

        _storage.Verify(service => service.UploadAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task WithoutAPreValidatedPlayback_TheServiceStillConvertsAndDecodesItself()
    {
        var bytes = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        await using var audio = new MemoryStream(bytes);
        _music.Setup(service => service.IsMp3File("Boof.mp3")).Returns(true);

        var result = await _service.UploadMusicWithoutAlbumArtAsync(
            audio, "Boof.mp3", "Boof", creatorId: 1);

        Assert.That(result.TrackDuration, Is.EqualTo(12.5));
        _music.Verify(service => service.ValidateAudioDecodeAsync(
            It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Mp3Upload_UsesOneBlobForOriginalAndPlayback()
    {
        var bytes = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        await using var audio = new MemoryStream(bytes);
        _music.Setup(service => service.IsMp3File("Boof.mp3")).Returns(true);

        var result = await _service.UploadMusicWithoutAlbumArtAsync(
            audio, "Boof.mp3", "Boof", creatorId: 109);

        Assert.Multiple(() =>
        {
            Assert.That(result.Mp3BlobPath, Is.EqualTo(SongMediaPaths.Playback(result.MediaGuid)));
            Assert.That(result.OriginalAudioBlobPath, Is.EqualTo(result.Mp3BlobPath));
            Assert.That(result.OriginalAudioFileSize, Is.EqualTo(bytes.Length));
        });
        _storage.Verify(service => service.UploadAsync(
            result.Mp3BlobPath, It.IsAny<Stream>(), "audio/mpeg"), Times.Once);
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
            audio, "Night Drive.wav", "Night Drive", creatorId: 1);

        Assert.Multiple(() =>
        {
            Assert.That(uploaded[SongMediaPaths.OriginalAudio(result.MediaGuid, ".wav")], Is.EqualTo(original));
            Assert.That(uploaded[SongMediaPaths.Playback(result.MediaGuid)], Is.EqualTo(converted));
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
                It.IsAny<string>(), It.IsAny<Stream>(), "audio/mpeg"))
            .Returns<string, Stream, string>(async (_, uploadStream, _) =>
            {
                using var copy = new MemoryStream();
                await uploadStream.CopyToAsync(copy);
                uploaded = copy.ToArray();
            });

        var result = await _service.UploadMusicWithoutAlbumArtAsync(
            audio,
            "Closed Stream.mp3",
            "Closed Stream",
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

        // The GUID is minted inside the call and never returned when it throws, so capture the
        // paths that were actually written and assert each one is rolled back.
        var written = new List<string>();
        _storage.Setup(service => service.UploadAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns<string, Stream, string>((path, _, _) =>
            {
                written.Add(path);
                return Task.CompletedTask;
            });

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UploadMusicWithoutAlbumArtAsync(audio, "Song.mp3", "Song", creatorId: 1));

        Assert.That(written, Is.Not.Empty);
        foreach (var path in written.Distinct())
        {
            _storage.Verify(service => service.DeleteAsync(path), Times.Once);
        }
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
            _service.UploadMusicWithoutAlbumArtAsync(audio, "Song.wav", "Song", creatorId: 1));
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
                null, "Song.mp3", image, "Song.png", "Song", creatorId: 1));
            Assert.ThrowsAsync<InvalidDataException>(() => _service.UploadMusicWithAlbumArtAsync(
                audio, "", image, "Song.png", "Song", creatorId: 1));
            Assert.ThrowsAsync<InvalidDataException>(() => _service.UploadMusicWithAlbumArtAsync(
                audio, "Song.mp3", image, "", "Song", creatorId: 1));
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
                It.IsAny<string>(), It.IsAny<string>(), null, 1))
            .ReturnsAsync(new SongMetadata { OriginalAudioBlobPath = "Song/Song.wav" });
        _storage.Setup(service => service.DeleteAsync("Song/Song.wav"))
            .ThrowsAsync(new InvalidOperationException("blob under lease"));

        var result = await _service.UploadMusicWithoutAlbumArtAsync(
            audio, "Song.mp3", "Song", creatorId: 1);

        Assert.That(result.Mp3BlobPath, Is.EqualTo(SongMediaPaths.Playback(result.MediaGuid)));
        _metadata.Verify(service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()), Times.Once);
        _storage.Verify(service => service.DeleteAsync(result.Mp3BlobPath), Times.Never);
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
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), 1))
            .ReturnsAsync(new SongMetadata { ImageBlobPath = "Song/Song.jpg" });

        var result = await _service.UploadMusicWithAlbumArtAsync(
            audio, "Song.mp3", image, "Song.png", "Song", creatorId: 1);

        Assert.That(result.ImageBlobPath, Is.EqualTo(SongMediaPaths.CoverArt(result.MediaGuid, ".png")));
        _storage.Verify(service => service.DeleteAsync("Song/Song.jpg"), Times.Once);
    }

    [Test]
    public async Task UploadWithCoverArt_RetainsTheCreatorsOriginalAlongsideTheServedCopy()
    {
        await using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Blue);
        using var skImage = SKImage.FromBitmap(bitmap);
        using var pngData = skImage.Encode(SKEncodedImageFormat.Png, 100);
        var imageBytes = pngData.ToArray();
        await using var image = new MemoryStream(imageBytes);

        _music.Setup(service => service.IsMp3File("Song.mp3")).Returns(true);
        var uploaded = new Dictionary<string, byte[]>();
        _storage.Setup(service => service.UploadAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns<string, Stream, string>(async (path, stream, _) =>
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                uploaded[path] = copy.ToArray();
            });

        var result = await _service.UploadMusicWithAlbumArtAsync(
            audio, "Song.mp3", image, "my art v1.2!.png", "Song", creatorId: 1);

        var servedPath = SongMediaPaths.CoverArt(result.MediaGuid, ".png");
        var originalPath = SongMediaPaths.OriginalCoverArt(result.MediaGuid, ".png");
        Assert.Multiple(() =>
        {
            Assert.That(uploaded[servedPath], Is.EqualTo(imageBytes));
            Assert.That(uploaded[originalPath], Is.EqualTo(imageBytes));
            Assert.That(result.OriginalCoverArtBlobPath, Is.EqualTo(originalPath));
        });
        _metadata.Verify(service => service.UpsertValidatedUploadAsync(
            It.Is<SongMetadata>(item => item.OriginalCoverArtBlobPath == originalPath
                && item.OriginalCoverArtFileName == "my art v1.2!.png")), Times.Once);
    }

    [Test]
    public async Task UploadWithCoverArt_GeneratesTheSharingImageWithoutAWastedInvalidation()
    {
        await using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Red);
        using var skImage = SKImage.FromBitmap(bitmap);
        using var pngData = skImage.Encode(SKEncodedImageFormat.Png, 100);
        await using var image = new MemoryStream(pngData.ToArray());
        _music.Setup(service => service.IsMp3File("Song.mp3")).Returns(true);

        var result = await _service.UploadMusicWithAlbumArtAsync(
            audio, "Song.mp3", image, "Song.png", "Song", creatorId: 1);

        var coverArtPath = SongMediaPaths.CoverArt(result.MediaGuid, ".png");
        _openGraph.Verify(service => service.PreGenerateFacebookImageAsync(coverArtPath), Times.Once);
        // The GUID folder is brand new, so there is no stale sharing image to delete.
        _openGraph.Verify(
            service => service.InvalidateFacebookImageAsync(It.IsAny<string>()), Times.Never);
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
            _service.UploadMusicWithoutAlbumArtAsync(audio, "Song.mp3", "Song", creatorId: 1));

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
                It.IsAny<string>(), It.IsAny<string>(), null, 7))
            .ThrowsAsync(new UnauthorizedAccessException("belongs to another creator"));

        Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.UploadMusicWithoutAlbumArtAsync(audio, "Song.mp3", "Song", creatorId: 7));

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
        // The destination GUID is minted inside the call, so pretend every target already holds
        // a blob that has to be snapshotted and restored.
        _storage.Setup(service => service.ExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
        _storage.Setup(service => service.GetFileInfoAsync(It.IsAny<string>()))
            .ReturnsAsync((string path) => new StorageFileInfo
            {
                Name = path,
                Length = existing.Length,
                ContentType = "audio/legacy"
            });
        _storage.Setup(service => service.OpenReadAsync(It.IsAny<string>()))
            .ReturnsAsync(() => new MemoryStream(existing));
        _metadata.Setup(service => service.UpsertValidatedUploadAsync(It.IsAny<SongMetadata>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var uploads = new List<(string Path, byte[] Bytes, string ContentType)>();
        _storage.Setup(service => service.UploadAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns<string, Stream, string>(async (path, stream, contentType) =>
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                uploads.Add((path, copy.ToArray(), contentType));
            });

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UploadMusicWithoutAlbumArtAsync(audio, "Song.mp3", "Song", creatorId: 1));

        Assert.Multiple(() =>
        {
            Assert.That(uploads, Has.Count.EqualTo(2));
            Assert.That(uploads[0].Bytes, Is.EqualTo(incoming));
            Assert.That(uploads[0].ContentType, Is.EqualTo("audio/mpeg"));
            // The rollback restores the pre-existing bytes and content type to the same path.
            Assert.That(uploads[1].Path, Is.EqualTo(uploads[0].Path));
            Assert.That(uploads[1].Bytes, Is.EqualTo(existing));
            Assert.That(uploads[1].ContentType, Is.EqualTo("audio/legacy"));
        });
        _storage.Verify(service => service.DeleteAsync(It.IsAny<string>()), Times.Never);
    }
}
