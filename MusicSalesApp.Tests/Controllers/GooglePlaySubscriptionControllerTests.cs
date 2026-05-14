using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
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
public class GooglePlaySubscriptionControllerTests
{
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<IGooglePlayVerificationService> _mockVerificationService;
    private Mock<ISubscriptionConfirmationEmailService> _mockSubscriptionConfirmationEmailService;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<GooglePlaySubscriptionController>> _mockLogger;
    private GooglePlaySubscriptionController _controller;

    [SetUp]
    public void Setup()
    {
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockVerificationService = new Mock<IGooglePlayVerificationService>();
        _mockSubscriptionConfirmationEmailService = new Mock<ISubscriptionConfirmationEmailService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<GooglePlaySubscriptionController>>();

        var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            userStoreMock.Object, null, null, null, null, null, null, null, null);

        _controller = new GooglePlaySubscriptionController(
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
        _mockConfiguration.Setup(c => c["GooglePlay:SubscriptionProductId"]).Returns("streamtunes_monthly_sub");
        _mockConfiguration.Setup(c => c["AppSettings:SubscriptionPrice"]).Returns("3.99");
        _mockConfiguration.Setup(c => c["BaseUrl"]).Returns("https://davidtest.dev");
    }

    [Test]
    public async Task VerifyAndRecordPurchase_SendsConfirmationEmail_WhenVerificationSucceeds()
    {
        _mockVerificationService
            .Setup(v => v.VerifySubscriptionAsync("purchase-token", "streamtunes_monthly_sub"))
            .ReturnsAsync(new GooglePlaySubscriptionInfo(
                "SUBSCRIPTION_STATE_ACTIVE",
                DateTimeOffset.UtcNow.AddDays(30),
                "order-123",
                true,
                "linked-token"));
        _mockSubscriptionService
            .Setup(s => s.GetSubscriptionByGooglePlayTokenAsync("purchase-token"))
            .ReturnsAsync((Subscription)null);
        _mockSubscriptionService
            .Setup(s => s.CreateGooglePlaySubscriptionAsync(1, "purchase-token", "order-123", 3.99m))
            .ReturnsAsync(new Subscription { Id = 12, BillingSource = BillingSources.GooglePlay, GooglePlayOrderId = "order-123", Status = SubscriptionStatuses.Active });

        var result = await _controller.VerifyAndRecordPurchase(new GooglePlayPurchaseRequest("purchase-token", "order-123"));

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockSubscriptionConfirmationEmailService.Verify(s => s.SendConfirmationAsync(It.IsAny<ApplicationUser>(), It.IsAny<Subscription>(), "https://davidtest.dev"), Times.Once);
    }

    [Test]
    public async Task VerifyAndRecordPurchase_ResendsConfirmationEmail_WhenSubscriptionAlreadyExists()
    {
        var existingSubscription = new Subscription
        {
            Id = 12,
            BillingSource = BillingSources.GooglePlay,
            GooglePlayOrderId = "order-123",
            GooglePlayPurchaseToken = "purchase-token",
            Status = SubscriptionStatuses.Cancelled
        };

        _mockVerificationService
            .Setup(v => v.VerifySubscriptionAsync("purchase-token", "streamtunes_monthly_sub"))
            .ReturnsAsync(new GooglePlaySubscriptionInfo(
                "SUBSCRIPTION_STATE_ACTIVE",
                DateTimeOffset.UtcNow.AddDays(30),
                "order-123",
                true,
                "linked-token"));
        _mockSubscriptionService
            .SetupSequence(s => s.GetSubscriptionByGooglePlayTokenAsync("purchase-token"))
            .ReturnsAsync(existingSubscription)
            .ReturnsAsync(existingSubscription);

        var result = await _controller.VerifyAndRecordPurchase(new GooglePlayPurchaseRequest("purchase-token", "order-123"));

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockSubscriptionService.Verify(s => s.UpdateGooglePlaySubscriptionStatusAsync(
            "purchase-token",
            SubscriptionStatuses.Active,
            It.IsAny<DateTime?>()), Times.Once);
        _mockSubscriptionConfirmationEmailService.Verify(s => s.SendConfirmationAsync(It.IsAny<ApplicationUser>(), existingSubscription, "https://davidtest.dev"), Times.Once);
        _mockSubscriptionService.Verify(s => s.CreateGooglePlaySubscriptionAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<decimal>()), Times.Never);
    }

    [Test]
    public async Task VerifyAndRecordPurchase_ReturnsVerificationMessage_WhenVerificationThrows()
    {
        _mockVerificationService
            .Setup(v => v.VerifySubscriptionAsync("purchase-token", "streamtunes_monthly_sub"))
            .ThrowsAsync(new GooglePlayVerificationException("Configured Google Play service account key file was not found on the server."));

        var result = await _controller.VerifyAndRecordPurchase(new GooglePlayPurchaseRequest("purchase-token", "order-123"));

        var badRequest = result as BadRequestObjectResult;
        Assert.That(badRequest, Is.Not.Null);

        var json = System.Text.Json.JsonSerializer.Serialize(badRequest!.Value);
        Assert.That(json, Does.Contain("Configured Google Play service account key file was not found on the server."));
    }

    [Test]
    public void VerifyAndRecordPurchase_UsesCookieAndBearerAuthenticationSchemes()
    {
        var method = typeof(GooglePlaySubscriptionController).GetMethod(nameof(GooglePlaySubscriptionController.VerifyAndRecordPurchase));

        var authorizeAttribute = method?.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.That(authorizeAttribute, Is.Not.Null);
        Assert.That(authorizeAttribute!.Roles, Is.EqualTo("Admin,User"));
        Assert.That(authorizeAttribute.AuthenticationSchemes, Is.EqualTo("Identity.Application,Bearer"));
    }
}