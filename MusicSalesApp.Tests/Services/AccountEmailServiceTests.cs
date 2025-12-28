using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AccountEmailServiceTests
{
    private Mock<IEmailService> _mockEmailService;
    private Mock<ILogger<AccountEmailService>> _mockLogger;
    private AccountEmailService _service;

    [SetUp]
    public void SetUp()
    {
        _mockEmailService = new Mock<IEmailService>();
        _mockLogger = new Mock<ILogger<AccountEmailService>>();

        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _service = new AccountEmailService(
            _mockEmailService.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task SendAccountCreatedEmailAsync_SendsEmailWithCorrectDetails()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendAccountCreatedEmailAsync(userEmail, userName, baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.Is<string>(s => s.Contains("Welcome")),
                It.Is<string>(body =>
                    body.Contains("logo-light-small.png") &&
                    body.Contains("Welcome to StreamTunes") &&
                    body.Contains("Test User") &&
                    body.Contains("successfully created"))),
            Times.Once);
    }

    [Test]
    public async Task SendAccountClosedEmailAsync_SendsEmailWithCorrectDetails()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendAccountClosedEmailAsync(userEmail, userName, baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.Is<string>(s => s.Contains("Suspended")),
                It.Is<string>(body =>
                    body.Contains("logo-light-small.png") &&
                    body.Contains("Account Suspended") &&
                    body.Contains("Test User") &&
                    body.Contains("not be able to log in") &&
                    body.Contains("reactivate your account"))),
            Times.Once);
    }

    [Test]
    public async Task SendPasswordChangedEmailAsync_SendsEmailWithCorrectDetails()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendPasswordChangedEmailAsync(userEmail, userName, baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.Is<string>(s => s.Contains("Password Changed")),
                It.Is<string>(body =>
                    body.Contains("logo-light-small.png") &&
                    body.Contains("Password Changed") &&
                    body.Contains("Test User") &&
                    body.Contains("successfully changed") &&
                    body.Contains("If you did not make this change"))),
            Times.Once);
    }

    [Test]
    public async Task SendAccountDeletedEmailAsync_SendsEmailWithCorrectDetails()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendAccountDeletedEmailAsync(userEmail, userName, baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.Is<string>(s => s.Contains("Account Deleted")),
                It.Is<string>(body =>
                    body.Contains("logo-light-small.png") &&
                    body.Contains("Account Deleted") &&
                    body.Contains("Test User") &&
                    body.Contains("permanently deleted") &&
                    body.Contains("permanently removed from our systems"))),
            Times.Once);
    }

    [Test]
    public async Task SendSubscriptionCancelledEmailAsync_SendsEmailWithCorrectDetails()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var endDate = DateTime.UtcNow.AddMonths(1);
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendSubscriptionCancelledEmailAsync(userEmail, userName, endDate, baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.Is<string>(s => s.Contains("Subscription Cancelled")),
                It.Is<string>(body =>
                    body.Contains("logo-light-small.png") &&
                    body.Contains("Subscription Cancelled") &&
                    body.Contains("Test User") &&
                    body.Contains("subscription will remain active") &&
                    body.Contains("resubscribe at any time"))),
            Times.Once);
    }

    [Test]
    public async Task SendSubscriptionCancelledEmailAsync_WithNullEndDate_ShowsFallbackText()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        DateTime? endDate = null;
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendSubscriptionCancelledEmailAsync(userEmail, userName, endDate, baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("end of your billing period"))),
            Times.Once);
    }

    [Test]
    public async Task SendAccountCreatedEmailAsync_WhenEmailServiceFails_ReturnsFalse()
    {
        // Arrange
        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var userEmail = "test@example.com";
        var userName = "Test User";
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendAccountCreatedEmailAsync(userEmail, userName, baseUrl);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task SendAccountClosedEmailAsync_WhenEmailServiceFails_ReturnsFalse()
    {
        // Arrange
        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var userEmail = "test@example.com";
        var userName = "Test User";
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendAccountClosedEmailAsync(userEmail, userName, baseUrl);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task SendPasswordChangedEmailAsync_WhenEmailServiceFails_ReturnsFalse()
    {
        // Arrange
        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var userEmail = "test@example.com";
        var userName = "Test User";
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendPasswordChangedEmailAsync(userEmail, userName, baseUrl);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task SendAccountDeletedEmailAsync_WhenEmailServiceFails_ReturnsFalse()
    {
        // Arrange
        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var userEmail = "test@example.com";
        var userName = "Test User";
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendAccountDeletedEmailAsync(userEmail, userName, baseUrl);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task SendSubscriptionCancelledEmailAsync_WhenEmailServiceFails_ReturnsFalse()
    {
        // Arrange
        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var userEmail = "test@example.com";
        var userName = "Test User";
        var endDate = DateTime.UtcNow.AddMonths(1);
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendSubscriptionCancelledEmailAsync(userEmail, userName, endDate, baseUrl);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task SendAccountCreatedEmailAsync_WithNullUserName_UsesValuedCustomer()
    {
        // Arrange
        var userEmail = "test@example.com";
        string userName = null!;
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendAccountCreatedEmailAsync(userEmail, userName, baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("Valued Customer"))),
            Times.Once);
    }

    [Test]
    public async Task SendAccountCreatedEmailAsync_WithEmptyUserName_UsesValuedCustomer()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "";
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendAccountCreatedEmailAsync(userEmail, userName, baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("Valued Customer"))),
            Times.Once);
    }

    [Test]
    public async Task SendAccountCreatedEmailAsync_EscapesHtmlInUserName()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "<script>alert('xss')</script>";
        var baseUrl = "https://streamtunes.net";

        // Act
        var result = await _service.SendAccountCreatedEmailAsync(userEmail, userName, baseUrl);

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                userEmail,
                It.IsAny<string>(),
                It.Is<string>(body => 
                    body.Contains("&lt;script&gt;") && 
                    !body.Contains("<script>"))),
            Times.Once);
    }

    [Test]
    public async Task AllEmailMethods_IncludeLogoUrl()
    {
        // Arrange
        var userEmail = "test@example.com";
        var userName = "Test User";
        var baseUrl = "https://streamtunes.net";
        var endDate = DateTime.UtcNow.AddMonths(1);

        // Act & Assert - Account Created
        await _service.SendAccountCreatedEmailAsync(userEmail, userName, baseUrl);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("images/logo-light-small.png"))),
            Times.Once);

        // Reset
        _mockEmailService.Invocations.Clear();

        // Act & Assert - Account Closed
        await _service.SendAccountClosedEmailAsync(userEmail, userName, baseUrl);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("images/logo-light-small.png"))),
            Times.Once);

        // Reset
        _mockEmailService.Invocations.Clear();

        // Act & Assert - Password Changed
        await _service.SendPasswordChangedEmailAsync(userEmail, userName, baseUrl);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("images/logo-light-small.png"))),
            Times.Once);

        // Reset
        _mockEmailService.Invocations.Clear();

        // Act & Assert - Account Deleted
        await _service.SendAccountDeletedEmailAsync(userEmail, userName, baseUrl);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("images/logo-light-small.png"))),
            Times.Once);

        // Reset
        _mockEmailService.Invocations.Clear();

        // Act & Assert - Subscription Cancelled
        await _service.SendSubscriptionCancelledEmailAsync(userEmail, userName, endDate, baseUrl);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("images/logo-light-small.png"))),
            Times.Once);
    }
}
