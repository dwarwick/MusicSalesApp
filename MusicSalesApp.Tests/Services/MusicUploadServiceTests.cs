using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Services;
using System.Text;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class MusicUploadServiceTests
{
    private Mock<IAzureStorageService> _mockStorageService;
    private Mock<IMusicService> _mockMusicService;
    private Mock<ILogger<MusicUploadService>> _mockLogger;
    private MusicUploadService _service;

    [SetUp]
    public void Setup()
    {
        _mockStorageService = new Mock<IAzureStorageService>();
        _mockMusicService = new Mock<IMusicService>();
        _mockLogger = new Mock<ILogger<MusicUploadService>>();
        _service = new MusicUploadService(
            _mockStorageService.Object,
            _mockMusicService.Object,
            _mockLogger.Object);
    }

    #region GetNormalizedBaseName Tests

    [Test]
    public void GetNormalizedBaseName_WithMp3File_ReturnsBaseName()
    {
        // Arrange
        var fileName = "Lipstick and Leather.mp3";

        // Act
        var result = _service.GetNormalizedBaseName(fileName);

        // Assert
        Assert.That(result, Is.EqualTo("Lipstick and Leather"));
    }

    [Test]
    public void GetNormalizedBaseName_WithMasteredSuffix_RemovesSuffix()
    {
        // Arrange
        var fileName = "Lipstick and Leather_mastered.mp3";

        // Act
        var result = _service.GetNormalizedBaseName(fileName);

        // Assert
        Assert.That(result, Is.EqualTo("Lipstick and Leather"));
    }

    [Test]
    public void GetNormalizedBaseName_WithJpegFile_ReturnsBaseName()
    {
        // Arrange
        var fileName = "Lipstick and Leather.jpeg";

        // Act
        var result = _service.GetNormalizedBaseName(fileName);

        // Assert
        Assert.That(result, Is.EqualTo("Lipstick and Leather"));
    }

    [Test]
    public void GetNormalizedBaseName_CaseInsensitiveMastered_RemovesSuffix()
    {
        // Arrange
        var fileName = "Song Title_MASTERED.mp3";

        // Act
        var result = _service.GetNormalizedBaseName(fileName);

        // Assert
        Assert.That(result, Is.EqualTo("Song Title"));
    }

    [Test]
    public void GetNormalizedBaseName_WithEmptyString_ReturnsEmpty()
    {
        // Arrange
        var fileName = string.Empty;

        // Act
        var result = _service.GetNormalizedBaseName(fileName);

        // Assert
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void GetNormalizedBaseName_WithNull_ReturnsEmpty()
    {
        // Arrange
        string fileName = null;

        // Act
        var result = _service.GetNormalizedBaseName(fileName);

        // Assert
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    #endregion

    #region ValidateFilePairing Tests

    [Test]
    public void ValidateFilePairing_MatchingFiles_ReturnsTrue()
    {
        // Arrange
        var audioFileName = "Lipstick and Leather_mastered.mp3";
        var albumArtFileName = "Lipstick and Leather.jpeg";

        // Act
        var result = _service.ValidateFilePairing(audioFileName, albumArtFileName);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void ValidateFilePairing_NonMatchingFiles_ReturnsFalse()
    {
        // Arrange
        var audioFileName = "Lipstick and Leather_mastered.mp3";
        var albumArtFileName = "LipstickandLeather.jpeg";

        // Act
        var result = _service.ValidateFilePairing(audioFileName, albumArtFileName);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void ValidateFilePairing_BothWithoutMastered_ReturnsTrue()
    {
        // Arrange
        var audioFileName = "My Song.mp3";
        var albumArtFileName = "My Song.jpeg";

        // Act
        var result = _service.ValidateFilePairing(audioFileName, albumArtFileName);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void ValidateFilePairing_CaseInsensitive_ReturnsTrue()
    {
        // Arrange
        var audioFileName = "LIPSTICK AND LEATHER_mastered.mp3";
        var albumArtFileName = "lipstick and leather.jpeg";

        // Act
        var result = _service.ValidateFilePairing(audioFileName, albumArtFileName);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void ValidateFilePairing_EmptyAudioFileName_ReturnsFalse()
    {
        // Arrange
        var audioFileName = string.Empty;
        var albumArtFileName = "Test.jpeg";

        // Act
        var result = _service.ValidateFilePairing(audioFileName, albumArtFileName);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void ValidateFilePairing_EmptyAlbumArtFileName_ReturnsFalse()
    {
        // Arrange
        var audioFileName = "Test.mp3";
        var albumArtFileName = string.Empty;

        // Act
        var result = _service.ValidateFilePairing(audioFileName, albumArtFileName);

        // Assert
        Assert.That(result, Is.False);
    }

    #endregion

    #region ValidateAllFilePairings Tests

    [Test]
    public void ValidateAllFilePairings_AllMatched_ReturnsValid()
    {
        // Arrange
        var fileNames = new List<string>
        {
            "Song One_mastered.mp3",
            "Song One.jpeg",
            "Song Two.mp3",
            "Song Two.jpg"
        };

        // Act
        var result = _service.ValidateAllFilePairings(fileNames);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.UnmatchedMp3Files, Is.Empty);
        Assert.That(result.UnmatchedAlbumArtFiles, Is.Empty);
    }

    [Test]
    public void ValidateAllFilePairings_WavFilesMatched_ReturnsValid()
    {
        // Arrange
        var fileNames = new List<string>
        {
            "Song One_mastered.wav",
            "Song One.jpeg",
            "Song Two.wav",
            "Song Two.jpg"
        };

        // Act
        var result = _service.ValidateAllFilePairings(fileNames);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.UnmatchedMp3Files, Is.Empty);
        Assert.That(result.UnmatchedAlbumArtFiles, Is.Empty);
    }

    [Test]
    public void ValidateAllFilePairings_MixedAudioFormats_ReturnsValid()
    {
        // Arrange
        var fileNames = new List<string>
        {
            "Song One_mastered.mp3",
            "Song One.jpeg",
            "Song Two.wav",
            "Song Two.jpg",
            "Song Three.flac",
            "Song Three.jpeg"
        };

        // Act
        var result = _service.ValidateAllFilePairings(fileNames);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.UnmatchedMp3Files, Is.Empty);
        Assert.That(result.UnmatchedAlbumArtFiles, Is.Empty);
    }

    [Test]
    public void ValidateAllFilePairings_UnmatchedMp3_ReturnsInvalid()
    {
        // Arrange
        var fileNames = new List<string>
        {
            "Song One_mastered.mp3",
            "Song One.jpeg",
            "Orphan Song.mp3"
        };

        // Act
        var result = _service.ValidateAllFilePairings(fileNames);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.UnmatchedMp3Files, Contains.Item("Orphan Song.mp3"));
        Assert.That(result.UnmatchedAlbumArtFiles, Is.Empty);
    }

    [Test]
    public void ValidateAllFilePairings_UnmatchedAlbumArt_ReturnsInvalid()
    {
        // Arrange
        var fileNames = new List<string>
        {
            "Song One_mastered.mp3",
            "Song One.jpeg",
            "Orphan Art.jpeg"
        };

        // Act
        var result = _service.ValidateAllFilePairings(fileNames);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.UnmatchedMp3Files, Is.Empty);
        Assert.That(result.UnmatchedAlbumArtFiles, Contains.Item("Orphan Art.jpeg"));
    }

    [Test]
    public void ValidateAllFilePairings_OnlyMp3Files_ReturnsInvalid()
    {
        // Arrange
        var fileNames = new List<string>
        {
            "Song One.mp3",
            "Song Two.mp3"
        };

        // Act
        var result = _service.ValidateAllFilePairings(fileNames);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.UnmatchedMp3Files.Count, Is.EqualTo(2));
        Assert.That(result.UnmatchedAlbumArtFiles, Is.Empty);
    }

    [Test]
    public void ValidateAllFilePairings_OnlyAlbumArtFiles_ReturnsInvalid()
    {
        // Arrange
        var fileNames = new List<string>
        {
            "Song One.jpeg",
            "Song Two.jpg"
        };

        // Act
        var result = _service.ValidateAllFilePairings(fileNames);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.UnmatchedMp3Files, Is.Empty);
        Assert.That(result.UnmatchedAlbumArtFiles.Count, Is.EqualTo(2));
    }

    [Test]
    public void ValidateAllFilePairings_EmptyList_ReturnsInvalid()
    {
        // Arrange
        var fileNames = new List<string>();

        // Act
        var result = _service.ValidateAllFilePairings(fileNames);

        // Assert
        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void ValidateAllFilePairings_NullList_ReturnsInvalid()
    {
        // Arrange
        IEnumerable<string> fileNames = null;

        // Act
        var result = _service.ValidateAllFilePairings(fileNames);

        // Assert
        Assert.That(result.IsValid, Is.False);
    }

    #endregion

    #region UploadMusicWithAlbumArtAsync Tests

    [Test]
    public async Task UploadMusicWithAlbumArtAsync_ValidPair_UploadsToCorrectFolder()
    {
        // Arrange
        var audioStream = new MemoryStream(Encoding.UTF8.GetBytes("audio content"));
        var albumArtStream = new MemoryStream(Encoding.UTF8.GetBytes("image content"));
        var audioFileName = "Lipstick and Leather_mastered.mp3";
        var albumArtFileName = "Lipstick and Leather.jpeg";

        _mockStorageService.Setup(s => s.EnsureContainerExistsAsync()).Returns(Task.CompletedTask);
        _mockMusicService.Setup(s => s.IsValidAudioFileAsync(It.IsAny<Stream>(), audioFileName))
            .ReturnsAsync(true);
        _mockMusicService.Setup(s => s.IsMp3File(audioFileName)).Returns(true);
        _mockStorageService.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.UploadMusicWithAlbumArtAsync(
            audioStream, audioFileName, albumArtStream, albumArtFileName);

        // Assert
        Assert.That(result, Is.EqualTo("Lipstick and Leather"));
        _mockStorageService.Verify(s => s.UploadAsync(
            "Lipstick and Leather/Lipstick and Leather.mp3", 
            It.IsAny<Stream>(), 
            "audio/mpeg"), Times.Once);
        _mockStorageService.Verify(s => s.UploadAsync(
            "Lipstick and Leather/Lipstick and Leather.jpeg", 
            It.IsAny<Stream>(), 
            "image/jpeg"), Times.Once);
    }

    [Test]
    public void UploadMusicWithAlbumArtAsync_NonMatchingFiles_ThrowsException()
    {
        // Arrange
        var audioStream = new MemoryStream(Encoding.UTF8.GetBytes("audio content"));
        var albumArtStream = new MemoryStream(Encoding.UTF8.GetBytes("image content"));
        var audioFileName = "Lipstick and Leather_mastered.mp3";
        var albumArtFileName = "DifferentName.jpeg";

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.UploadMusicWithAlbumArtAsync(
                audioStream, audioFileName, albumArtStream, albumArtFileName));
    }

    [Test]
    public void UploadMusicWithAlbumArtAsync_NullAudioStream_ThrowsException()
    {
        // Arrange
        Stream audioStream = null;
        var albumArtStream = new MemoryStream(Encoding.UTF8.GetBytes("image content"));
        var audioFileName = "Test.mp3";
        var albumArtFileName = "Test.jpeg";

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _service.UploadMusicWithAlbumArtAsync(
                audioStream, audioFileName, albumArtStream, albumArtFileName));
    }

    [Test]
    public void UploadMusicWithAlbumArtAsync_NullAlbumArtStream_ThrowsException()
    {
        // Arrange
        var audioStream = new MemoryStream(Encoding.UTF8.GetBytes("audio content"));
        Stream albumArtStream = null;
        var audioFileName = "Test.mp3";
        var albumArtFileName = "Test.jpeg";

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _service.UploadMusicWithAlbumArtAsync(
                audioStream, audioFileName, albumArtStream, albumArtFileName));
    }

    [Test]
    public void UploadMusicWithAlbumArtAsync_EmptyAudioFileName_ThrowsException()
    {
        // Arrange
        var audioStream = new MemoryStream(Encoding.UTF8.GetBytes("audio content"));
        var albumArtStream = new MemoryStream(Encoding.UTF8.GetBytes("image content"));
        var audioFileName = string.Empty;
        var albumArtFileName = "Test.jpeg";

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.UploadMusicWithAlbumArtAsync(
                audioStream, audioFileName, albumArtStream, albumArtFileName));
    }

    [Test]
    public void UploadMusicWithAlbumArtAsync_EmptyAlbumArtFileName_ThrowsException()
    {
        // Arrange
        var audioStream = new MemoryStream(Encoding.UTF8.GetBytes("audio content"));
        var albumArtStream = new MemoryStream(Encoding.UTF8.GetBytes("image content"));
        var audioFileName = "Test.mp3";
        var albumArtFileName = string.Empty;

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.UploadMusicWithAlbumArtAsync(
                audioStream, audioFileName, albumArtStream, albumArtFileName));
    }

    #endregion
}
