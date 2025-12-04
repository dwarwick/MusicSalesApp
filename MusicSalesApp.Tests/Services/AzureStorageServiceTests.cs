using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MusicSalesApp.Services;
using MusicSalesApp.Common.Helpers;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AzureStorageServiceTests
{
    private Mock<ILogger<AzureStorageService>> _mockLogger;
    private IOptions<AzureStorageOptions> _options;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<AzureStorageService>>();
        _options = Options.Create(new AzureStorageOptions
        {
            StorageAccountConnectionString = "UseDevelopmentStorage=true",
            ContainerName = "test-container"
        });
    }

    [Test]
    public void Constructor_WithValidOptions_DoesNotThrow()
    {
        // Act & Assert
        Assert.DoesNotThrow(() => new AzureStorageService(_options, _mockLogger.Object));
    }

    [Test]
    public void Constructor_WithMissingConnectionString_ThrowsArgumentException()
    {
        // Arrange
        var invalidOptions = Options.Create(new AzureStorageOptions
        {
            StorageAccountConnectionString = "",
            ContainerName = "test-container"
        });

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => new AzureStorageService(invalidOptions, _mockLogger.Object));
        Assert.That(ex.Message, Does.Contain("StorageAccountConnectionString"));
    }

    [Test]
    public void Constructor_WithMissingContainerName_ThrowsArgumentException()
    {
        // Arrange
        var invalidOptions = Options.Create(new AzureStorageOptions
        {
            StorageAccountConnectionString = "UseDevelopmentStorage=true",
            ContainerName = ""
        });

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => new AzureStorageService(invalidOptions, _mockLogger.Object));
        Assert.That(ex.Message, Does.Contain("ContainerName"));
    }

    [Test]
    public async Task SetTagsAsync_WithNullFileName_ThrowsArgumentNullException()
    {
        // Arrange
        var service = new AzureStorageService(_options, _mockLogger.Object);
        var tags = new Dictionary<string, string> { { "test", "value" } };

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () => await service.SetTagsAsync(null, tags));
    }

    [Test]
    public async Task SetTagsAsync_WithEmptyFileName_ThrowsArgumentNullException()
    {
        // Arrange
        var service = new AzureStorageService(_options, _mockLogger.Object);
        var tags = new Dictionary<string, string> { { "test", "value" } };

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () => await service.SetTagsAsync("", tags));
    }

    [Test]
    public async Task SetTagsAsync_WithNullTags_ThrowsArgumentNullException()
    {
        // Arrange
        var service = new AzureStorageService(_options, _mockLogger.Object);

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () => await service.SetTagsAsync("test.mp3", null));
    }

    [Test]
    public async Task GetTagsAsync_WithNullFileName_ThrowsArgumentNullException()
    {
        // Arrange
        var service = new AzureStorageService(_options, _mockLogger.Object);

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () => await service.GetTagsAsync(null));
    }

    [Test]
    public async Task GetTagsAsync_WithEmptyFileName_ThrowsArgumentNullException()
    {
        // Arrange
        var service = new AzureStorageService(_options, _mockLogger.Object);

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () => await service.GetTagsAsync(""));
    }
}
