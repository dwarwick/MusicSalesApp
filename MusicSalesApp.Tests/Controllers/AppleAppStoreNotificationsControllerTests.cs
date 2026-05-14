using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using MusicSalesApp.Controllers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class AppleAppStoreNotificationsControllerTests
{
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<AppleAppStoreNotificationsController>> _mockLogger;
    private AppleAppStoreNotificationsController _controller;

    [SetUp]
    public void Setup()
    {
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<AppleAppStoreNotificationsController>>();

        _mockConfiguration.Setup(c => c["AppleAppStore:SubscriptionProductId"]).Returns("streamtunes_monthly_sub_ios");

        _controller = new AppleAppStoreNotificationsController(
            _mockSubscriptionService.Object,
            _mockConfiguration.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task HandleNotification_UpdatesSubscriptionStatus_ForRenewalNotification()
    {
        var transactionPayload = Base64UrlEncoder.Encode("{\"transactionId\":\"tx-123\",\"originalTransactionId\":\"orig-123\",\"productId\":\"streamtunes_monthly_sub_ios\",\"bundleId\":\"net.streamtunes.musicsalesapp.maui\",\"environment\":\"Sandbox\",\"expiresDate\":1893456000000}");
        var signedTransactionInfo = $"header.{transactionPayload}.signature";
        var notificationPayload = Base64UrlEncoder.Encode($"{{\"notificationType\":\"DID_RENEW\",\"subtype\":\"INITIAL_BUY\",\"data\":{{\"signedTransactionInfo\":\"{signedTransactionInfo}\"}}}}");
        var signedPayload = $"header.{notificationPayload}.signature";

        var result = await _controller.HandleNotification(new AppStoreServerNotificationRequest(signedPayload));

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
                3.99m),
            Times.Once);
    }

    [Test]
    public async Task HandleNotification_IgnoresNotification_ForDifferentProduct()
    {
        var transactionPayload = Base64UrlEncoder.Encode("{\"transactionId\":\"tx-123\",\"originalTransactionId\":\"orig-123\",\"productId\":\"other_product\",\"bundleId\":\"net.streamtunes.musicsalesapp.maui\",\"environment\":\"Sandbox\",\"expiresDate\":1893456000000}");
        var signedTransactionInfo = $"header.{transactionPayload}.signature";
        var notificationPayload = Base64UrlEncoder.Encode($"{{\"notificationType\":\"DID_RENEW\",\"data\":{{\"signedTransactionInfo\":\"{signedTransactionInfo}\"}}}}");
        var signedPayload = $"header.{notificationPayload}.signature";

        var result = await _controller.HandleNotification(new AppStoreServerNotificationRequest(signedPayload));

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
        var transactionPayload = Base64UrlEncoder.Encode("{\"transactionId\":\"tx-123\",\"originalTransactionId\":\"orig-123\",\"productId\":\"streamtunes_monthly_sub_ios\",\"bundleId\":\"net.streamtunes.musicsalesapp.maui\",\"environment\":\"Sandbox\",\"expiresDate\":1893456000000}");
        var signedTransactionInfo = $"header.{transactionPayload}.signature";
        var renewalPayload = Base64UrlEncoder.Encode("{\"autoRenewStatus\":0}");
        var signedRenewalInfo = $"header.{renewalPayload}.signature";
        var notificationPayload = Base64UrlEncoder.Encode($"{{\"notificationType\":\"DID_CHANGE_RENEWAL_STATUS\",\"subtype\":\"AUTO_RENEW_DISABLED\",\"data\":{{\"signedTransactionInfo\":\"{signedTransactionInfo}\",\"signedRenewalInfo\":\"{signedRenewalInfo}\"}}}}");
        var signedPayload = $"header.{notificationPayload}.signature";

        var result = await _controller.HandleNotification(new AppStoreServerNotificationRequest(signedPayload));

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
                3.99m),
            Times.Once);
    }
}