using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Controllers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Text;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class MusicControllerTests
{
    private Mock<IAzureStorageService> _mockStorageService;
    private Mock<ICartService> _mockCartService;
    private Mock<IStreamCountService> _mockStreamCountService;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private MusicController _controller;

    [SetUp]
    public void Setup()
    {
        _mockStorageService = new Mock<IAzureStorageService>();
        _mockCartService = new Mock<ICartService>();
        _mockStreamCountService = new Mock<IStreamCountService>();
        
        // Mock UserManager with required dependencies
        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object, null, null, null, null, null, null, null, null);
        
        _controller = new MusicController(
            _mockStorageService.Object,
            _mockCartService.Object,
            _mockStreamCountService.Object,
            _mockUserManager.Object);

        // Set up HttpContext for controller (required for Response.Headers access)
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
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

    [Test]
    public async Task GetStreamUrl_WithValidFile_ReturnsOkWithSasUrl()
    {
        // Arrange
        var fileName = "test.mp3";
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/test.mp3?sv=2021-06-08&st=2024-01-01T00%3A00%3A00Z&se=2024-01-02T00%3A00%3A00Z&sr=b&sp=r&sig=signature");
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync((ApplicationUser)null);
        _mockStorageService.Setup(s => s.GetReadSasUri(fileName, It.IsAny<TimeSpan>()))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetStreamUrl(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = result as OkObjectResult;
        var value = okResult.Value;
        var urlProperty = value.GetType().GetProperty("url");
        var url = urlProperty.GetValue(value) as string;
        Assert.That(url, Is.EqualTo(sasUri.ToString()));
    }

    [Test]
    public async Task GetStreamUrl_WithEmptyFileName_ReturnsBadRequest()
    {
        // Arrange
        var fileName = string.Empty;

        // Act
        var result = await _controller.GetStreamUrl(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    [Test]
    public async Task GetStreamUrl_ForOwner_UsesLongerLifetime()
    {
        // Arrange
        var fileName = "test.mp3";
        var userId = 123;
        var user = new ApplicationUser { Id = userId, UserName = "testuser" };
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/test.mp3?sv=2021-06-08&st=2024-01-01T00%3A00%3A00Z&se=2024-01-02T00%3A00%3A00Z&sr=b&sp=r&sig=signature");
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(user);
        _mockCartService.Setup(s => s.UserOwnsSongAsync(userId, fileName))
            .ReturnsAsync(true);
        _mockStorageService.Setup(s => s.GetReadSasUri(fileName, TimeSpan.FromHours(24)))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetStreamUrl(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockStorageService.Verify(s => s.GetReadSasUri(fileName, TimeSpan.FromHours(24)), Times.Once);
    }

    [Test]
    public async Task GetStreamUrl_ForNonOwner_UsesShorterLifetime()
    {
        // Arrange
        var fileName = "test.mp3";
        var userId = 123;
        var user = new ApplicationUser { Id = userId, UserName = "testuser" };
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/test.mp3?sv=2021-06-08&st=2024-01-01T00%3A00%3A00Z&se=2024-01-02T00%3A00%3A00Z&sr=b&sp=r&sig=signature");
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(user);
        _mockCartService.Setup(s => s.UserOwnsSongAsync(userId, fileName))
            .ReturnsAsync(false);
        _mockStorageService.Setup(s => s.GetReadSasUri(fileName, TimeSpan.FromHours(2)))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetStreamUrl(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockStorageService.Verify(s => s.GetReadSasUri(fileName, TimeSpan.FromHours(2)), Times.Once);
    }
}
