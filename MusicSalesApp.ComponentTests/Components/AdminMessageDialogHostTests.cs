using Bunit;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.Services;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class AdminMessageDialogHostTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        SetupAuthorizedUser(42, "listener@example.com");
        SetupRendererInfo();

        MockAdminMessageService.Setup(x => x.GetPendingDialogMessagesAsync(42))
            .ReturnsAsync(new List<PendingAdminMessageDto>
            {
                new()
                {
                    MessageId = 11,
                    Subject = "Testing subject",
                    MessageText = "Please read this update.",
                    CreatedAtUtc = new DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc)
                }
            });

        TestContext.JSInterop
            .Setup<string>("dashboardHelper.formatAdminMessageDate", _ => true)
            .SetResult("05/03/2026");
    }

    [Test]
    public void AdminMessageDialogHost_RendersPendingMessage()
    {
        var cut = TestContext.Render<AdminMessageDialogHost>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Message from StreamTunes"));
            Assert.That(cut.Markup, Does.Contain("Testing subject"));
            Assert.That(cut.Markup, Does.Contain("Created:"));
            Assert.That(cut.Markup, Does.Contain("05/03/2026"));
            Assert.That(cut.Markup, Does.Contain("Please read this update."));
        });
    }

    [Test]
    public void AdminMessageDialogHost_RefreshesWhenHubSignalsUpdate()
    {
        MockAdminMessageService.Reset();
        MockAdminMessageService.SetupSequence(x => x.GetPendingDialogMessagesAsync(42))
            .ReturnsAsync(new List<PendingAdminMessageDto>())
            .ReturnsAsync(new List<PendingAdminMessageDto>
            {
                new()
                {
                    MessageId = 12,
                    Subject = "Live subject",
                    MessageText = "This arrived while the user was already logged in.",
                    CreatedAtUtc = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc)
                }
            });

        TestContext.JSInterop
            .Setup<string>("dashboardHelper.formatAdminMessageDate", _ => true)
            .SetResult("05/04/2026");

        var cut = TestContext.Render<AdminMessageDialogHost>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("Live subject"));
        });

        MockAdminMessageHubClient.Raise(x => x.OnAdminMessagesUpdated += null);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Live subject"));
            Assert.That(cut.Markup, Does.Contain("This arrived while the user was already logged in."));
        });
    }

    [Test]
    public void AdminMessageDialogHost_AcknowledgeButtonAcknowledgesMessage()
    {
        MockAdminMessageService.Setup(x => x.AcknowledgeMessageAsync(42, 11))
            .ReturnsAsync(true);

        var cut = TestContext.Render<AdminMessageDialogHost>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Please read this update."));
        });

        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            MockAdminMessageService.Verify(x => x.AcknowledgeMessageAsync(42, 11), Times.Once);
            Assert.That(cut.Markup, Does.Not.Contain("Please read this update."));
        });
    }
}