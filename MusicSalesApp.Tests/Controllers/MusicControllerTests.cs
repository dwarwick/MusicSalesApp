using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Controllers;
using MusicSalesApp.Services;
using System.Text;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class MusicControllerTests
{
    private Mock<IAzureStorageService> _mockStorageService;
    private Mock<IMusicService> _mockMusicService;
    private Mock<ILogger<MusicController>> _mockLogger;
    private MusicController _controller;

    [SetUp]
    public void Setup()
    {
        _mockStorageService = new Mock<IAzureStorageService>();
        _mockMusicService = new Mock<IMusicService>();
        _mockLogger = new Mock<ILogger<MusicController>>();
        _controller = new MusicController(
            _mockStorageService.Object,
            _mockMusicService.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task Upload_WithNullFile_ReturnsBadRequest()
    {
        // Arrange
        IFormFile file = null;
        var destinationFolder = "music";

        // Act
        var result = await _controller.Upload(file, destinationFolder);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Upload_WithEmptyFile_ReturnsBadRequest()
    {
        // Arrange
        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.Length).Returns(0);
        mockFile.Setup(f => f.FileName).Returns("test.mp3");
        var destinationFolder = "music";

        // Act
        var result = await _controller.Upload(mockFile.Object, destinationFolder);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Upload_WithInvalidAudioFile_ReturnsBadRequest()
    {
        // Arrange
        var content = "test content";
        var fileName = "test.mp3";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.Length).Returns(stream.Length);
        mockFile.Setup(f => f.FileName).Returns(fileName);
        mockFile.Setup(f => f.OpenReadStream()).Returns(stream);

        _mockMusicService.Setup(s => s.IsValidAudioFileAsync(It.IsAny<Stream>(), fileName))
            .ReturnsAsync(false);

        var destinationFolder = "music";

        // Act
        var result = await _controller.Upload(mockFile.Object, destinationFolder);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockStorageService.Verify(s => s.EnsureContainerExistsAsync(), Times.Once);
    }

    [Test]
    public async Task Upload_WithValidMp3File_UploadsSuccessfully()
    {
        // Arrange
        var content = "test mp3 content";
        var fileName = "test.mp3";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.Length).Returns(stream.Length);
        mockFile.Setup(f => f.FileName).Returns(fileName);
        mockFile.Setup(f => f.OpenReadStream()).Returns(stream);

        _mockMusicService.Setup(s => s.IsValidAudioFileAsync(It.IsAny<Stream>(), fileName))
            .ReturnsAsync(true);
        _mockMusicService.Setup(s => s.IsMp3File(fileName))
            .Returns(true);
        _mockStorageService.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockStorageService.Setup(s => s.EnsureContainerExistsAsync())
            .Returns(Task.CompletedTask);

        var destinationFolder = "music";

        // Act
        var result = await _controller.Upload(mockFile.Object, destinationFolder);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockStorageService.Verify(s => s.UploadAsync("music/test.mp3", It.IsAny<Stream>(), "audio/mpeg"), Times.Once);
    }

    [Test]
    public async Task Upload_WithEmptyDestinationFolder_UploadsToRoot()
    {
        // Arrange
        var content = "test mp3 content";
        var fileName = "test.mp3";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.Length).Returns(stream.Length);
        mockFile.Setup(f => f.FileName).Returns(fileName);
        mockFile.Setup(f => f.OpenReadStream()).Returns(stream);

        _mockMusicService.Setup(s => s.IsValidAudioFileAsync(It.IsAny<Stream>(), fileName))
            .ReturnsAsync(true);
        _mockMusicService.Setup(s => s.IsMp3File(fileName))
            .Returns(true);
        _mockStorageService.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockStorageService.Setup(s => s.EnsureContainerExistsAsync())
            .Returns(Task.CompletedTask);

        var destinationFolder = string.Empty;

        // Act
        var result = await _controller.Upload(mockFile.Object, destinationFolder);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockStorageService.Verify(s => s.UploadAsync("test.mp3", It.IsAny<Stream>(), "audio/mpeg"), Times.Once);
    }

    [Test]
    public async Task Upload_WithNonMp3File_ConvertsAndUploads()
    {
        // Arrange
        var content = "test wav content";
        var fileName = "test.wav";
        var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var convertedStream = new MemoryStream(Encoding.UTF8.GetBytes("converted mp3"));

        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.Length).Returns(inputStream.Length);
        mockFile.Setup(f => f.FileName).Returns(fileName);
        mockFile.Setup(f => f.OpenReadStream()).Returns(inputStream);

        _mockMusicService.Setup(s => s.IsValidAudioFileAsync(It.IsAny<Stream>(), fileName))
            .ReturnsAsync(true);
        _mockMusicService.Setup(s => s.IsMp3File(fileName))
            .Returns(false);
        _mockMusicService.Setup(s => s.ConvertToMp3Async(It.IsAny<Stream>(), fileName, null))
            .ReturnsAsync(convertedStream);
        _mockStorageService.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockStorageService.Setup(s => s.EnsureContainerExistsAsync())
            .Returns(Task.CompletedTask);

        var destinationFolder = "music";

        // Act
        var result = await _controller.Upload(mockFile.Object, destinationFolder);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockMusicService.Verify(s => s.ConvertToMp3Async(It.IsAny<Stream>(), fileName, null), Times.Once);
        _mockStorageService.Verify(s => s.UploadAsync("music/test.mp3", It.IsAny<Stream>(), "audio/mpeg"), Times.Once);
    }

    [Test]
    public async Task List_ReturnsOkWithFiles()
    {
        // Arrange
        var files = new List<StorageFileInfo>
        {
            new StorageFileInfo { Name = "test1.mp3", Length = 1000 },
            new StorageFileInfo { Name = "test2.mp3", Length = 2000 }
        };
        _mockStorageService.Setup(s => s.ListFilesAsync()).ReturnsAsync(files);

        // Act
        var result = await _controller.List();

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = result as OkObjectResult;
        Assert.That(okResult.Value, Is.EqualTo(files));
    }

    [Test]
    public async Task Stream_WithValidFile_ReturnsFileResult()
    {
        // Arrange
        var fileName = "test.mp3";
        var fileInfo = new StorageFileInfo
        {
            Name = fileName,
            Length = 1000,
            ContentType = "audio/mpeg"
        };
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("test content"));

        _mockStorageService.Setup(s => s.GetFileInfoAsync(fileName)).ReturnsAsync(fileInfo);
        _mockStorageService.Setup(s => s.OpenReadAsync(fileName)).ReturnsAsync(stream);

        // Act
        var result = await _controller.Stream(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<FileStreamResult>());
    }

    [Test]
    public async Task Stream_WithEmptyFileName_ReturnsBadRequest()
    {
        // Arrange
        var fileName = string.Empty;

        // Act
        var result = await _controller.Stream(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    [Test]
    public async Task Stream_WithNonExistentFile_ReturnsNotFound()
    {
        // Arrange
        var fileName = "nonexistent.mp3";
        _mockStorageService.Setup(s => s.GetFileInfoAsync(fileName)).ReturnsAsync((StorageFileInfo)null);

        // Act
        var result = await _controller.Stream(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }
}
