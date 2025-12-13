using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class SubscriptionServiceTests
{
    private Mock<ILogger<SubscriptionService>> _mockLogger;
    private DbContextOptions<AppDbContext> _dbOptions;
    private IDbContextFactory<AppDbContext> _contextFactory;
    private SubscriptionService _service;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<SubscriptionService>>();

        // Use in-memory database for testing
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        var mockFactory = new Mock<IDbContextFactory<AppDbContext>>();
        mockFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_dbOptions));

        _contextFactory = mockFactory.Object;
        _service = new SubscriptionService(_contextFactory, _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Database.EnsureDeleted();
    }

    [Test]
    public async Task CreateSubscriptionAsync_CreatesNewSubscription()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-123456789";
        var monthlyPrice = 3.99m;

        // Act
        var result = await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.UserId, Is.EqualTo(userId));
        Assert.That(result.PayPalSubscriptionId, Is.EqualTo(paypalSubscriptionId));
        Assert.That(result.MonthlyPrice, Is.EqualTo(monthlyPrice));
        Assert.That(result.Status, Is.EqualTo("ACTIVE"));
    }

    [Test]
    public async Task HasActiveSubscriptionAsync_ReturnsTrueWhenSubscriptionExists()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-123456789";
        var monthlyPrice = 3.99m;
        await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);

        // Act
        var result = await _service.HasActiveSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task HasActiveSubscriptionAsync_ReturnsFalseWhenNoSubscription()
    {
        // Arrange
        var userId = 1;

        // Act
        var result = await _service.HasActiveSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CancelSubscriptionAsync_CancelsActiveSubscription()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-123456789";
        var monthlyPrice = 3.99m;
        var createdSubscription = await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);

        // Act
        var result = await _service.CancelSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.True);

        // Get the subscription directly by PayPal ID since GetActiveSubscriptionAsync filters by ACTIVE status
        var subscription = await _service.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
        Assert.That(subscription, Is.Not.Null);
        Assert.That(subscription.Status, Is.EqualTo("CANCELLED"));
        Assert.That(subscription.CancelledAt, Is.Not.Null);
        Assert.That(subscription.EndDate, Is.Not.Null);
    }

    [Test]
    public async Task GetActiveSubscriptionAsync_ReturnsNullWhenNoActiveSubscription()
    {
        // Arrange
        var userId = 1;

        // Act
        var result = await _service.GetActiveSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetActiveSubscriptionAsync_ReturnsActiveSubscription()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-123456789";
        var monthlyPrice = 3.99m;
        await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);

        // Act
        var result = await _service.GetActiveSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.UserId, Is.EqualTo(userId));
        Assert.That(result.Status, Is.EqualTo("ACTIVE"));
    }

    [Test]
    public async Task UpdateSubscriptionStatusAsync_UpdatesStatus()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-123456789";
        var monthlyPrice = 3.99m;
        await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);
        var newStatus = "SUSPENDED";
        var nextBillingDate = DateTime.UtcNow.AddMonths(1);

        // Act
        await _service.UpdateSubscriptionStatusAsync(paypalSubscriptionId, newStatus, nextBillingDate);

        // Assert
        var subscription = await _service.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
        Assert.That(subscription, Is.Not.Null);
        Assert.That(subscription.Status, Is.EqualTo(newStatus));
        Assert.That(subscription.NextBillingDate, Is.Not.Null);
    }

    [Test]
    public async Task DeletePendingSubscriptionAsync_DeletesUnpaidSubscription()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-123456789";
        var monthlyPrice = 3.99m;
        await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);

        // Act - Delete pending subscription (no payment made yet)
        var result = await _service.DeletePendingSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.True);
        
        // Verify subscription is deleted
        var subscription = await _service.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
        Assert.That(subscription, Is.Null);
    }

    [Test]
    public async Task DeletePendingSubscriptionAsync_DoesNotDeletePaidSubscription()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-123456789";
        var monthlyPrice = 3.99m;
        await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);
        
        // Simulate payment by setting LastPaymentDate
        await _service.UpdateSubscriptionDetailsAsync(
            paypalSubscriptionId, 
            DateTime.UtcNow.AddMonths(1), 
            DateTime.UtcNow);

        // Act - Try to delete but it has payment so should not delete
        var result = await _service.DeletePendingSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.False);
        
        // Verify subscription still exists
        var subscription = await _service.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
        Assert.That(subscription, Is.Not.Null);
        Assert.That(subscription.Status, Is.EqualTo("ACTIVE"));
    }

    [Test]
    public async Task DeletePendingSubscriptionAsync_ReturnsFalseWhenNoSubscription()
    {
        // Arrange
        var userId = 1;

        // Act
        var result = await _service.DeletePendingSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.False);
    }
}
