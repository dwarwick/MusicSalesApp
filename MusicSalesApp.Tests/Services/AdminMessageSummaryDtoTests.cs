using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class AdminMessageSummaryDtoTests
{
    [Test]
    public void RolesDisplay_JoinsRoleNames()
    {
        var dto = new AdminMessageSummaryDto
        {
            RoleNames = new[] { "Admin", "Creator" }
        };

        Assert.That(dto.RolesDisplay, Is.EqualTo("Admin, Creator"));
    }

    [TestCase(true, false, "Dialogue")]
    [TestCase(false, true, "Email")]
    [TestCase(true, true, "Dialogue, Email")]
    [TestCase(false, false, "None")]
    public void ChannelsDisplay_ReturnsExpectedValue(bool showDialog, bool sendEmail, string expected)
    {
        var dto = new AdminMessageSummaryDto
        {
            ShowDialog = showDialog,
            SendEmail = sendEmail
        };

        Assert.That(dto.ChannelsDisplay, Is.EqualTo(expected));
    }
}