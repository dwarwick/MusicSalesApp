using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
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
        Assert.That(result.Status, Is.EqualTo(SubscriptionStatuses.ApprovalPending));
    }

    [Test]
    public async Task HasActiveSubscriptionAsync_ReturnsTrueWhenSubscriptionExists()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-123456789";
        var monthlyPrice = 3.99m;
        await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);
        // Activate the subscription (simulating successful PayPal payment)
        await _service.ActivateSubscriptionAsync(paypalSubscriptionId, DateTime.UtcNow.AddMonths(1), DateTime.UtcNow);

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
        // Activate the subscription (simulating successful PayPal payment)
        await _service.ActivateSubscriptionAsync(paypalSubscriptionId, DateTime.UtcNow.AddMonths(1), DateTime.UtcNow);

        // Act
        var result = await _service.CancelSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.True);

        // Get the subscription directly by PayPal ID since GetActiveSubscriptionAsync filters by ACTIVE status
        var subscription = await _service.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
        Assert.That(subscription, Is.Not.Null);
        Assert.That(subscription.Status, Is.EqualTo(SubscriptionStatuses.Cancelled));
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
        // Activate the subscription (simulating successful PayPal payment)
        await _service.ActivateSubscriptionAsync(paypalSubscriptionId, DateTime.UtcNow.AddMonths(1), DateTime.UtcNow);

        // Act
        var result = await _service.GetActiveSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.UserId, Is.EqualTo(userId));
        Assert.That(result.Status, Is.EqualTo(SubscriptionStatuses.Active));
    }

    [Test]
    public async Task GetActiveSubscriptionAsync_ReturnsCancelledSubscription_WhenStillInsideBillingPeriod()
    {
        const int userId = 1;

        using (var context = new AppDbContext(_dbOptions))
        {
            context.Subscriptions.Add(new Subscription
            {
                UserId = userId,
                Status = SubscriptionStatuses.Cancelled,
                EndDate = DateTime.UtcNow.AddDays(5),
                MonthlyPrice = 3.99m,
                CreatedAt = DateTime.UtcNow,
                LastPaymentDate = DateTime.UtcNow.AddDays(-20)
            });
            await context.SaveChangesAsync();
        }

        var result = await _service.GetActiveSubscriptionAsync(userId);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Status, Is.EqualTo(SubscriptionStatuses.Cancelled));
    }

    [Test]
    public async Task GetLatestSubscriptionAsync_ExpiresCancelledSubscription_WhenBillingPeriodHasEnded()
    {
        const int userId = 1;

        using (var context = new AppDbContext(_dbOptions))
        {
            context.Subscriptions.Add(new Subscription
            {
                UserId = userId,
                Status = SubscriptionStatuses.Cancelled,
                EndDate = DateTime.UtcNow.AddMinutes(-5),
                MonthlyPrice = 3.99m,
                CreatedAt = DateTime.UtcNow,
                CancelledAt = DateTime.UtcNow.AddDays(-2),
                LastPaymentDate = DateTime.UtcNow.AddDays(-20)
            });
            await context.SaveChangesAsync();
        }

        var result = await _service.GetLatestSubscriptionAsync(userId);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Status, Is.EqualTo(SubscriptionStatuses.Expired));

        var hasActiveSubscription = await _service.HasActiveSubscriptionAsync(userId);
        Assert.That(hasActiveSubscription, Is.False);
    }

    [Test]
    public async Task NormalizeExpiredSubscriptionsAsync_ExpiresAllStaleEntitlements()
    {
        var expiredEndDate = DateTime.UtcNow.AddMinutes(-10);

        using (var context = new AppDbContext(_dbOptions))
        {
            context.Subscriptions.AddRange(
                new Subscription
                {
                    UserId = 1,
                    Status = SubscriptionStatuses.Active,
                    EndDate = expiredEndDate,
                    MonthlyPrice = 3.99m,
                    CreatedAt = DateTime.UtcNow.AddDays(-5),
                    LastPaymentDate = DateTime.UtcNow.AddDays(-30)
                },
                new Subscription
                {
                    UserId = 2,
                    Status = SubscriptionStatuses.Cancelled,
                    EndDate = expiredEndDate,
                    MonthlyPrice = 3.99m,
                    CreatedAt = DateTime.UtcNow.AddDays(-4),
                    CancelledAt = DateTime.UtcNow.AddDays(-1),
                    LastPaymentDate = DateTime.UtcNow.AddDays(-30)
                },
                new Subscription
                {
                    UserId = 3,
                    Status = SubscriptionStatuses.Suspended,
                    EndDate = expiredEndDate,
                    MonthlyPrice = 3.99m,
                    CreatedAt = DateTime.UtcNow.AddDays(-3),
                    LastPaymentDate = DateTime.UtcNow.AddDays(-30)
                },
                new Subscription
                {
                    UserId = 4,
                    Status = SubscriptionStatuses.Active,
                    EndDate = DateTime.UtcNow.AddDays(2),
                    MonthlyPrice = 3.99m,
                    CreatedAt = DateTime.UtcNow.AddDays(-2),
                    LastPaymentDate = DateTime.UtcNow.AddDays(-5)
                });

            await context.SaveChangesAsync();
        }

        var normalizedCount = await _service.NormalizeExpiredSubscriptionsAsync();

        Assert.That(normalizedCount, Is.EqualTo(3));

        using var verificationContext = new AppDbContext(_dbOptions);
        var subscriptions = await verificationContext.Subscriptions
            .OrderBy(s => s.UserId)
            .ToListAsync();

        Assert.That(subscriptions[0].Status, Is.EqualTo(SubscriptionStatuses.Expired));
        Assert.That(subscriptions[0].CancelledAt, Is.EqualTo(expiredEndDate));
        Assert.That(subscriptions[1].Status, Is.EqualTo(SubscriptionStatuses.Expired));
        Assert.That(subscriptions[2].Status, Is.EqualTo(SubscriptionStatuses.Expired));
        Assert.That(subscriptions[2].CancelledAt, Is.EqualTo(expiredEndDate));
        Assert.That(subscriptions[3].Status, Is.EqualTo(SubscriptionStatuses.Active));
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
        
        // Activate and simulate payment
        await _service.ActivateSubscriptionAsync(paypalSubscriptionId, DateTime.UtcNow.AddMonths(1), DateTime.UtcNow);

        // Act - Try to delete but it has been activated and paid, so should not delete
        var result = await _service.DeletePendingSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.False);
        
        // Verify subscription still exists
        var subscription = await _service.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
        Assert.That(subscription, Is.Not.Null);
        Assert.That(subscription.Status, Is.EqualTo(SubscriptionStatuses.Active));
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

    [Test]
    public async Task CreateSubscriptionAsync_StatusIsApprovalPending()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-PENDING-1";
        var monthlyPrice = 3.99m;

        // Act
        var result = await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);

        // Assert - subscription should be APPROVAL_PENDING, not ACTIVE
        Assert.That(result.Status, Is.EqualTo(SubscriptionStatuses.ApprovalPending));
        
        // GetActiveSubscriptionAsync should NOT return it
        var active = await _service.GetActiveSubscriptionAsync(userId);
        Assert.That(active, Is.Null);
        
        // HasActiveSubscriptionAsync should return false
        var hasActive = await _service.HasActiveSubscriptionAsync(userId);
        Assert.That(hasActive, Is.False);
    }

    [Test]
    public async Task GetPendingSubscriptionAsync_ReturnsPendingSubscription()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-PENDING-2";
        var monthlyPrice = 3.99m;
        await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);

        // Act
        var result = await _service.GetPendingSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Status, Is.EqualTo(SubscriptionStatuses.ApprovalPending));
        Assert.That(result.PayPalSubscriptionId, Is.EqualTo(paypalSubscriptionId));
    }

    [Test]
    public async Task ActivateSubscriptionAsync_SetsStatusToActive()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-ACTIVATE-1";
        var monthlyPrice = 3.99m;
        await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);

        // Act
        await _service.ActivateSubscriptionAsync(paypalSubscriptionId, DateTime.UtcNow.AddMonths(1), DateTime.UtcNow);

        // Assert
        var subscription = await _service.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
        Assert.That(subscription, Is.Not.Null);
        Assert.That(subscription.Status, Is.EqualTo(SubscriptionStatuses.Active));
        Assert.That(subscription.LastPaymentDate, Is.Not.Null);
        Assert.That(subscription.NextBillingDate, Is.Not.Null);
    }

    [Test]
    public async Task DeletePendingSubscriptionAsync_DeletesApprovalPendingSubscription()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-DELETE-PENDING";
        var monthlyPrice = 3.99m;
        await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);

        // Act - Delete the APPROVAL_PENDING subscription
        var result = await _service.DeletePendingSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.True);
        var subscription = await _service.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
        Assert.That(subscription, Is.Null);
    }

    [Test]
    public async Task CancelSubscriptionAsync_CancelsApprovalPendingSubscription()
    {
        // Arrange
        var userId = 1;
        var paypalSubscriptionId = "SUB-CANCEL-PENDING";
        var monthlyPrice = 3.99m;
        await _service.CreateSubscriptionAsync(userId, paypalSubscriptionId, monthlyPrice);

        // Act - Cancel the APPROVAL_PENDING subscription (user cancels from ManageAccount)
        var result = await _service.CancelSubscriptionAsync(userId);

        // Assert
        Assert.That(result, Is.True);
        var subscription = await _service.GetSubscriptionByPayPalIdAsync(paypalSubscriptionId);
        Assert.That(subscription, Is.Not.Null);
        Assert.That(subscription.Status, Is.EqualTo(SubscriptionStatuses.Cancelled));
    }

    [Test]
    public async Task CreateSubscriptionAsync_RemovesStaleApprovalPendingSubscriptions()
    {
        // Arrange - create a stale APPROVAL_PENDING subscription
        var userId = 1;
        var staleSubscriptionId = "SUB-STALE";
        var monthlyPrice = 3.99m;
        await _service.CreateSubscriptionAsync(userId, staleSubscriptionId, monthlyPrice);

        // Act - create a new subscription (should remove the stale one)
        var newSubscriptionId = "SUB-NEW";
        var result = await _service.CreateSubscriptionAsync(userId, newSubscriptionId, monthlyPrice);

        // Assert - stale subscription should be removed
        var stale = await _service.GetSubscriptionByPayPalIdAsync(staleSubscriptionId);
        Assert.That(stale, Is.Null);
        
        // New subscription should exist
        var newSub = await _service.GetSubscriptionByPayPalIdAsync(newSubscriptionId);
        Assert.That(newSub, Is.Not.Null);
        Assert.That(newSub.Status, Is.EqualTo(SubscriptionStatuses.ApprovalPending));
    }

    [Test]
    public async Task CreateAppleSubscriptionAsync_CreatesAppleSubscription()
    {
        const int userId = 1;
        var purchaseTime = DateTime.UtcNow.AddMinutes(-2);

        var result = await _service.CreateAppleSubscriptionAsync(
            userId,
            "apple-tx-1",
            "apple-orig-1",
            "streamtunes_monthly_sub_ios",
            "account-token-1",
            "Sandbox",
            3.99m,
            purchaseTime);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result.UserId, Is.EqualTo(userId));
            Assert.That(result.BillingSource, Is.EqualTo(BillingSources.Apple));
            Assert.That(result.AppStoreTransactionId, Is.EqualTo("apple-tx-1"));
            Assert.That(result.AppStoreOriginalTransactionId, Is.EqualTo("apple-orig-1"));
            Assert.That(result.AppStoreProductId, Is.EqualTo("streamtunes_monthly_sub_ios"));
            Assert.That(result.AppStoreAppAccountToken, Is.EqualTo("account-token-1"));
            Assert.That(result.AppStoreEnvironment, Is.EqualTo("Sandbox"));
            Assert.That(result.Status, Is.EqualTo(SubscriptionStatuses.Active));
            Assert.That(result.StartDate, Is.EqualTo(purchaseTime));
        });
    }

    [Test]
    public async Task GetSubscriptionByAppleTransactionIdAsync_ReturnsAppleSubscription()
    {
        await _service.CreateAppleSubscriptionAsync(
            1,
            "apple-tx-lookup",
            "apple-orig-lookup",
            "streamtunes_monthly_sub_ios",
            null,
            "Production",
            3.99m);

        var result = await _service.GetSubscriptionByAppleTransactionIdAsync("apple-tx-lookup");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.AppStoreOriginalTransactionId, Is.EqualTo("apple-orig-lookup"));
    }

    [Test]
    public async Task UpdateAppleSubscriptionStatusAsync_UpdatesAppleSubscriptionFields()
    {
        await _service.CreateAppleSubscriptionAsync(
            1,
            "apple-tx-old",
            "apple-orig-update",
            "streamtunes_monthly_sub_ios",
            "account-token-1",
            "Sandbox",
            3.99m);

        var expiryTime = DateTime.UtcNow.AddDays(30);

        await _service.UpdateAppleSubscriptionStatusAsync(
            "apple-orig-update",
            SubscriptionStatuses.Active,
            expiryTime,
            "apple-tx-new",
            "Production");

        var result = await _service.GetSubscriptionByAppleOriginalTransactionIdAsync("apple-orig-update");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result.AppStoreTransactionId, Is.EqualTo("apple-tx-new"));
            Assert.That(result.AppStoreEnvironment, Is.EqualTo("Production"));
            Assert.That(result.NextBillingDate, Is.EqualTo(expiryTime));
            Assert.That(result.EndDate, Is.EqualTo(expiryTime));
            Assert.That(result.LastPaymentDate, Is.Not.Null);
        });
    }

    [Test]
    public async Task UpdateAppleSubscriptionStatusAsync_CreatesAppleSubscriptionFromNotification_WhenMissingAndAppAccountTokenMapsToUser()
    {
        var expiryTime = DateTime.UtcNow.AddDays(30);

        await _service.UpdateAppleSubscriptionStatusAsync(
            "apple-orig-missing",
            SubscriptionStatuses.Active,
            expiryTime,
            "apple-tx-created",
            "Sandbox",
            "streamtunes_monthly_sub_ios",
            "22",
            4.99m);

        var result = await _service.GetSubscriptionByAppleOriginalTransactionIdAsync("apple-orig-missing");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.UserId, Is.EqualTo(22));
            Assert.That(result.AppStoreTransactionId, Is.EqualTo("apple-tx-created"));
            Assert.That(result.AppStoreProductId, Is.EqualTo("streamtunes_monthly_sub_ios"));
            Assert.That(result.AppStoreAppAccountToken, Is.EqualTo("22"));
            Assert.That(result.Status, Is.EqualTo(SubscriptionStatuses.Active));
            Assert.That(result.MonthlyPrice, Is.EqualTo(4.99m));
        });
    }

    [Test]
    public async Task UpdateAppleSubscriptionStatusAsync_DoesNotCreateSubscription_WhenNotificationCannotMapToUser()
    {
        await _service.UpdateAppleSubscriptionStatusAsync(
            "apple-orig-missing-no-user",
            SubscriptionStatuses.Active,
            DateTime.UtcNow.AddDays(30),
            "apple-tx-created",
            "Sandbox",
            "streamtunes_monthly_sub_ios",
            "not-a-user-id",
            4.99m);

        var result = await _service.GetSubscriptionByAppleOriginalTransactionIdAsync("apple-orig-missing-no-user");

        Assert.That(result, Is.Null);
    }
}
