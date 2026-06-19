using Bunit;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Public.Legal;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class AccountDeletionTests : BUnitTestBase
{
    [Test]
    public void AccountDeletion_Renders()
    {
        var cut = TestContext.Render<AccountDeletion>();

        Assert.That(cut.Markup, Does.Contain("Account Deletion"));
        Assert.That(cut.Markup, Does.Contain("Streamtunes"));
    }

    [Test]
    public void AccountDeletion_ExplainsSubscriptionRequirement()
    {
        var cut = TestContext.Render<AccountDeletion>();

        Assert.That(cut.Markup, Does.Contain("Cancel Any Active Subscription"));
        Assert.That(cut.Markup, Does.Contain("cancel any active subscription before deleting your account"));
    }

    [Test]
    public void AccountDeletion_ExplainsCreatorRequirement()
    {
        var cut = TestContext.Render<AccountDeletion>();

        Assert.That(cut.Markup, Does.Contain("Active Creators Must Stop First"));
        Assert.That(cut.Markup, Does.Contain("href=\"/CreatorSettings\""));
        Assert.That(cut.Markup, Does.Contain("Creator / Artist Settings"));
        Assert.That(cut.Markup, Does.Contain("Stop Being a Creator"));
        Assert.That(cut.Markup, Does.Contain("removes those songs from playlists across the service"));
    }

    [Test]
    public void AccountDeletion_ExplainsReRegistrationLimitations()
    {
        var cut = TestContext.Render<AccountDeletion>();

        Assert.That(cut.Markup, Does.Contain("Create a New Account Later"));
        Assert.That(cut.Markup, Does.Contain("deleted playlists and settings will not be restored"));
    }
}
