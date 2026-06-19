using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class CreatorEmailServiceTests
{
    private Mock<IEmailService> _mockEmailService;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<CreatorEmailService>> _mockLogger;
    private CreatorEmailService _service;
    private const string TestBaseUrl = "https://streamtunes.net";
    private const string TestUserEmail = "creator@example.com";
    private const string TestAdminEmail = "admin@streamtunes.net";

    [SetUp]
    public void SetUp()
    {
        _mockEmailService = new Mock<IEmailService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<CreatorEmailService>>();

        // Setup configuration defaults
        _mockConfiguration.Setup(c => c["EmailSettings:AdminEmail"]).Returns(TestAdminEmail);
        _mockConfiguration.Setup(c => c["EmailSettings:CustomerServiceEmail"]).Returns("customerservice@streamtunes.net");

        // Setup email service to return true by default
        _mockEmailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _mockEmailService.Setup(e => e.GetLogoUrl())
            .Returns("https://streamtunes.net/images/logo-light-small.png");

        _service = new CreatorEmailService(
            _mockEmailService.Object,
            _mockConfiguration.Object,
            _mockLogger.Object);
    }

    #region SendTaxFormReceivedEmailAsync Tests

    [Test]
    public async Task SendTaxFormReceivedEmailAsync_SendsEmailWithCorrectSubject_ForW9()
    {
        // Act
        var result = await _service.SendTaxFormReceivedEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            "Tax Form Received - Under Review",
            It.Is<string>(body => body.Contains("W-9"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormReceivedEmailAsync_SendsEmailWithCorrectSubject_ForW8()
    {
        // Act
        var result = await _service.SendTaxFormReceivedEmailAsync(TestUserEmail, TestBaseUrl, "W-8");

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            "Tax Form Received - Under Review",
            It.Is<string>(body => body.Contains("W-8"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormReceivedEmailAsync_IncludesLogo()
    {
        // Act
        await _service.SendTaxFormReceivedEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("/images/logo-light-small.png"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormReceivedEmailAsync_IncludesManageAccountLink()
    {
        // Act
        await _service.SendTaxFormReceivedEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("/manage-account"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormReceivedEmailAsync_ReturnsFalse_WhenEmailServiceFails()
    {
        // Arrange
        _mockEmailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        // Act
        var result = await _service.SendTaxFormReceivedEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        Assert.That(result, Is.False);
    }

    #endregion

    #region SendTaxFormProcessingErrorEmailAsync Tests

    [Test]
    public async Task SendTaxFormProcessingErrorEmailAsync_SendsBothUserAndAdminEmails()
    {
        // Act
        var result = await _service.SendTaxFormProcessingErrorEmailAsync(
            TestUserEmail, TestBaseUrl, "SUB-123", "Test error message");

        // Assert
        Assert.That(result, Is.True);

        // Verify user email was sent
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            "Issue Processing Your Tax Form",
            It.IsAny<string>()),
            Times.Once);

        // Verify admin email was sent
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            "Tax Form Processing Error - Action Required",
            It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormProcessingErrorEmailAsync_AdminEmailContainsSubmissionId()
    {
        // Act
        await _service.SendTaxFormProcessingErrorEmailAsync(
            TestUserEmail, TestBaseUrl, "SUB-123", "Test error message");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("SUB-123"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormProcessingErrorEmailAsync_AdminEmailContainsErrorDetails()
    {
        // Act
        await _service.SendTaxFormProcessingErrorEmailAsync(
            TestUserEmail, TestBaseUrl, "SUB-123", "Test error message");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("Test error message"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormProcessingErrorEmailAsync_UserEmailAsksToRequestNewForm()
    {
        // Act
        await _service.SendTaxFormProcessingErrorEmailAsync(
            TestUserEmail, TestBaseUrl, "SUB-123", "Test error message");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("Creator / Artist Settings") &&
                body.Contains("/CreatorSettings") &&
                body.Contains("Request a new tax form") &&
                body.Contains("/manage-account"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormProcessingErrorEmailAsync_ReturnsFalse_WhenOneEmailFails()
    {
        // Arrange - First call succeeds, second fails
        var callCount = 0;
        _mockEmailService.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount != 2;
            });

        // Act
        var result = await _service.SendTaxFormProcessingErrorEmailAsync(
            TestUserEmail, TestBaseUrl, "SUB-123", "Test error message");

        // Assert
        Assert.That(result, Is.False);
    }

    #endregion

    #region SendTaxFormFailedEmailAsync Tests

    [Test]
    public async Task SendTaxFormFailedEmailAsync_SendsEmailWithCorrectSubject_ForW9()
    {
        // Act
        var result = await _service.SendTaxFormFailedEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            "W-9 Form Submission Failed",
            It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormFailedEmailAsync_SendsEmailWithCorrectSubject_ForW8()
    {
        // Act
        var result = await _service.SendTaxFormFailedEmailAsync(TestUserEmail, TestBaseUrl, "W-8");

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            "W-8 Form Submission Failed",
            It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormFailedEmailAsync_IncludesFailureReason_WhenProvided()
    {
        // Act
        await _service.SendTaxFormFailedEmailAsync(TestUserEmail, TestBaseUrl, "W-9", "TIN verification failed");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("TIN verification failed"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormFailedEmailAsync_AsksUserToDoubleCheckInfo()
    {
        // Act
        await _service.SendTaxFormFailedEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("Double-check") || body.Contains("double-check"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormFailedEmailAsync_IncludesNextSteps()
    {
        // Act
        await _service.SendTaxFormFailedEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("Creator / Artist Settings") &&
                body.Contains("/CreatorSettings") &&
                body.Contains("Request a new tax form") &&
                body.Contains("/manage-account"))),
            Times.Once);
    }

    #endregion

    #region SendTaxFormSuccessEmailAsync Tests

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_SendsBothUserAndAdminEmails()
    {
        // Act
        var result = await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        Assert.That(result, Is.True);

        // Verify user email was sent
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            "StreamTunes - W-9 Form Processed",
            It.IsAny<string>()),
            Times.Once);

        // Verify admin email was sent
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            "New Creator Tax Form Completed",
            It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_UserEmailIsStatusUpdate()
    {
        // Act
        await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("W-9 Form Status"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_UserEmailIncludesNextSteps()
    {
        // Act
        await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            It.IsAny<string>(),
            It.Is<string>(body => 
                body.Contains("Creator / Artist Settings") &&
                body.Contains("/CreatorSettings") &&
                body.Contains("/manage-account") &&
                body.Contains("No further action is needed"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_UserEmailIncludesCustomerServiceContact()
    {
        // Act
        await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestUserEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("customerservice@streamtunes.net"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_AdminEmailContainsFormType_W9()
    {
        // Act
        await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("W-9"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_AdminEmailContainsFormType_W8()
    {
        // Act
        await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-8", "MX");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("W-8"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_AdminEmailContainsCountryName_ForW8()
    {
        // Act
        await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-8", "MX");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("Mexico"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_AdminEmailContainsCountryName_ForW8_Canada()
    {
        // Act
        await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-8", "CA");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("Canada"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_AdminEmailContainsCountryName_ForW8_Germany()
    {
        // Act
        await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-8", "DE");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("Germany"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_AdminEmailContainsCountryName_ForW8_UnitedKingdom()
    {
        // Act
        await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-8", "GB");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("United Kingdom"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_AdminEmailDoesNotContainCountry_ForW9()
    {
        // Act
        await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body => !body.Contains("<strong>Country:</strong>"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_AdminEmailContainsUserEmail()
    {
        // Act
        await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-9");

        // Assert
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains(TestUserEmail))),
            Times.Once);
    }

    #endregion

    #region Edge Cases

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_HandlesUnknownCountryCode()
    {
        // Act
        var result = await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-8", "ZZ");

        // Assert - Should still succeed, just return the code as-is
        Assert.That(result, Is.True);
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("ZZ"))),
            Times.Once);
    }

    [Test]
    public async Task SendTaxFormSuccessEmailAsync_HandlesNullCountryCode_ForW8()
    {
        // Act
        var result = await _service.SendTaxFormSuccessEmailAsync(TestUserEmail, TestBaseUrl, "W-8", null);

        // Assert - Should still succeed, just no country in admin email
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task SendTaxFormProcessingErrorEmailAsync_HandlesNullSubmissionId()
    {
        // Act
        var result = await _service.SendTaxFormProcessingErrorEmailAsync(
            TestUserEmail, TestBaseUrl, null, "Test error");

        // Assert
        Assert.That(result, Is.True);
        _mockEmailService.Verify(e => e.SendEmailAsync(
            TestAdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("N/A"))),
            Times.Once);
    }

    #endregion
}
