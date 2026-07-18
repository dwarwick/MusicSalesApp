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
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<SubscriptionController>> _mockLogger;
    private Mock<IHttpClientFactory> _mockHttpClientFactory;
    private Mock<ISubscriptionConfirmationEmailService> _mockSubscriptionConfirmationEmailService;
    private Mock<IAccountEmailService> _mockAccountEmailService;
    private Mock<IGooglePlayVerificationService> _mockGooglePlayService;
    private Mock<IPayPalSubscriptionManagementService> _mockPayPalSubscriptionManagementService;
    private SubscriptionController _controller;

    [SetUp]
    public void Setup()
    {
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<SubscriptionController>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockSubscriptionConfirmationEmailService = new Mock<ISubscriptionConfirmationEmailService>();
        _mockAccountEmailService = new Mock<IAccountEmailService>();
        _mockGooglePlayService = new Mock<IGooglePlayVerificationService>();
        _mockPayPalSubscriptionManagementService = new Mock<IPayPalSubscriptionManagementService>();

        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object, null, null, null, null, null, null, null, null);

        _controller = new SubscriptionController(
            _mockSubscriptionService.Object,
            _mockUserManager.Object,
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockHttpClientFactory.Object,
            _mockSubscriptionConfirmationEmailService.Object,
            _mockAccountEmailService.Object,
            _mockGooglePlayService.Object,
            _mockPayPalSubscriptionManagementService.Object);

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
        Assert.That(json, Does.Not.Contain("subscriptionPrice"));
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
    public async Task GetSubscriptionStatus_ReturnsCancelledGoogleTrialAccessThroughVerifiedExpiry()
    {
        var subscription = new Subscription
        {
            Id = 3,
            UserId = 1,
            Status = SubscriptionStatuses.Cancelled,
            BillingSource = BillingSources.GooglePlay,
            EndDate = DateTime.UtcNow.AddDays(7),
            TrialStartDate = DateTime.UtcNow.AddDays(-1),
            TrialEndDate = DateTime.UtcNow.AddDays(2),
            MonthlyPrice = 3.99m,
            GooglePlayPurchaseToken = "gp-token-456"
        };

        _mockSubscriptionService.Setup(s => s.GetActiveSubscriptionAsync(1)).ReturnsAsync(subscription);
        var result = await _controller.GetSubscriptionStatus();
        var ok = result as OkObjectResult;

        Assert.That(ok, Is.Not.Null);
        var json = System.Text.Json.JsonSerializer.Serialize(ok!.Value);
        Assert.That(json, Does.Contain("\"hasSubscription\":true"));
        Assert.That(json, Does.Contain("\"isOnTrial\":true"));
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
        var result = await _controller.GetSubscriptionStatus();
        var ok = result as OkObjectResult;

        Assert.That(ok, Is.Not.Null);
        var json = System.Text.Json.JsonSerializer.Serialize(ok!.Value);
        Assert.That(json, Does.Contain("\"hasSubscription\":false"));
        Assert.That(json, Does.Contain("\"status\":\"EXPIRED\""));
    }

    [Test]
    public async Task GetSubscriptionStatus_RefreshesGooglePlay_WhenLocalSubscriptionExpiredButGoogleIsActive()
    {
        var expiredSubscription = new Subscription
        {
            Id = 5,
            UserId = 1,
            Status = SubscriptionStatuses.Expired,
            BillingSource = BillingSources.GooglePlay,
            EndDate = DateTime.UtcNow.AddMinutes(-2),
            MonthlyPrice = 3.99m,
            GooglePlayPurchaseToken = "renewed-token"
        };
        var refreshedSubscription = new Subscription
        {
            Id = 5,
            UserId = 1,
            Status = SubscriptionStatuses.Active,
            BillingSource = BillingSources.GooglePlay,
            EndDate = DateTime.UtcNow.AddDays(30),
            MonthlyPrice = 2.99m,
            GooglePlayPurchaseToken = "renewed-token"
        };

        _mockSubscriptionService.SetupSequence(s => s.GetActiveSubscriptionAsync(1))
            .ReturnsAsync((Subscription)null)
            .ReturnsAsync(refreshedSubscription);
        _mockSubscriptionService.Setup(s => s.GetLatestSubscriptionAsync(1)).ReturnsAsync(expiredSubscription);
        _mockConfiguration.Setup(c => c["GooglePlay:SubscriptionProductId"]).Returns("streamtunes_monthly_sub");
        _mockGooglePlayService.Setup(g => g.VerifySubscriptionAsync("renewed-token", "streamtunes_monthly_sub"))
            .ReturnsAsync(new GooglePlaySubscriptionInfo(
                "SUBSCRIPTION_STATE_ACTIVE",
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddDays(30),
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

        var result = await _controller.GetSubscriptionStatus();
        var ok = result as OkObjectResult;

        Assert.That(ok, Is.Not.Null);
        var json = System.Text.Json.JsonSerializer.Serialize(ok!.Value);
        Assert.That(json, Does.Contain("\"hasSubscription\":true"));
        Assert.That(json, Does.Contain("\"monthlyPrice\":2.99"));
        _mockSubscriptionService.Verify(s => s.UpdateGooglePlaySubscriptionStatusAsync(
            "renewed-token",
            SubscriptionStatuses.Active,
            It.IsAny<DateTime?>(),
            It.Is<GooglePlaySubscriptionInfo>(info => info.RecurringPrice == 2.99m)), Times.Once);
    }

    [Test]
    public async Task CreateSubscription_PassesDisplayedOfferVersionToSharedPayPalManagementService()
    {
        _mockConfiguration.Setup(configuration => configuration["PayPal:ReturnBaseUrl"])
            .Returns("https://subscriptions.example");
        _mockPayPalSubscriptionManagementService.Setup(service => service.CreateSubscriptionAsync(
                It.Is<ApplicationUser>(user => user.Id == 1),
                true,
                27,
                "P-DISPLAYED",
                "https://subscriptions.example",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalCheckoutResult(
                true,
                "https://paypal.example/approve/I-NEW",
                SubscriptionId: "I-NEW"));

        var result = await _controller.CreateSubscription(new CreateSubscriptionRequest
        {
            AgreeToTerms = true,
            OfferVersion = 27,
            PlanId = "P-DISPLAYED"
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockPayPalSubscriptionManagementService.Verify(service => service.CreateSubscriptionAsync(
            It.Is<ApplicationUser>(user => user.Id == 1),
            true,
            27,
            "P-DISPLAYED",
            "https://subscriptions.example",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ActivateSubscription_ReturnsSuccessForActiveTrialWithoutLastPayment()
    {
        var trialEnd = DateTime.UtcNow.AddDays(3);
        var localSubscription = new Subscription
        {
            Id = 9,
            UserId = 1,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = "I-TRIAL",
            Status = SubscriptionStatuses.ApprovalPending
        };
        var activeTrial = new Subscription
        {
            Id = localSubscription.Id,
            UserId = localSubscription.UserId,
            BillingSource = BillingSources.PayPal,
            PayPalSubscriptionId = localSubscription.PayPalSubscriptionId,
            Status = SubscriptionStatuses.Active,
            TrialStartDate = DateTime.UtcNow,
            TrialEndDate = trialEnd,
            NextBillingDate = trialEnd
        };
        _mockSubscriptionService.Setup(service => service.GetSubscriptionByPayPalIdAsync("I-TRIAL"))
            .ReturnsAsync(localSubscription);
        _mockPayPalSubscriptionManagementService.Setup(service => service.ReconcileSubscriptionAsync(
                "I-TRIAL",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalSubscriptionReconciliationResult
            {
                Subscription = activeTrial,
                PreviousStatus = SubscriptionStatuses.ApprovalPending,
                BecameActive = true
            });

        var result = await _controller.ActivateSubscription(new ActivateSubscriptionRequest
        {
            SubscriptionId = "I-TRIAL"
        });

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var json = System.Text.Json.JsonSerializer.Serialize(ok!.Value);
        Assert.That(json, Does.Contain("\"isTrial\":true"));
        _mockPayPalSubscriptionManagementService.Verify(service => service.ReconcileSubscriptionAsync(
            "I-TRIAL",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task CancelSubscription_PayPal_RoutesThroughSharedManagementService()
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
        var entitlementEnd = DateTime.UtcNow.AddDays(3);
        _mockPayPalSubscriptionManagementService.Setup(service => service.CancelSubscriptionAsync(
                It.Is<ApplicationUser>(user => user.Id == 1),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PayPalCancellationResult(true, entitlementEnd));

        var result = await _controller.CancelSubscription();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockPayPalSubscriptionManagementService.Verify(service => service.CancelSubscriptionAsync(
            It.Is<ApplicationUser>(user => user.Id == 1),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockGooglePlayService.Verify(g => g.CancelSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockSubscriptionService.Verify(service => service.CancelSubscriptionAsync(
            It.IsAny<int>(),
            It.IsAny<DateTime?>()), Times.Never);
    }

    [Test]
    public async Task CancelSubscription_Apple_ReturnsBadRequest()
    {
        var subscription = new Subscription
        {
            Id = 3,
            UserId = 1,
            Status = "ACTIVE",
            BillingSource = BillingSources.Apple
        };
        _mockSubscriptionService.Setup(s => s.GetActiveSubscriptionAsync(1)).ReturnsAsync(subscription);

        var result = await _controller.CancelSubscription();
        var badRequest = result as BadRequestObjectResult;

        Assert.That(badRequest, Is.Not.Null);
        Assert.That(badRequest!.Value?.ToString(), Does.Contain("Apple subscriptions must be cancelled"));
        _mockSubscriptionService.Verify(s => s.CancelSubscriptionAsync(It.IsAny<int>()), Times.Never);
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
