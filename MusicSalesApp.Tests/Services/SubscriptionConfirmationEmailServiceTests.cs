#nullable enable
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class SubscriptionConfirmationEmailServiceTests
{
    private Mock<IPurchaseEmailService> _mockPurchaseEmailService;
    private Mock<ILogger<SubscriptionConfirmationEmailService>> _mockLogger;
    private SubscriptionConfirmationEmailService _service;

    [SetUp]
    public void Setup()
    {
        _mockPurchaseEmailService = new Mock<IPurchaseEmailService>();
        _mockLogger = new Mock<ILogger<SubscriptionConfirmationEmailService>>();
        _service = new SubscriptionConfirmationEmailService(_mockPurchaseEmailService.Object, _mockLogger.Object);
    }

    [Test]
    public async Task SendConfirmationAsync_UsesAppleOriginalTransactionId_ForAppleSubscriptions()
    {
        var user = new ApplicationUser { Id = 22, Email = "test@example.com", UserName = "tester", TimeZoneId = "America/New_York" };
        var subscription = new Subscription
        {
            BillingSource = BillingSources.Apple,
            AppStoreOriginalTransactionId = "orig-123"
        };

        _mockPurchaseEmailService
            .Setup(service => service.SendSubscriptionConfirmationAsync("test@example.com", "tester", subscription, "orig-123", "America/New_York", "https://davidtest.dev"))
            .ReturnsAsync(true);

        var result = await _service.SendConfirmationAsync(user, subscription, "https://davidtest.dev");

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task SendConfirmationAsync_ReturnsFalse_WhenExternalReferenceIsMissing()
    {
        var user = new ApplicationUser { Id = 22, Email = "test@example.com", UserName = "tester" };
        var subscription = new Subscription
        {
            BillingSource = BillingSources.Apple
        };

        var result = await _service.SendConfirmationAsync(user, subscription, "https://davidtest.dev");

        Assert.That(result, Is.False);
        _mockPurchaseEmailService.Verify(
            service => service.SendSubscriptionConfirmationAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Subscription>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task SendTrialStartedAsync_UsesGooglePlayOrderId()
    {
        var user = new ApplicationUser { Id = 22, Email = "test@example.com", UserName = "tester", TimeZoneId = "America/New_York" };
        var subscription = new Subscription
        {
            BillingSource = BillingSources.GooglePlay,
            GooglePlayOrderId = "gpa-order-123",
            GooglePlayPurchaseToken = "token-123"
        };

        _mockPurchaseEmailService
            .Setup(service => service.SendSubscriptionTrialStartedAsync("test@example.com", "tester", subscription, "gpa-order-123", "America/New_York", "https://davidtest.dev"))
            .ReturnsAsync(true);

        var result = await _service.SendTrialStartedAsync(user, subscription, "https://davidtest.dev");

        Assert.That(result, Is.True);
    }
}