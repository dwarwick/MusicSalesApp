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
    private MusicController _controller;

    [SetUp]
    public void Setup()
    {
        _mockStorageService = new Mock<IAzureStorageService>();        
        _controller = new MusicController(_mockStorageService.Object);
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
