#nullable enable
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Controllers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class GooglePlayWebhookControllerTests
{
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<IGooglePlayVerificationService> _mockVerificationService;
    private Mock<IAccountEmailService> _mockAccountEmailService;
    private Mock<ISubscriptionConfirmationEmailService> _mockSubscriptionConfirmationEmailService;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<GooglePlayWebhookController>> _mockLogger;
    private GooglePlayWebhookController _controller;

    [SetUp]
    public void SetUp()
    {
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockVerificationService = new Mock<IGooglePlayVerificationService>();
        _mockAccountEmailService = new Mock<IAccountEmailService>();
        _mockSubscriptionConfirmationEmailService = new Mock<ISubscriptionConfirmationEmailService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<GooglePlayWebhookController>>();

        var userStore = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStore.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _mockConfiguration.Setup(c => c["GooglePlay:SubscriptionProductId"]).Returns("streamtunes_monthly_sub");
        _mockConfiguration.Setup(c => c["BaseUrl"]).Returns("https://davidtest.dev");

        _controller = new GooglePlayWebhookController(
            _mockSubscriptionService.Object,
            _mockVerificationService.Object,
            _mockAccountEmailService.Object,
            _mockSubscriptionConfirmationEmailService.Object,
            _mockUserManager.Object,
            _mockConfiguration.Object,
            _mockLogger.Object);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    [Test]
    public async Task HandleNotification_CancelledSubscription_SendsCancellationEmail()
    {
        var subscription = new Subscription
        {
            Id = 21,
            UserId = 9,
            BillingSource = BillingSources.GooglePlay,
            EndDate = new DateTime(2026, 6, 15, 8, 30, 0, DateTimeKind.Utc)
        };
        var user = new ApplicationUser
        {
            Id = 9,
            UserName = "googleuser",
            Email = "google@example.com"
        };
        var expiryTime = new DateTimeOffset(subscription.EndDate.Value);

        _mockVerificationService.Setup(service => service.VerifySubscriptionAsync("token-123", "streamtunes_monthly_sub"))
            .ReturnsAsync(new GooglePlaySubscriptionInfo(
                "SUBSCRIPTION_STATE_CANCELED",
                DateTimeOffset.UtcNow,
                expiryTime,
                "order-123",
                true,
                string.Empty,
                false,
                "base-plan",
                null,
                [],
                false,
                null,
                "USD"));
        _mockSubscriptionService.Setup(service => service.GetSubscriptionByGooglePlayTokenAsync("token-123"))
            .ReturnsAsync(subscription);
        _mockUserManager.Setup(manager => manager.FindByIdAsync("9"))
            .ReturnsAsync(user);

        var payload = new
        {
            message = new
            {
                data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    subscriptionNotification = new
                    {
                        purchaseToken = "token-123",
                        notificationType = 3
                    }
                })))
            }
        };

        var result = await _controller.HandleNotification(JsonSerializer.SerializeToElement(payload));

        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockSubscriptionService.Verify(service => service.UpdateGooglePlaySubscriptionStatusAsync(
            "token-123",
            SubscriptionStatuses.Cancelled,
            subscription.EndDate,
            It.IsAny<GooglePlaySubscriptionInfo>()), Times.Once);
        _mockAccountEmailService.Verify(service => service.SendSubscriptionCancelledEmailAsync(
            "google@example.com",
            "googleuser",
            subscription.EndDate,
            BillingSources.GooglePlay,
            null,
            "https://davidtest.dev"), Times.Once);
    }

    [Test]
    public async Task HandleNotification_RenewedSubscription_DoesNotSendCancellationEmail()
    {
        var payload = new
        {
            message = new
            {
                data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    subscriptionNotification = new
                    {
                        purchaseToken = "token-456",
                        notificationType = 2
                    }
                })))
            }
        };

        _mockVerificationService.Setup(service => service.VerifySubscriptionAsync("token-456", "streamtunes_monthly_sub"))
            .ReturnsAsync(new GooglePlaySubscriptionInfo(
                "SUBSCRIPTION_STATE_ACTIVE",
                DateTimeOffset.UtcNow,
                new DateTimeOffset(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)),
                "order-456",
                true,
                string.Empty,
                false,
                "base-plan",
                null,
                [],
                true,
                null,
                "USD"));

        var result = await _controller.HandleNotification(JsonSerializer.SerializeToElement(payload));

        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockSubscriptionService.Verify(service => service.UpdateGooglePlaySubscriptionStatusAsync(
            "token-456",
            SubscriptionStatuses.Active,
            It.IsAny<DateTime?>(),
            It.IsAny<GooglePlaySubscriptionInfo>()), Times.Once);
        _mockAccountEmailService.Verify(service => service.SendSubscriptionCancelledEmailAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<DateTime?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task HandleNotification_ExpiredNotificationWithActiveGoogleState_KeepsSubscriptionActive()
    {
        var payload = new
        {
            message = new
            {
                data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    subscriptionNotification = new
                    {
                        purchaseToken = "token-renewed-after-trial",
                        notificationType = 13
                    }
                })))
            }
        };

        _mockVerificationService.Setup(service => service.VerifySubscriptionAsync("token-renewed-after-trial", "streamtunes_monthly_sub"))
            .ReturnsAsync(new GooglePlaySubscriptionInfo(
                "SUBSCRIPTION_STATE_ACTIVE",
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddMinutes(30),
                "order-renewed",
                true,
                string.Empty,
                false,
                "base-plan",
                null,
                [],
                true,
                2.99m,
                "USD"));

        var result = await _controller.HandleNotification(JsonSerializer.SerializeToElement(payload));

        Assert.That(result, Is.InstanceOf<OkResult>());
        _mockSubscriptionService.Verify(service => service.UpdateGooglePlaySubscriptionStatusAsync(
            "token-renewed-after-trial",
            SubscriptionStatuses.Active,
            It.IsAny<DateTime?>(),
            It.Is<GooglePlaySubscriptionInfo>(info => info.SubscriptionState == "SUBSCRIPTION_STATE_ACTIVE" && info.RecurringPrice == 2.99m)), Times.Once);
    }
}