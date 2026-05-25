using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class ContactRequestEmailServiceTests
{
    private const string UserEmail = "listener@example.com";
    private const string AdminEmail = "admin@streamtunes.net";
    private const string CustomerServiceEmail = "customerservice@streamtunes.net";

    private Mock<IEmailService> _mockEmailService;
    private ContactRequestEmailService _service;

    [SetUp]
    public void SetUp()
    {
        _mockEmailService = new Mock<IEmailService>();
        _mockEmailService
            .Setup(service => service.GetEmailLogoHtml())
            .Returns("<img src='logo.png' alt='StreamTunes Logo' />");
        _mockEmailService
            .Setup(service => service.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["EmailSettings:AdminEmail"] = AdminEmail,
                ["EmailSettings:CustomerServiceEmail"] = CustomerServiceEmail
            })
            .Build();

        _service = new ContactRequestEmailService(
            _mockEmailService.Object,
            configuration,
            Mock.Of<ILogger<ContactRequestEmailService>>());
    }

    [Test]
    public async Task SendContactRequestEmailsAsync_SendsUserReceiptAndAdminCopy()
    {
        var result = await _service.SendContactRequestEmailsAsync(
            UserEmail,
            "Bug Report",
            "The play button stopped working.");

        Assert.That(result.Success, Is.True);
        _mockEmailService.Verify(service => service.SendEmailAsync(
            UserEmail,
            "StreamTunes - We Received Your Message",
            It.Is<string>(body =>
                body.Contains("usually within 48 hours") &&
                body.Contains("Bug Report") &&
                body.Contains("The play button stopped working.") &&
                body.Contains(CustomerServiceEmail))), Times.Once);

        _mockEmailService.Verify(service => service.SendEmailAsync(
            AdminEmail,
            "StreamTunes Admin - Contact Form: Bug Report",
            It.Is<string>(body =>
                body.Contains(UserEmail) &&
                body.Contains("Bug Report") &&
                body.Contains("The play button stopped working."))), Times.Once);
    }

    [Test]
    public async Task SendContactRequestEmailsAsync_HtmlEncodesUserInput()
    {
        await _service.SendContactRequestEmailsAsync(
            UserEmail,
            "General Question / Comment",
            "<script>alert('bad')</script>\nSecond line");

        _mockEmailService.Verify(service => service.SendEmailAsync(
            UserEmail,
            It.IsAny<string>(),
            It.Is<string>(body =>
                body.Contains("&lt;script&gt;alert(&#39;bad&#39;)&lt;/script&gt;<br />Second line") &&
                !body.Contains("<script>alert"))), Times.Once);

        _mockEmailService.Verify(service => service.SendEmailAsync(
            AdminEmail,
            It.IsAny<string>(),
            It.Is<string>(body =>
                body.Contains("&lt;script&gt;alert(&#39;bad&#39;)&lt;/script&gt;<br />Second line") &&
                !body.Contains("<script>alert"))), Times.Once);
    }

    [Test]
    public async Task SendContactRequestEmailsAsync_ReturnsPartialFailure_WhenAdminEmailFails()
    {
        _mockEmailService
            .SetupSequence(service => service.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);

        var result = await _service.SendContactRequestEmailsAsync(
            UserEmail,
            "App Suggestion",
            "Please add shuffle controls.");

        Assert.Multiple(() =>
        {
            Assert.That(result.UserEmailSent, Is.True);
            Assert.That(result.AdminEmailSent, Is.False);
            Assert.That(result.Success, Is.False);
        });
    }
}