using Bunit;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Admin;
using MusicSalesApp.Services;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class AdminMessagesTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        SetupRendererInfo();

        MockAdminMessageService.Setup(x => x.GetAvailableRoleNamesAsync())
            .ReturnsAsync(new List<string> { "User", "Creator" });
        MockAdminMessageService.Setup(x => x.GetMessagesAsync())
            .ReturnsAsync(new List<AdminMessageSummaryDto>());
    }

    [Test]
    public void AdminMessages_RendersCreateSectionAndGrid()
    {
        var cut = TestContext.Render<AdminMessages>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Create Message"));
            Assert.That(cut.Markup, Does.Contain("Message History"));
        });
    }

    [Test]
    public void AdminMessages_WithExistingMessage_ShowsViewAction()
    {
        MockAdminMessageService.Setup(x => x.GetMessagesAsync())
            .ReturnsAsync(new List<AdminMessageSummaryDto>
            {
                new()
                {
                    Id = 10,
                    Subject = "Release note",
                    MessageText = "Full message body",
                    RoleNames = new List<string> { "Creator" },
                    ShowDialog = true,
                    CreatedAtUtc = new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc),
                    RecipientCount = 1,
                    AcknowledgedCount = 0,
                    PendingCount = 1,
                    EmailedCount = 0,
                    CanceledCount = 0
                }
            });

        var cut = TestContext.Render<AdminMessages>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Release note"));
            Assert.That(cut.Markup, Does.Contain("View"));
        });
    }

    [Test]
    public void AdminMessages_WhenCreateClickedWithEmptyForm_ShowsValidationErrors()
    {
        var cut = TestContext.Render<AdminMessages>();
        cut.WaitForState(() => cut.Markup.Contains("Create Message"), TimeSpan.FromSeconds(5));

        cut.FindAll("button")
            .First(button => button.TextContent.Contains("Create Message", StringComparison.OrdinalIgnoreCase))
            .Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Subject is required."));
            Assert.That(cut.Markup, Does.Contain("Message text is required."));
            Assert.That(cut.Markup, Does.Contain("Select at least one role."));
        });
    }
}