using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Common.Helpers;
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

    // ------------------------------------------------------------ push notifications

    [Test]
    public async Task IsPushNotificationsEnabledAsync_IsOffWhenNobodyHasSaidOtherwise()
    {
        // The whole point of the flag. Until push is proven end to end and the phone apps are in
        // the stores, an absent row has to read as "not yet" rather than "sure, go ahead".
        var enabled = await _service.IsPushNotificationsEnabledAsync();

        Assert.That(enabled, Is.False);
    }

    [Test]
    public async Task IsPushNotificationsEnabledAsync_IsOffWhenTheStoredValueIsNotABoolean()
    {
        await _service.SetSettingAsync(
            AppSettingsService.PushNotificationsEnabledKey, "yes", "Hand-edited");

        // A row nobody can parse must fail closed, the same as a missing one. Reading "yes" as
        // true would let a typo in the settings table start sending notifications.
        var enabled = await _service.IsPushNotificationsEnabledAsync();

        Assert.That(enabled, Is.False);
    }

    [Test]
    public async Task SetPushNotificationsEnabledAsync_RoundTripsBothWays()
    {
        await _service.SetPushNotificationsEnabledAsync(true);
        var afterTurningOn = await _service.IsPushNotificationsEnabledAsync();

        await _service.SetPushNotificationsEnabledAsync(false);
        var afterTurningOff = await _service.IsPushNotificationsEnabledAsync();

        Assert.Multiple(() =>
        {
            Assert.That(afterTurningOn, Is.True);
            Assert.That(afterTurningOff, Is.False, "An admin must be able to switch it back off.");
        });
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

    // ===== PayPal web subscription offer =====

    [Test]
    public async Task GetPayPalWebSubscriptionOfferAsync_ReturnsNull_WhenNotConfigured()
    {
        var result = await _service.GetPayPalWebSubscriptionOfferAsync();

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SetPayPalWebSubscriptionOfferAsync_RoundTripsAtomicSnapshot()
    {
        var offer = CreatePayPalWebOffer();

        var saved = await _service.SetPayPalWebSubscriptionOfferAsync(offer);
        var loaded = await _service.GetPayPalWebSubscriptionOfferAsync();

        Assert.Multiple(() =>
        {
            Assert.That(saved.Version, Is.EqualTo(1));
            Assert.That(saved.UpdatedAtUtc, Is.Not.EqualTo(default(DateTime)));
            Assert.That(loaded, Is.EqualTo(saved));
            Assert.That(loaded!.PrimaryPlan.TrialDays, Is.EqualTo(3));
            Assert.That(loaded.ResubscriberPlan!.RegularPrice, Is.EqualTo(0.99m));
        });

        using var context = new AppDbContext(_dbOptions);
        var storedSettings = await context.AppSettings
            .Where(setting => setting.Key == AppSettingKeys.PayPalWebSubscriptionOfferSnapshot)
            .ToListAsync();
        Assert.That(storedSettings, Has.Count.EqualTo(1));
        Assert.That(storedSettings[0].Value.Length, Is.LessThanOrEqualTo(2000));
    }

    [Test]
    public async Task SetPayPalWebSubscriptionOfferAsync_IncrementsVersionAndUpdatesSnapshot()
    {
        var first = await _service.SetPayPalWebSubscriptionOfferAsync(CreatePayPalWebOffer());
        var second = await _service.SetPayPalWebSubscriptionOfferAsync(
            CreatePayPalWebOffer() with
            {
                PrimaryPlan = CreatePayPalWebOffer().PrimaryPlan with { Name = "Updated trial plan" }
            });

        Assert.Multiple(() =>
        {
            Assert.That(first.Version, Is.EqualTo(1));
            Assert.That(second.Version, Is.EqualTo(2));
            Assert.That(second.PrimaryPlan.Name, Is.EqualTo("Updated trial plan"));
        });
    }

    [Test]
    public async Task SetPayPalWebSubscriptionOfferAsync_ConcurrentSavesReceiveDistinctVersions()
    {
        var secondService = new AppSettingsService(_contextFactory, _mockLogger.Object);
        var firstOffer = CreatePayPalWebOffer() with
        {
            PrimaryPlan = CreatePayPalWebOffer().PrimaryPlan with { Id = "P-FIRST" }
        };
        var secondOffer = CreatePayPalWebOffer() with
        {
            PrimaryPlan = CreatePayPalWebOffer().PrimaryPlan with { Id = "P-SECOND" }
        };

        var savedOffers = await Task.WhenAll(
            _service.SetPayPalWebSubscriptionOfferAsync(firstOffer),
            secondService.SetPayPalWebSubscriptionOfferAsync(secondOffer));
        var loaded = await _service.GetPayPalWebSubscriptionOfferAsync();

        Assert.Multiple(() =>
        {
            Assert.That(savedOffers.Select(offer => offer.Version), Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(loaded!.Version, Is.EqualTo(2));
            Assert.That(
                loaded.PrimaryPlan.Id,
                Is.EqualTo(savedOffers.Single(offer => offer.Version == 2).PrimaryPlan.Id));
        });
    }

    [Test]
    public async Task GetPayPalWebSubscriptionOfferAsync_ReturnsNull_WhenSnapshotIsMalformed()
    {
        await _service.SetSettingAsync(
            AppSettingKeys.PayPalWebSubscriptionOfferSnapshot,
            "{not-json}");

        var result = await _service.GetPayPalWebSubscriptionOfferAsync();

        Assert.That(result, Is.Null);
    }

    // ===== Site Maintenance Methods =====

    [Test]
    public async Task GetSiteMaintenanceStartUtcAsync_ReturnsNull_WhenNotSet()
    {
        var result = await _service.GetSiteMaintenanceStartUtcAsync();
        Assert.That(result, Is.Null);
    }

    private static PayPalWebSubscriptionOffer CreatePayPalWebOffer()
    {
        return new PayPalWebSubscriptionOffer
        {
            PrimaryPlan = new PayPalWebPlanSnapshot
            {
                Id = "P-TRIAL",
                Name = "Three-day trial",
                Status = PayPalPlanStatuses.Active,
                RegularPrice = 0.99m,
                CurrencyCode = PayPalSubscriptionDefaults.UsdCurrencyCode,
                IntervalUnit = PayPalBillingIntervals.Month,
                IntervalCount = 1,
                TrialDays = 3
            },
            ResubscriberPlan = new PayPalWebPlanSnapshot
            {
                Id = "P-NO-TRIAL",
                Name = "Monthly without trial",
                Status = PayPalPlanStatuses.Active,
                RegularPrice = 0.99m,
                CurrencyCode = PayPalSubscriptionDefaults.UsdCurrencyCode,
                IntervalUnit = PayPalBillingIntervals.Month,
                IntervalCount = 1
            }
        };
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
                Key = AppSettingsService.SiteMaintenanceStartUtcKey,
                Value = "not-a-date",
                UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var result = await _service.GetSiteMaintenanceStartUtcAsync();
        Assert.That(result, Is.Null);
    }
}
