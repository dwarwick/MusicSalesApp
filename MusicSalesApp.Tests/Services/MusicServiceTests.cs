using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Services;
using System.Text;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class MusicServiceTests
{
    private Mock<ILogger<MusicService>> _mockLogger;
    private MusicService _service;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<MusicService>>();
        _service = new MusicService(_mockLogger.Object);
    }

    [Test]
    public async Task IsValidAudioFileAsync_WithValidMp3Extension_ReturnsTrue()
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));
        var fileName = "test.mp3";

        // Act
        var result = await _service.IsValidAudioFileAsync(stream, fileName);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task IsValidAudioFileAsync_WithValidWavExtension_ReturnsTrue()
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));
        var fileName = "test.wav";

        // Act
        var result = await _service.IsValidAudioFileAsync(stream, fileName);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task IsValidAudioFileAsync_WithValidFlacExtension_ReturnsTrue()
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));
        var fileName = "test.flac";

        // Act
        var result = await _service.IsValidAudioFileAsync(stream, fileName);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task IsValidAudioFileAsync_WithInvalidExtension_ReturnsFalse()
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));
        var fileName = "test.txt";

        // Act
        var result = await _service.IsValidAudioFileAsync(stream, fileName);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsValidAudioFileAsync_WithNullStream_ReturnsFalse()
    {
        // Arrange
        Stream stream = null;
        var fileName = "test.mp3";

        // Act
        var result = await _service.IsValidAudioFileAsync(stream, fileName);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsValidAudioFileAsync_WithEmptyFileName_ReturnsFalse()
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));
        var fileName = string.Empty;

        // Act
        var result = await _service.IsValidAudioFileAsync(stream, fileName);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsValidAudioFileAsync_WithNullFileName_ReturnsFalse()
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));
        string fileName = null;

        // Act
        var result = await _service.IsValidAudioFileAsync(stream, fileName);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsMp3File_WithMp3Extension_ReturnsTrue()
    {
        // Arrange
        var fileName = "test.mp3";

        // Act
        var result = _service.IsMp3File(fileName);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsMp3File_WithUpperCaseMp3Extension_ReturnsTrue()
    {
        // Arrange
        var fileName = "test.MP3";

        // Act
        var result = _service.IsMp3File(fileName);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsMp3File_WithWavExtension_ReturnsFalse()
    {
        // Arrange
        var fileName = "test.wav";

        // Act
        var result = _service.IsMp3File(fileName);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsMp3File_WithEmptyFileName_ReturnsFalse()
    {
        // Arrange
        var fileName = string.Empty;

        // Act
        var result = _service.IsMp3File(fileName);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsMp3File_WithNullFileName_ReturnsFalse()
    {
        // Arrange
        string fileName = null;

        // Act
        var result = _service.IsMp3File(fileName);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void ConvertToMp3Async_WithNullStream_ThrowsArgumentNullException()
    {
        // Arrange
        Stream stream = null;
        var fileName = "test.wav";

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () => 
            await _service.ConvertToMp3Async(stream, fileName));
    }

    [Test]
    public void ConvertToMp3Async_WithNullFileName_ThrowsArgumentNullException()
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));
        string fileName = null;

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () => 
            await _service.ConvertToMp3Async(stream, fileName));
    }

    [Test]
    public void ConvertToMp3Async_WithEmptyFileName_ThrowsArgumentNullException()
    {
        // Arrange
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test"));
        var fileName = string.Empty;

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () => 
            await _service.ConvertToMp3Async(stream, fileName));
    }
}
