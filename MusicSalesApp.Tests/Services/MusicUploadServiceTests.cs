using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Services;
using System.Collections.Generic;
using System.Text;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class MusicUploadServiceTests
{
    private Mock<IAzureStorageService> _mockStorageService;
    private Mock<IMusicService> _mockMusicService;
    private Mock<ISongMetadataService> _mockMetadataService;
    private Mock<ILogger<MusicUploadService>> _mockLogger;
    private MusicUploadService _service;

    [SetUp]
    public void Setup()
    {
        _mockStorageService = new Mock<IAzureStorageService>();
        _mockMusicService = new Mock<IMusicService>();
        _mockMetadataService = new Mock<ISongMetadataService>();
        _mockLogger = new Mock<ILogger<MusicUploadService>>();
        _service = new MusicUploadService(
            _mockStorageService.Object,
            _mockMusicService.Object,
            _mockMetadataService.Object,
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
        _mockStorageService.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<IDictionary<string, string>>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.UploadMusicWithAlbumArtAsync(
            audioStream, audioFileName, albumArtStream, albumArtFileName);

        // Assert
        Assert.That(result, Is.EqualTo("Lipstick and Leather"));
        _mockStorageService.Verify(s => s.UploadAsync(
            "Lipstick and Leather/Lipstick and Leather.mp3", 
            It.IsAny<Stream>(), 
            "audio/mpeg",
            It.IsAny<IDictionary<string, string>>()), Times.Once);
        _mockStorageService.Verify(s => s.UploadAsync(
            "Lipstick and Leather/Lipstick and Leather.jpeg", 
            It.IsAny<Stream>(), 
            "image/jpeg",
            It.IsAny<IDictionary<string, string>>()), Times.Once);
    }

    [Test]
    public async Task UploadMusicWithAlbumArtAsync_WithAlbumName_SetsIndexTags()
    {
        // Arrange
        var audioStream = new MemoryStream(Encoding.UTF8.GetBytes("audio content"));
        var albumArtStream = new MemoryStream(Encoding.UTF8.GetBytes("image content"));
        var audioFileName = "Song.mp3";
        var albumArtFileName = "Song.jpeg";
        var albumName = "My Test Album";

        _mockStorageService.Setup(s => s.EnsureContainerExistsAsync()).Returns(Task.CompletedTask);
        _mockMusicService.Setup(s => s.IsValidAudioFileAsync(It.IsAny<Stream>(), audioFileName))
            .ReturnsAsync(true);
        _mockMusicService.Setup(s => s.IsMp3File(audioFileName)).Returns(true);
        _mockStorageService.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<IDictionary<string, string>>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.UploadMusicWithAlbumArtAsync(
            audioStream, audioFileName, albumArtStream, albumArtFileName, albumName);

        // Assert
        Assert.That(result, Is.EqualTo("Song"));
        
        // Verify audio file upload has AlbumName index tag
        _mockStorageService.Verify(s => s.UploadAsync(
            "Song/Song.mp3", 
            It.IsAny<Stream>(), 
            "audio/mpeg",
            It.Is<IDictionary<string, string>>(m => 
                m != null && m.ContainsKey("AlbumName") && m["AlbumName"] == "My Test Album")), Times.Once);
        
        // Verify album art upload has AlbumName and IsAlbumCover=false index tags
        _mockStorageService.Verify(s => s.UploadAsync(
            "Song/Song.jpeg", 
            It.IsAny<Stream>(), 
            "image/jpeg",
            It.Is<IDictionary<string, string>>(m => 
                m != null && 
                m.ContainsKey("AlbumName") && m["AlbumName"] == "My Test Album" &&
                m.ContainsKey("IsAlbumCover") && m["IsAlbumCover"] == "false")), Times.Once);
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

    #region UploadAlbumCoverAsync Tests

    [Test]
    public async Task UploadAlbumCoverAsync_ValidAlbumCover_UploadsWithCorrectIndexTags()
    {
        // Arrange
        var albumArtStream = new MemoryStream(Encoding.UTF8.GetBytes("image content"));
        var albumArtFileName = "cover.jpeg";
        var albumName = "My Test Album";

        _mockStorageService.Setup(s => s.EnsureContainerExistsAsync()).Returns(Task.CompletedTask);
        _mockStorageService.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<IDictionary<string, string>>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.UploadAlbumCoverAsync(albumArtStream, albumArtFileName, albumName);

        // Assert
        Assert.That(result, Is.EqualTo("My Test Album/cover_cover.jpeg"));
        _mockStorageService.Verify(s => s.UploadAsync(
            "My Test Album/cover_cover.jpeg",
            It.IsAny<Stream>(),
            "image/jpeg",
            It.Is<IDictionary<string, string>>(m =>
                m != null &&
                m.ContainsKey("AlbumName") && m["AlbumName"] == "My Test Album" &&
                m.ContainsKey("IsAlbumCover") && m["IsAlbumCover"] == "true")), Times.Once);
    }

    [Test]
    public void UploadAlbumCoverAsync_NullStream_ThrowsException()
    {
        // Arrange
        Stream albumArtStream = null;
        var albumArtFileName = "cover.jpeg";
        var albumName = "My Test Album";

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _service.UploadAlbumCoverAsync(albumArtStream, albumArtFileName, albumName));
    }

    [Test]
    public void UploadAlbumCoverAsync_EmptyFileName_ThrowsException()
    {
        // Arrange
        var albumArtStream = new MemoryStream(Encoding.UTF8.GetBytes("image content"));
        var albumArtFileName = string.Empty;
        var albumName = "My Test Album";

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.UploadAlbumCoverAsync(albumArtStream, albumArtFileName, albumName));
    }

    [Test]
    public void UploadAlbumCoverAsync_EmptyAlbumName_ThrowsException()
    {
        // Arrange
        var albumArtStream = new MemoryStream(Encoding.UTF8.GetBytes("image content"));
        var albumArtFileName = "cover.jpeg";
        var albumName = string.Empty;

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.UploadAlbumCoverAsync(albumArtStream, albumArtFileName, albumName));
    }

    [Test]
    public void UploadAlbumCoverAsync_InvalidFileExtension_ThrowsException()
    {
        // Arrange
        var albumArtStream = new MemoryStream(Encoding.UTF8.GetBytes("image content"));
        var albumArtFileName = "cover.png";
        var albumName = "My Test Album";

        // Act & Assert
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await _service.UploadAlbumCoverAsync(albumArtStream, albumArtFileName, albumName));
    }

    #endregion

    #region ValidateAllFilePairings RequireAudioFile Tests

    [Test]
    public void ValidateAllFilePairings_RequireAudioFileFalse_OnlyAlbumArtValid()
    {
        // Arrange
        var fileNames = new List<string>
        {
            "cover.jpeg"
        };

        // Act
        var result = _service.ValidateAllFilePairings(fileNames, requireAudioFile: false);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.UnmatchedMp3Files, Is.Empty);
        Assert.That(result.UnmatchedAlbumArtFiles, Is.Empty);
    }

    [Test]
    public void ValidateAllFilePairings_RequireAudioFileFalse_NoFilesInvalid()
    {
        // Arrange
        var fileNames = new List<string>();

        // Act
        var result = _service.ValidateAllFilePairings(fileNames, requireAudioFile: false);

        // Assert
        Assert.That(result.IsValid, Is.False);
    }

    #endregion
}
