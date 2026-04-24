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
public class GooglePlaySubscriptionTests
{
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<IAppSettingsService> _mockAppSettingsService;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<SubscriptionController>> _mockLogger;
    private Mock<IHttpClientFactory> _mockHttpClientFactory;
    private Mock<IPurchaseEmailService> _mockPurchaseEmailService;
    private Mock<IAccountEmailService> _mockAccountEmailService;
    private Mock<IGooglePlayVerificationService> _mockGooglePlayService;
    private SubscriptionController _controller;

    [SetUp]
    public void Setup()
    {
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockAppSettingsService = new Mock<IAppSettingsService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<SubscriptionController>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockPurchaseEmailService = new Mock<IPurchaseEmailService>();
        _mockAccountEmailService = new Mock<IAccountEmailService>();
        _mockGooglePlayService = new Mock<IGooglePlayVerificationService>();

        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object, null, null, null, null, null, null, null, null);

        _controller = new SubscriptionController(
            _mockSubscriptionService.Object,
            _mockAppSettingsService.Object,
            _mockUserManager.Object,
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockPurchaseEmailService.Object,
            _mockAccountEmailService.Object,
            _mockGooglePlayService.Object);

        // Set up authenticated user context
        var user = new ApplicationUser { Id = 1, UserName = "testuser", Email = "test@test.com" };
        var claims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Name, "testuser")
        }, "test"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claims }
        };

        _mockUserManager.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(user);
    }

    // --- Status endpoint ---

    [Test]
    public async Task GetSubscriptionStatus_ReturnsPayPalBillingSource()
    {
        var subscription = new Subscription
        {
            Id = 1,
            UserId = 1,
            Status = "ACTIVE",
            BillingSource = BillingSources.PayPal,
            StartDate = DateTime.UtcNow.AddDays(-10),
            MonthlyPrice = 3.99m,
            PayPalSubscriptionId = "PP-123"
        };
        _mockSubscriptionService.Setup(s => s.GetActiveSubscriptionAsync(1)).ReturnsAsync(subscription);

        var result = await _controller.GetSubscriptionStatus();
        var ok = result as OkObjectResult;

        Assert.That(ok, Is.Not.Null);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.That(json, Does.Contain("\"billingSource\":\"PayPal\""));
    }

    [Test]
    public async Task GetSubscriptionStatus_ReturnsGooglePlayBillingSource()
    {
        var subscription = new Subscription
        {
            Id = 2,
            UserId = 1,
            Status = "ACTIVE",
            BillingSource = BillingSources.GooglePlay,
            StartDate = DateTime.UtcNow.AddDays(-5),
            MonthlyPrice = 3.99m,
            GooglePlayPurchaseToken = "gp-token-123"
        };
        _mockSubscriptionService.Setup(s => s.GetActiveSubscriptionAsync(1)).ReturnsAsync(subscription);

        var result = await _controller.GetSubscriptionStatus();
        var ok = result as OkObjectResult;

        Assert.That(ok, Is.Not.Null);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.That(json, Does.Contain("\"billingSource\":\"GooglePlay\""));
    }

    [Test]
    public async Task GetSubscriptionStatus_ReturnsCancelledAccessDetails_WhenCancelledButStillInsideBillingPeriod()
    {
        var subscription = new Subscription
        {
            Id = 3,
            UserId = 1,
            Status = SubscriptionStatuses.Cancelled,
            BillingSource = BillingSources.GooglePlay,
            EndDate = DateTime.UtcNow.AddDays(7),
            MonthlyPrice = 3.99m,
            GooglePlayPurchaseToken = "gp-token-456"
        };

        _mockSubscriptionService.Setup(s => s.GetActiveSubscriptionAsync(1)).ReturnsAsync(subscription);
        _mockAppSettingsService.Setup(s => s.GetSubscriptionPriceAsync()).ReturnsAsync(3.99m);

        var result = await _controller.GetSubscriptionStatus();
        var ok = result as OkObjectResult;

        Assert.That(ok, Is.Not.Null);
        var json = System.Text.Json.JsonSerializer.Serialize(ok!.Value);
        Assert.That(json, Does.Contain("\"hasSubscription\":true"));
        Assert.That(json, Does.Contain("\"status\":\"CANCELLED\""));
        Assert.That(json, Does.Contain("\"billingSource\":\"GooglePlay\""));
    }

    [Test]
    public async Task GetSubscriptionStatus_ReturnsExpiredStatusWithoutAccess_WhenLatestSubscriptionExpired()
    {
        var subscription = new Subscription
        {
            Id = 4,
            UserId = 1,
            Status = SubscriptionStatuses.Expired,
            BillingSource = BillingSources.GooglePlay,
            EndDate = DateTime.UtcNow.AddDays(-1),
            MonthlyPrice = 3.99m,
            GooglePlayPurchaseToken = "gp-token-789"
        };

        _mockSubscriptionService.Setup(s => s.GetActiveSubscriptionAsync(1)).ReturnsAsync((Subscription)null);
        _mockSubscriptionService.Setup(s => s.GetLatestSubscriptionAsync(1)).ReturnsAsync(subscription);
        _mockAppSettingsService.Setup(s => s.GetSubscriptionPriceAsync()).ReturnsAsync(3.99m);

        var result = await _controller.GetSubscriptionStatus();
        var ok = result as OkObjectResult;

        Assert.That(ok, Is.Not.Null);
        var json = System.Text.Json.JsonSerializer.Serialize(ok!.Value);
        Assert.That(json, Does.Contain("\"hasSubscription\":false"));
        Assert.That(json, Does.Contain("\"status\":\"EXPIRED\""));
    }

    // --- Cancel routing ---

    [Test]
    public async Task CancelSubscription_GooglePlay_CallsGooglePlayCancel()
    {
        var subscription = new Subscription
        {
            Id = 2,
            UserId = 1,
            Status = "ACTIVE",
            BillingSource = BillingSources.GooglePlay,
            GooglePlayPurchaseToken = "gp-token-123"
        };
        _mockSubscriptionService.Setup(s => s.GetActiveSubscriptionAsync(1)).ReturnsAsync(subscription);
        _mockGooglePlayService.Setup(g => g.CancelSubscriptionAsync("gp-token-123", It.IsAny<string>())).ReturnsAsync(true);
        _mockSubscriptionService.Setup(s => s.CancelSubscriptionAsync(1)).ReturnsAsync(true);
        _mockConfiguration.Setup(c => c["GooglePlay:SubscriptionProductId"]).Returns("streamtunes_monthly_sub");

        var result = await _controller.CancelSubscription();
        var ok = result as OkObjectResult;

        Assert.That(ok, Is.Not.Null);
        _mockGooglePlayService.Verify(g => g.CancelSubscriptionAsync("gp-token-123", "streamtunes_monthly_sub"), Times.Once);
    }

    [Test]
    public async Task CancelSubscription_GooglePlayFails_ReturnsBadRequest()
    {
        var subscription = new Subscription
        {
            Id = 2,
            UserId = 1,
            Status = "ACTIVE",
            BillingSource = BillingSources.GooglePlay,
            GooglePlayPurchaseToken = "gp-token-123"
        };
        _mockSubscriptionService.Setup(s => s.GetActiveSubscriptionAsync(1)).ReturnsAsync(subscription);
        _mockGooglePlayService.Setup(g => g.CancelSubscriptionAsync("gp-token-123", It.IsAny<string>())).ReturnsAsync(false);
        _mockConfiguration.Setup(c => c["GooglePlay:SubscriptionProductId"]).Returns("streamtunes_monthly_sub");

        var result = await _controller.CancelSubscription();

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CancelSubscription_PayPal_DoesNotCallGooglePlay()
    {
        var subscription = new Subscription
        {
            Id = 1,
            UserId = 1,
            Status = "ACTIVE",
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "PP-123"
        };
        _mockSubscriptionService.Setup(s => s.GetActiveSubscriptionAsync(1)).ReturnsAsync(subscription);
        // PayPal cancel requires HTTP calls we don't set up, so it will fail — that's fine,
        // the important thing is that Google Play was NOT called
        _mockSubscriptionService.Setup(s => s.CancelSubscriptionAsync(1)).ReturnsAsync(true);

        await _controller.CancelSubscription();

        _mockGooglePlayService.Verify(g => g.CancelSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task CancelSubscription_Apple_SucceedsLocally()
    {
        var subscription = new Subscription
        {
            Id = 3,
            UserId = 1,
            Status = "ACTIVE",
            BillingSource = BillingSources.Apple
        };
        _mockSubscriptionService.Setup(s => s.GetActiveSubscriptionAsync(1)).ReturnsAsync(subscription);
        _mockSubscriptionService.Setup(s => s.CancelSubscriptionAsync(1)).ReturnsAsync(true);

        var result = await _controller.CancelSubscription();
        var ok = result as OkObjectResult;

        Assert.That(ok, Is.Not.Null);
        // Apple cancellation succeeds locally (user must manage in iOS Settings)
        _mockGooglePlayService.Verify(g => g.CancelSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task CancelSubscription_NoSubscription_ReturnsBadRequest()
    {
        _mockSubscriptionService.Setup(s => s.GetActiveSubscriptionAsync(1)).ReturnsAsync((Subscription)null);

        var result = await _controller.CancelSubscription();

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }
}
