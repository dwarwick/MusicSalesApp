using Bunit;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Admin;
using MusicSalesApp.Services;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class ContactMessagesTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        SetupRendererInfo();

        MockContactRequestAdminService.Setup(service => service.GetSubmissionsAsync())
            .ReturnsAsync(new List<ContactRequestSubmissionDto>());
    }

    [Test]
    public void ContactMessages_RendersGridPanel()
    {
        var cut = TestContext.Render<ContactMessages>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Contact Messages"));
            Assert.That(cut.Markup, Does.Contain("Mobile Contact Form Submissions"));
        });
    }

    [Test]
    public void ContactMessages_WithExistingSubmission_ShowsGridRowAndViewAction()
    {
        MockContactRequestAdminService.Setup(service => service.GetSubmissionsAsync())
            .ReturnsAsync(new List<ContactRequestSubmissionDto>
            {
                new()
                {
                    Id = 12,
                    UserId = 7,
                    UserEmail = "listener@example.com",
                    Subject = "Bug Report",
                    MessageText = "Playback fails after one minute.",
                    MessageLength = 32,
                    IpAddress = "192.0.2.1",
                    SubmittedAtUtc = new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc),
                    UserEmailSent = true,
                    AdminEmailSent = true,
                    EmailSendCompletedAtUtc = new DateTime(2026, 5, 25, 12, 0, 5, DateTimeKind.Utc)
                }
            });

        var cut = TestContext.Render<ContactMessages>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("listener@example.com"));
            Assert.That(cut.Markup, Does.Contain("Bug Report"));
            Assert.That(cut.Markup, Does.Contain("Playback fails after one minute."));
            Assert.That(cut.Markup, Does.Contain("View"));
        });
    }
}