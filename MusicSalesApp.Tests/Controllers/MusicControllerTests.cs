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
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<IStreamCountService> _mockStreamCountService;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<ILogger<MusicController>> _mockLogger;
    private MusicController _controller;

    [SetUp]
    public void Setup()
    {
        _mockStorageService = new Mock<IAzureStorageService>();
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockStreamCountService = new Mock<IStreamCountService>();
        _mockLogger = new Mock<ILogger<MusicController>>();
        
        // Mock UserManager with required dependencies
        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object, null, null, null, null, null, null, null, null);
        
        _controller = new MusicController(
            _mockStorageService.Object,
            _mockSubscriptionService.Object,
            _mockStreamCountService.Object,
            _mockUserManager.Object,
            _mockLogger.Object);

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
        _mockStorageService.Setup(s => s.OpenReadAsync(fileName)).ReturnsAsync((Stream)null);

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
    public async Task GetStreamUrl_ForSubscriber_UsesLongerLifetime()
    {
        // Arrange
        var fileName = "test.mp3";
        var userId = 123;
        var user = new ApplicationUser { Id = userId, UserName = "testuser" };
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/test.mp3?sv=2021-06-08&st=2024-01-01T00%3A00%3A00Z&se=2024-01-02T00%3A00%3A00Z&sr=b&sp=r&sig=signature");
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(user);
        _mockSubscriptionService.Setup(s => s.HasActiveSubscriptionAsync(userId))
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
    public async Task GetStreamUrl_ForNonSubscriber_UsesShorterLifetime()
    {
        // Arrange
        var fileName = "test.mp3";
        var userId = 123;
        var user = new ApplicationUser { Id = userId, UserName = "testuser" };
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/test.mp3?sv=2021-06-08&st=2024-01-01T00%3A00%3A00Z&se=2024-01-02T00%3A00%3A00Z&sr=b&sp=r&sig=signature");
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(user);
        _mockSubscriptionService.Setup(s => s.HasActiveSubscriptionAsync(userId))
            .ReturnsAsync(false);
        _mockStorageService.Setup(s => s.GetReadSasUri(fileName, TimeSpan.FromHours(2)))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetStreamUrl(fileName);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockStorageService.Verify(s => s.GetReadSasUri(fileName, TimeSpan.FromHours(2)), Times.Once);
    }

    [Test]
    public async Task RecordStream_WithValidId_ReturnsOkWithStreamCount()
    {
        // Arrange
        var songMetadataId = 1;
        var newCount = 42;
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync((ApplicationUser)null);
        _mockStreamCountService.Setup(s => s.IncrementStreamCountAsync(songMetadataId, null, false))
            .ReturnsAsync(newCount);

        // Act
        var result = await _controller.RecordStream(songMetadataId);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = result as OkObjectResult;
        var value = okResult.Value;
        var streamCountProperty = value.GetType().GetProperty("streamCount");
        var streamCount = (int)streamCountProperty.GetValue(value);
        Assert.That(streamCount, Is.EqualTo(newCount));
    }

    [Test]
    public async Task RecordStream_WithAdminUser_PassesIsAdminTrue()
    {
        // Arrange
        var songMetadataId = 1;
        var adminUser = new ApplicationUser { Id = 1, UserName = "admin@app.com" };
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(adminUser);
        _mockUserManager.Setup(x => x.IsInRoleAsync(adminUser, "Admin"))
            .ReturnsAsync(true);
        _mockStreamCountService.Setup(s => s.IncrementStreamCountAsync(songMetadataId, adminUser.Id, true))
            .ReturnsAsync(10);

        // Act
        var result = await _controller.RecordStream(songMetadataId);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockStreamCountService.Verify(s => s.IncrementStreamCountAsync(songMetadataId, adminUser.Id, true), Times.Once);
    }

    [Test]
    public async Task RecordStream_WithRegularUser_PassesIsAdminFalse()
    {
        // Arrange
        var songMetadataId = 1;
        var regularUser = new ApplicationUser { Id = 2, UserName = "user@app.com" };
        
        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(regularUser);
        _mockUserManager.Setup(x => x.IsInRoleAsync(regularUser, "Admin"))
            .ReturnsAsync(false);
        _mockStreamCountService.Setup(s => s.IncrementStreamCountAsync(songMetadataId, regularUser.Id, false))
            .ReturnsAsync(15);

        // Act
        var result = await _controller.RecordStream(songMetadataId);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockStreamCountService.Verify(s => s.IncrementStreamCountAsync(songMetadataId, regularUser.Id, false), Times.Once);
    }

    [Test]
    public async Task RecordStream_WithInvalidId_ReturnsBadRequest()
    {
        // Arrange
        var songMetadataId = 0;

        // Act
        var result = await _controller.RecordStream(songMetadataId);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetStreamCount_WithValidId_ReturnsOkWithCount()
    {
        // Arrange
        var songMetadataId = 1;
        var count = 100;
        
        _mockStreamCountService.Setup(s => s.GetStreamCountAsync(songMetadataId))
            .ReturnsAsync(count);

        // Act
        var result = await _controller.GetStreamCount(songMetadataId);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = result as OkObjectResult;
        var value = okResult.Value;
        var streamCountProperty = value.GetType().GetProperty("streamCount");
        var streamCount = (int)streamCountProperty.GetValue(value);
        Assert.That(streamCount, Is.EqualTo(count));
    }

    [Test]
    public async Task GetStreamCount_WithInvalidId_ReturnsBadRequest()
    {
        // Arrange
        var songMetadataId = -1;

        // Act
        var result = await _controller.GetStreamCount(songMetadataId);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetStreamUrls_WithValidFiles_ReturnsOkWithUrls()
    {
        // Arrange
        var fileNames = new List<string> { "track1.mp3", "track2.mp3", "track3.mp3" };
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/test.mp3?sig=signature");

        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync((ApplicationUser)null);
        _mockStorageService.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetStreamUrls(fileNames);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = result as OkObjectResult;
        var urls = okResult.Value as Dictionary<string, string>;
        Assert.That(urls, Is.Not.Null);
        Assert.That(urls.Count, Is.EqualTo(3));
        Assert.That(urls.ContainsKey("track1.mp3"), Is.True);
        Assert.That(urls.ContainsKey("track2.mp3"), Is.True);
        Assert.That(urls.ContainsKey("track3.mp3"), Is.True);
    }

    [Test]
    public async Task GetStreamUrls_WithNullList_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.GetStreamUrls(null);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    [Test]
    public async Task GetStreamUrls_WithEmptyList_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.GetStreamUrls(new List<string>());

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    [Test]
    public async Task GetStreamUrls_ForSubscriber_UsesLongerLifetime()
    {
        // Arrange
        var fileNames = new List<string> { "track1.mp3" };
        var userId = 123;
        var user = new ApplicationUser { Id = userId, UserName = "testuser" };
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/test.mp3?sig=signature");

        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync(user);
        _mockSubscriptionService.Setup(s => s.HasActiveSubscriptionAsync(userId))
            .ReturnsAsync(true);
        _mockStorageService.Setup(s => s.GetReadSasUri("track1.mp3", TimeSpan.FromHours(24)))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetStreamUrls(fileNames);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockStorageService.Verify(s => s.GetReadSasUri("track1.mp3", TimeSpan.FromHours(24)), Times.Once);
    }

    [Test]
    public async Task GetStreamUrls_SkipsBlankFileNames()
    {
        // Arrange
        var fileNames = new List<string> { "track1.mp3", "", "  ", "track2.mp3" };
        var sasUri = new Uri("https://storage.blob.core.windows.net/container/test.mp3?sig=signature");

        _mockUserManager.Setup(x => x.GetUserAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>()))
            .ReturnsAsync((ApplicationUser)null);
        _mockStorageService.Setup(s => s.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(sasUri);

        // Act
        var result = await _controller.GetStreamUrls(fileNames);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = result as OkObjectResult;
        var urls = okResult.Value as Dictionary<string, string>;
        Assert.That(urls, Is.Not.Null);
        Assert.That(urls.Count, Is.EqualTo(2));
        Assert.That(urls.ContainsKey("track1.mp3"), Is.True);
        Assert.That(urls.ContainsKey("track2.mp3"), Is.True);
    }
}
