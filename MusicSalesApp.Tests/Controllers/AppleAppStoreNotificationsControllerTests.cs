#nullable enable
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Controllers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class AppleAppStoreNotificationsControllerTests
{
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<IAccountEmailService> _mockAccountEmailService;
    private Mock<ISubscriptionConfirmationEmailService> _mockSubscriptionConfirmationEmailService;
    private Mock<IAppleAppStoreVerificationService> _mockVerificationService;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<AppleAppStoreNotificationsController>> _mockLogger;
    private AppleAppStoreNotificationsController _controller;

    [SetUp]
    public void Setup()
    {
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockAccountEmailService = new Mock<IAccountEmailService>();
        _mockSubscriptionConfirmationEmailService = new Mock<ISubscriptionConfirmationEmailService>();
        _mockVerificationService = new Mock<IAppleAppStoreVerificationService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<AppleAppStoreNotificationsController>>();
        var userStore = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _mockConfiguration.Setup(c => c["AppleAppStore:SubscriptionProductId"]).Returns("streamtunes_monthly_sub_ios");
        _mockConfiguration.Setup(c => c["BaseUrl"]).Returns("https://davidtest.dev");

        _controller = new AppleAppStoreNotificationsController(
            _mockSubscriptionService.Object,
            _mockAccountEmailService.Object,
            _mockSubscriptionConfirmationEmailService.Object,
            _mockVerificationService.Object,
            _mockUserManager.Object,
            _mockConfiguration.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task HandleNotification_UpdatesSubscriptionStatus_ForRenewalNotification()
    {
        SetupVerifiedNotification("DID_RENEW", "INITIAL_BUY", price: 990);

        var result = await _controller.HandleNotification(new AppStoreServerNotificationRequest("signed-payload"));

        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockSubscriptionService.Verify(
            s => s.UpdateAppleSubscriptionStatusAsync(
                "orig-123",
                "ACTIVE",
                It.IsAny<DateTime?>(),
                "tx-123",
                "Sandbox",
                "streamtunes_monthly_sub_ios",
                It.IsAny<string>(),
                0.99m),
            Times.Once);
        _mockAccountEmailService.Verify(
            service => service.SendSubscriptionCancelledEmailAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>()),
            Times.Never);
        _mockSubscriptionConfirmationEmailService.Verify(
            service => service.SendConfirmationAsync(
                It.IsAny<ApplicationUser>(),
                It.IsAny<Subscription>(),
                It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task HandleNotification_IgnoresNotification_ForDifferentProduct()
    {
        SetupVerifiedNotification("DID_RENEW", productId: "other_product");

        var result = await _controller.HandleNotification(new AppStoreServerNotificationRequest("signed-payload"));

        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockSubscriptionService.Verify(
            s => s.UpdateAppleSubscriptionStatusAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>()),
            Times.Never);
    }

    [Test]
    public async Task HandleNotification_UpdatesSubscriptionStatus_ForAutoRenewDisabledNotification()
    {
        var subscription = new Subscription
        {
            Id = 7,
            UserId = 42,
            BillingSource = BillingSources.Apple,
            EndDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        };
        var user = new ApplicationUser
        {
            Id = 42,
            UserName = "appleuser",
            Email = "apple@example.com",
            TimeZoneId = "America/Los_Angeles"
        };

        _mockSubscriptionService.Setup(s => s.GetSubscriptionByAppleOriginalTransactionIdAsync("orig-123"))
            .ReturnsAsync(subscription);
        _mockUserManager.Setup(m => m.FindByIdAsync("42"))
            .ReturnsAsync(user);

        SetupVerifiedNotification(
            "DID_CHANGE_RENEWAL_STATUS",
            "AUTO_RENEW_DISABLED",
            renewal: new AppleAppStoreServerRenewalInfo(0, 990, "USD"));

        var result = await _controller.HandleNotification(new AppStoreServerNotificationRequest("signed-payload"));

        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockSubscriptionService.Verify(
            s => s.UpdateAppleSubscriptionStatusAsync(
                "orig-123",
                "CANCELLED",
                It.IsAny<DateTime?>(),
                "tx-123",
                "Sandbox",
                "streamtunes_monthly_sub_ios",
                It.IsAny<string>(),
                0.99m),
            Times.Once);
        _mockAccountEmailService.Verify(
            service => service.SendSubscriptionCancelledEmailAsync(
                "apple@example.com",
                "appleuser",
                subscription.EndDate,
                BillingSources.Apple,
                "America/Los_Angeles",
                "https://davidtest.dev"),
            Times.Once);
    }

    [Test]
    public async Task HandleNotification_SendsConfirmationEmail_WhenSubscriptionReactivatesAfterLapse()
    {
        var expiredSubscription = new Subscription
        {
            Id = 7,
            UserId = 42,
            BillingSource = BillingSources.Apple,
            Status = SubscriptionStatuses.Active,
            EndDate = DateTime.UtcNow.AddMinutes(-5),
            AppStoreOriginalTransactionId = "orig-123"
        };
        var reactivatedSubscription = new Subscription
        {
            Id = 7,
            UserId = 42,
            BillingSource = BillingSources.Apple,
            Status = SubscriptionStatuses.Active,
            EndDate = DateTime.UtcNow.AddDays(30),
            AppStoreOriginalTransactionId = "orig-123"
        };
        var user = new ApplicationUser
        {
            Id = 42,
            UserName = "appleuser",
            Email = "apple@example.com"
        };

        _mockSubscriptionService.SetupSequence(s => s.GetSubscriptionByAppleOriginalTransactionIdAsync("orig-123"))
            .ReturnsAsync(expiredSubscription)
            .ReturnsAsync(reactivatedSubscription);
        _mockUserManager.Setup(m => m.FindByIdAsync("42"))
            .ReturnsAsync(user);

        SetupVerifiedNotification("SUBSCRIBED", "INITIAL_BUY");

        var result = await _controller.HandleNotification(new AppStoreServerNotificationRequest("signed-payload"));

        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockSubscriptionConfirmationEmailService.Verify(
            service => service.SendConfirmationAsync(user, reactivatedSubscription, "https://davidtest.dev"),
            Times.Once);
    }

    [Test]
    public async Task HandleNotification_DoesNotSendConfirmationEmail_WhenSubscriptionIsStillEntitled()
    {
        var currentSubscription = new Subscription
        {
            Id = 7,
            UserId = 42,
            BillingSource = BillingSources.Apple,
            Status = SubscriptionStatuses.Cancelled,
            EndDate = DateTime.UtcNow.AddMinutes(5),
            AppStoreOriginalTransactionId = "orig-123"
        };

        _mockSubscriptionService.Setup(s => s.GetSubscriptionByAppleOriginalTransactionIdAsync("orig-123"))
            .ReturnsAsync(currentSubscription);

        SetupVerifiedNotification("SUBSCRIBED", "INITIAL_BUY");

        var result = await _controller.HandleNotification(new AppStoreServerNotificationRequest("signed-payload"));

        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockSubscriptionConfirmationEmailService.Verify(
            service => service.SendConfirmationAsync(
                It.IsAny<ApplicationUser>(),
                It.IsAny<Subscription>(),
                It.IsAny<string>()),
            Times.Never);
    }

    private void SetupVerifiedNotification(
        string notificationType,
        string? subtype = null,
        string productId = "streamtunes_monthly_sub_ios",
        long? price = null,
        AppleAppStoreServerRenewalInfo? renewal = null)
    {
        _mockVerificationService
            .Setup(service => service.VerifyServerNotification(It.IsAny<string>()))
            .Returns(new AppleAppStoreServerNotificationInfo(
                notificationType,
                subtype,
                new AppleAppStoreServerTransactionInfo(
                    "tx-123",
                    "orig-123",
                    productId,
                    "net.streamtunes.musicsalesapp.maui",
                    "Sandbox",
                    null,
                    1893456000000,
                    null,
                    price,
                    price.HasValue ? "USD" : null),
                renewal));
    }
}
