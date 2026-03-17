using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AppSettingsServiceTests
{
    private Mock<ILogger<AppSettingsService>> _mockLogger;
    private DbContextOptions<AppDbContext> _dbOptions;
    private IDbContextFactory<AppDbContext> _contextFactory;
    private AppSettingsService _service;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<AppSettingsService>>();

        // Use in-memory database for testing
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        var mockFactory = new Mock<IDbContextFactory<AppDbContext>>();
        mockFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_dbOptions));

        _contextFactory = mockFactory.Object;
        _service = new AppSettingsService(_contextFactory, _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Database.EnsureDeleted();
    }

    [Test]
    public async Task GetSettingAsync_ReturnsNull_WhenSettingDoesNotExist()
    {
        // Act
        var result = await _service.GetSettingAsync("NonExistentKey");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SetSettingAsync_CreatesNewSetting_WhenNotExists()
    {
        // Arrange
        var key = "TestKey";
        var value = "TestValue";
        var description = "Test description";

        // Act
        await _service.SetSettingAsync(key, value, description);

        // Assert
        var result = await _service.GetSettingAsync(key);
        Assert.That(result, Is.EqualTo(value));
    }

    [Test]
    public async Task SetSettingAsync_UpdatesExistingSetting()
    {
        // Arrange
        var key = "TestKey";
        var initialValue = "InitialValue";
        var updatedValue = "UpdatedValue";

        await _service.SetSettingAsync(key, initialValue);

        // Act
        await _service.SetSettingAsync(key, updatedValue);

        // Assert
        var result = await _service.GetSettingAsync(key);
        Assert.That(result, Is.EqualTo(updatedValue));
    }

    [Test]
    public async Task GetSubscriptionPriceAsync_ReturnsDefaultPrice_WhenNotSet()
    {
        // Act
        var result = await _service.GetSubscriptionPriceAsync();

        // Assert
        Assert.That(result, Is.EqualTo(AppSettingsService.DefaultSubscriptionPrice));
    }

    [Test]
    public async Task GetSubscriptionPriceAsync_ReturnsConfiguredPrice()
    {
        // Arrange
        var expectedPrice = 5.99m;
        await _service.SetSubscriptionPriceAsync(expectedPrice);

        // Act
        var result = await _service.GetSubscriptionPriceAsync();

        // Assert
        Assert.That(result, Is.EqualTo(expectedPrice));
    }

    [Test]
    public async Task SetSubscriptionPriceAsync_SavesCorrectFormat()
    {
        // Arrange
        var price = 9.99m;

        // Act
        await _service.SetSubscriptionPriceAsync(price);

        // Assert
        var rawValue = await _service.GetSettingAsync(AppSettingsService.SubscriptionPriceKey);
        Assert.That(rawValue, Is.EqualTo("9.99"));
    }

    [Test]
    public async Task GetSubscriptionPriceAsync_ReturnsDefault_WhenInvalidValueStored()
    {
        // Arrange - Store an invalid value directly
        using (var context = new AppDbContext(_dbOptions))
        {
            context.AppSettings.Add(new AppSettings
            {
                Key = AppSettingsService.SubscriptionPriceKey,
                Value = "invalid",
                UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _service.GetSubscriptionPriceAsync();

        // Assert
        Assert.That(result, Is.EqualTo(AppSettingsService.DefaultSubscriptionPrice));
    }

    // ===== Site Maintenance Methods =====

    [Test]
    public async Task GetSiteMaintenanceStartUtcAsync_ReturnsNull_WhenNotSet()
    {
        var result = await _service.GetSiteMaintenanceStartUtcAsync();
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SetAndGetSiteMaintenanceStartUtcAsync_RoundTrips()
    {
        var expected = new DateTime(2025, 7, 15, 3, 0, 0, DateTimeKind.Utc);
        await _service.SetSiteMaintenanceStartUtcAsync(expected);

        var result = await _service.GetSiteMaintenanceStartUtcAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.EqualTo(expected).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task GetSiteMaintenanceEndUtcAsync_ReturnsNull_WhenNotSet()
    {
        var result = await _service.GetSiteMaintenanceEndUtcAsync();
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SetAndGetSiteMaintenanceEndUtcAsync_RoundTrips()
    {
        var expected = new DateTime(2025, 7, 15, 7, 0, 0, DateTimeKind.Utc);
        await _service.SetSiteMaintenanceEndUtcAsync(expected);

        var result = await _service.GetSiteMaintenanceEndUtcAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.EqualTo(expected).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task ShouldShowSiteMaintenanceNoticeAsync_ReturnsFalse_WhenNotSet()
    {
        var result = await _service.ShouldShowSiteMaintenanceNoticeAsync();
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task ShouldShowSiteMaintenanceNoticeAsync_ReturnsFalse_WhenEndIsMinValue()
    {
        await _service.SetSiteMaintenanceEndUtcAsync(DateTime.MinValue);

        var result = await _service.ShouldShowSiteMaintenanceNoticeAsync();
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task ShouldShowSiteMaintenanceNoticeAsync_ReturnsFalse_WhenEndIsInPast()
    {
        await _service.SetSiteMaintenanceEndUtcAsync(DateTime.UtcNow.AddHours(-1));

        var result = await _service.ShouldShowSiteMaintenanceNoticeAsync();
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task ShouldShowSiteMaintenanceNoticeAsync_ReturnsTrue_WhenEndIsInFuture()
    {
        await _service.SetSiteMaintenanceEndUtcAsync(DateTime.UtcNow.AddHours(2));

        var result = await _service.ShouldShowSiteMaintenanceNoticeAsync();
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task GetSiteMaintenanceStartUtcAsync_ReturnsNull_WhenInvalidValueStored()
    {
        using (var context = new AppDbContext(_dbOptions))
        {
            context.AppSettings.Add(new AppSettings
            {
                Key = "SiteMaintenanceStartUtc",
                Value = "not-a-date",
                UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var result = await _service.GetSiteMaintenanceStartUtcAsync();
        Assert.That(result, Is.Null);
    }
}
