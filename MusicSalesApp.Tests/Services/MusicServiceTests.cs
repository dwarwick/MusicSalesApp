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

    [TestCase("my song's @ mix v1.2 (remix)!.mp3")]
    [TestCase("song__name.mp3")]
    [TestCase("_leading underscore.mp3")]
    [TestCase("Été – Hiver.mp3")]
    public async Task IsValidAudioFileAsync_UnconventionalFileNameWithValidContent_ReturnsTrue(string fileName)
    {
        // Validation is about the bytes and the extension now, not the characters in the name.
        var stream = new MemoryStream([(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0]);

        var result = await _service.IsValidAudioFileAsync(stream, fileName);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task IsValidAudioFileAsync_WithValidMp3Extension_ReturnsTrue()
    {
        // Arrange
        var stream = new MemoryStream([(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0]);
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
        var stream = new MemoryStream([(byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E']);
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
        var stream = new MemoryStream([(byte)'f', (byte)'L', (byte)'a', (byte)'C']);
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
    public async Task IsValidAudioFileAsync_WithMismatchedContainer_ReturnsFalse()
    {
        var wav = new MemoryStream([(byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E']);

        var result = await _service.IsValidAudioFileAsync(wav, "Song.mp3");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsValidAudioFileAsync_WithCorruptContent_ReturnsFalse()
    {
        var corrupt = new MemoryStream(Encoding.UTF8.GetBytes("not audio"));

        var result = await _service.IsValidAudioFileAsync(corrupt, "Song.flac");

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
}
