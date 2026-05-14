using Microsoft.AspNetCore.Authorization;
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
using System.Security.Claims;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class AppleAppStoreSubscriptionControllerTests
{
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<IAppleAppStoreVerificationService> _mockVerificationService;
    private Mock<ISubscriptionConfirmationEmailService> _mockSubscriptionConfirmationEmailService;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<AppleAppStoreSubscriptionController>> _mockLogger;
    private AppleAppStoreSubscriptionController _controller;

    [SetUp]
    public void Setup()
    {
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockVerificationService = new Mock<IAppleAppStoreVerificationService>();
        _mockSubscriptionConfirmationEmailService = new Mock<ISubscriptionConfirmationEmailService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<AppleAppStoreSubscriptionController>>();

        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object, null, null, null, null, null, null, null, null);

        _controller = new AppleAppStoreSubscriptionController(
            _mockSubscriptionService.Object,
            _mockVerificationService.Object,
            _mockSubscriptionConfirmationEmailService.Object,
            _mockUserManager.Object,
            _mockConfiguration.Object,
            _mockLogger.Object);

        var user = new ApplicationUser { Id = 1, Email = "test@test.com", UserName = "testuser" };
        var claims = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "User")],
            "test"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claims }
        };

        _mockUserManager.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        _mockUserManager.Setup(m => m.UpdateAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(IdentityResult.Success);
        _mockConfiguration.Setup(c => c["AppleAppStore:SubscriptionProductId"]).Returns("streamtunes_monthly_sub_ios");
        _mockConfiguration.Setup(c => c["AppSettings:SubscriptionPrice"]).Returns("3.99");
        _mockConfiguration.Setup(c => c["BaseUrl"]).Returns("https://davidtest.dev");
    }

    [Test]
    public async Task VerifyAndRecordPurchase_ReturnsVerificationMessage_WhenVerificationThrows()
    {
        _mockVerificationService
            .Setup(v => v.VerifySubscriptionAsync("tx-123", "streamtunes_monthly_sub_ios"))
            .ThrowsAsync(new AppleAppStoreVerificationException("Apple App Store private key is not configured on the server."));

        var result = await _controller.VerifyAndRecordPurchase(new AppStorePurchaseRequest("tx-123", "account-token"));

        var badRequest = result as BadRequestObjectResult;
        Assert.That(badRequest, Is.Not.Null);

        var json = System.Text.Json.JsonSerializer.Serialize(badRequest!.Value);
        Assert.That(json, Does.Contain("Apple App Store private key is not configured on the server."));
    }

    [Test]
    public async Task VerifyAndRecordPurchase_CreatesAppleSubscription_WhenVerificationSucceeds()
    {
        var purchaseTime = DateTime.UtcNow.AddMinutes(-2);

        _mockVerificationService
            .Setup(v => v.VerifySubscriptionAsync("tx-123", "streamtunes_monthly_sub_ios"))
            .ReturnsAsync(new AppleAppStoreSubscriptionInfo(
                "ACTIVE",
                DateTime.UtcNow.AddDays(30),
                purchaseTime,
                "tx-123",
                "orig-123",
                "streamtunes_monthly_sub_ios",
                "Sandbox",
                "account-token"));
        _mockSubscriptionService
            .SetupSequence(s => s.GetSubscriptionByAppleOriginalTransactionIdAsync("orig-123"))
            .ReturnsAsync((Subscription)null)
            .ReturnsAsync(new Subscription
            {
                Id = 17,
                UserId = 1,
                BillingSource = "Apple",
                Status = "ACTIVE",
                AppStoreOriginalTransactionId = "orig-123",
                NextBillingDate = DateTime.UtcNow.AddDays(30),
                EndDate = DateTime.UtcNow.AddDays(30)
            });
        _mockSubscriptionService
            .Setup(s => s.CreateAppleSubscriptionAsync(1, "tx-123", "orig-123", "streamtunes_monthly_sub_ios", "account-token", "Sandbox", 3.99m, purchaseTime))
            .ReturnsAsync(new Subscription { Id = 17, UserId = 1, BillingSource = "Apple", Status = "ACTIVE" });

        var result = await _controller.VerifyAndRecordPurchase(new AppStorePurchaseRequest("tx-123", "account-token"));

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        _mockSubscriptionService.Verify(s => s.CreateAppleSubscriptionAsync(1, "tx-123", "orig-123", "streamtunes_monthly_sub_ios", "account-token", "Sandbox", 3.99m, purchaseTime), Times.Once);
        _mockSubscriptionService.Verify(s => s.UpdateAppleSubscriptionStatusAsync(
            "orig-123",
            "ACTIVE",
            It.IsAny<DateTime?>(),
            "tx-123",
            "Sandbox",
            "streamtunes_monthly_sub_ios",
            "account-token",
            3.99m), Times.Once);
        _mockSubscriptionConfirmationEmailService.Verify(s => s.SendConfirmationAsync(
            It.IsAny<ApplicationUser>(),
            It.Is<Subscription>(subscription => subscription.AppStoreOriginalTransactionId == "orig-123" && subscription.NextBillingDate.HasValue),
            "https://davidtest.dev"), Times.Once);
    }

    [Test]
    public async Task VerifyAndRecordPurchase_ResendsConfirmationEmail_WhenSubscriptionReactivates()
    {
        var existingSubscription = new Subscription
        {
            Id = 17,
            UserId = 1,
            BillingSource = "Apple",
            Status = "CANCELLED",
            AppStoreOriginalTransactionId = "orig-123"
        };

        _mockVerificationService
            .Setup(v => v.VerifySubscriptionAsync("tx-123", "streamtunes_monthly_sub_ios"))
            .ReturnsAsync(new AppleAppStoreSubscriptionInfo(
                "ACTIVE",
                DateTime.UtcNow.AddDays(30),
                DateTime.UtcNow.AddMinutes(-2),
                "tx-123",
                "orig-123",
                "streamtunes_monthly_sub_ios",
                "Sandbox",
                "account-token"));
        _mockSubscriptionService
            .SetupSequence(s => s.GetSubscriptionByAppleOriginalTransactionIdAsync("orig-123"))
            .ReturnsAsync(existingSubscription)
            .ReturnsAsync(existingSubscription);

        var result = await _controller.VerifyAndRecordPurchase(new AppStorePurchaseRequest("tx-123", "account-token"));

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockSubscriptionService.Verify(s => s.UpdateAppleSubscriptionStatusAsync(
            "orig-123",
            "ACTIVE",
            It.IsAny<DateTime?>(),
            "tx-123",
            "Sandbox",
            "streamtunes_monthly_sub_ios",
            "account-token",
            3.99m), Times.Once);
        _mockSubscriptionConfirmationEmailService.Verify(s => s.SendConfirmationAsync(It.IsAny<ApplicationUser>(), existingSubscription, "https://davidtest.dev"), Times.Once);
        _mockSubscriptionService.Verify(s => s.CreateAppleSubscriptionAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<decimal>()), Times.Never);
    }

    [Test]
    public async Task VerifyAndRecordPurchase_DoesNotResendConfirmationEmail_WhenSubscriptionAlreadyActive()
    {
        var existingSubscription = new Subscription
        {
            Id = 17,
            UserId = 1,
            BillingSource = "Apple",
            Status = SubscriptionStatuses.Active,
            AppStoreOriginalTransactionId = "orig-123"
        };

        _mockVerificationService
            .Setup(v => v.VerifySubscriptionAsync("tx-123", "streamtunes_monthly_sub_ios"))
            .ReturnsAsync(new AppleAppStoreSubscriptionInfo(
                "ACTIVE",
                DateTime.UtcNow.AddDays(30),
                DateTime.UtcNow.AddMinutes(-2),
                "tx-123",
                "orig-123",
                "streamtunes_monthly_sub_ios",
                "Sandbox",
                "account-token"));
        _mockSubscriptionService
            .Setup(s => s.GetSubscriptionByAppleOriginalTransactionIdAsync("orig-123"))
            .ReturnsAsync(existingSubscription);

        var result = await _controller.VerifyAndRecordPurchase(new AppStorePurchaseRequest("tx-123", "account-token"));

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockSubscriptionService.Verify(s => s.UpdateAppleSubscriptionStatusAsync(
            "orig-123",
            "ACTIVE",
            It.IsAny<DateTime?>(),
            "tx-123",
            "Sandbox",
            "streamtunes_monthly_sub_ios",
            "account-token",
            3.99m), Times.Once);
        _mockSubscriptionConfirmationEmailService.Verify(s => s.SendConfirmationAsync(It.IsAny<ApplicationUser>(), It.IsAny<Subscription>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task VerifyAndRecordPurchase_PersistsProvidedTimeZone_WhenRequestIncludesIt()
    {
        var existingSubscription = new Subscription
        {
            Id = 17,
            UserId = 1,
            BillingSource = "Apple",
            Status = "ACTIVE",
            AppStoreOriginalTransactionId = "orig-123"
        };

        _mockVerificationService
            .Setup(v => v.VerifySubscriptionAsync("tx-123", "streamtunes_monthly_sub_ios"))
            .ReturnsAsync(new AppleAppStoreSubscriptionInfo(
                "ACTIVE",
                DateTime.UtcNow.AddDays(30),
                DateTime.UtcNow.AddMinutes(-2),
                "tx-123",
                "orig-123",
                "streamtunes_monthly_sub_ios",
                "Sandbox",
                "account-token"));
        _mockSubscriptionService
            .SetupSequence(s => s.GetSubscriptionByAppleOriginalTransactionIdAsync("orig-123"))
            .ReturnsAsync(existingSubscription)
            .ReturnsAsync(existingSubscription);

        await _controller.VerifyAndRecordPurchase(new AppStorePurchaseRequest("tx-123", "account-token", "America/Los_Angeles"));

        _mockUserManager.Verify(m => m.UpdateAsync(It.Is<ApplicationUser>(user => user.TimeZoneId == "America/Los_Angeles")), Times.Once);
    }

    [Test]
    public void VerifyAndRecordPurchase_UsesCookieAndBearerAuthenticationSchemes()
    {
        var method = typeof(AppleAppStoreSubscriptionController).GetMethod(nameof(AppleAppStoreSubscriptionController.VerifyAndRecordPurchase));

        var authorizeAttribute = method?.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.That(authorizeAttribute, Is.Not.Null);
        Assert.That(authorizeAttribute.Roles, Is.EqualTo("Admin,User"));
        Assert.That(authorizeAttribute.AuthenticationSchemes, Is.EqualTo("Identity.Application,Bearer"));
    }
}