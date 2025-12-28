using Bunit;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class TermsOfUseTests : BUnitTestBase
{
    [Test]
    public void TermsOfUse_Renders()
    {
        // Act
        var cut = TestContext.Render<TermsOfUse>();

        // Assert - Check for key elements
        Assert.That(cut.Markup, Does.Contain("Terms of Use"));
        Assert.That(cut.Markup, Does.Contain("Streamtunes"));
    }

    [Test]
    public void TermsOfUse_ContainsBusinessInformation()
    {
        // Act
        var cut = TestContext.Render<TermsOfUse>();

        // Assert - Verify business information
        Assert.That(cut.Markup, Does.Contain("sole proprietorship"));
        Assert.That(cut.Markup, Does.Contain("State of Nevada"));
        Assert.That(cut.Markup, Does.Contain("streamtunes.net"));
    }

    [Test]
    public void TermsOfUse_ContainsAccountTypes()
    {
        // Act
        var cut = TestContext.Render<TermsOfUse>();

        // Assert - Verify account type information
        Assert.That(cut.Markup, Does.Contain("Free Account"));
        Assert.That(cut.Markup, Does.Contain("60 seconds"));
        Assert.That(cut.Markup, Does.Contain("Monthly Subscription"));
    }

    [Test]
    public void TermsOfUse_ContainsPurchaseTerms()
    {
        // Act
        var cut = TestContext.Render<TermsOfUse>();

        // Assert - Verify purchase terms
        Assert.That(cut.Markup, Does.Contain("Purchases"));
        Assert.That(cut.Markup, Does.Contain("permanent access"));
        Assert.That(cut.Markup, Does.Contain("No Refunds"));
    }

    [Test]
    public void TermsOfUse_ContainsSubscriptionTerms()
    {
        // Act
        var cut = TestContext.Render<TermsOfUse>();

        // Assert - Verify subscription terms
        Assert.That(cut.Markup, Does.Contain("Cancellation"));
        Assert.That(cut.Markup, Does.Contain("current billing period"));
        Assert.That(cut.Markup, Does.Contain("subscription end date"));
    }

    [Test]
    public void TermsOfUse_ContainsAccountDeletionPolicy()
    {
        // Act
        var cut = TestContext.Render<TermsOfUse>();

        // Assert - Verify account deletion information
        Assert.That(cut.Markup, Does.Contain("Account Deletion"));
        Assert.That(cut.Markup, Does.Contain("permanently deleted"));
        Assert.That(cut.Markup, Does.Contain("cannot be undone"));
    }

    [Test]
    public void TermsOfUse_ContainsIntellectualPropertySection()
    {
        // Act
        var cut = TestContext.Render<TermsOfUse>();

        // Assert - Verify intellectual property section
        Assert.That(cut.Markup, Does.Contain("Intellectual Property"));
        Assert.That(cut.Markup, Does.Contain("AI-Generated Content"));
        Assert.That(cut.Markup, Does.Contain("ChatGPT"));
    }

    [Test]
    public void TermsOfUse_ContainsGoverningLaw()
    {
        // Act
        var cut = TestContext.Render<TermsOfUse>();

        // Assert - Verify governing law section
        Assert.That(cut.Markup, Does.Contain("Governing Law"));
        Assert.That(cut.Markup, Does.Contain("Nevada"));
    }

    [Test]
    public void TermsOfUse_ContainsLiabilityLimitations()
    {
        // Act
        var cut = TestContext.Render<TermsOfUse>();

        // Assert - Verify liability limitations
        Assert.That(cut.Markup, Does.Contain("Limitation of Liability"));
        Assert.That(cut.Markup, Does.Contain("AS IS"));
    }

    [Test]
    public void TermsOfUse_ContainsContactInformation()
    {
        // Act
        var cut = TestContext.Render<TermsOfUse>();

        // Assert - Verify contact information
        Assert.That(cut.Markup, Does.Contain("Contact Information"));
    }

    [Test]
    public void TermsOfUse_HasLastUpdatedDate()
    {
        // Act
        var cut = TestContext.Render<TermsOfUse>();

        // Assert - Verify last updated date
        Assert.That(cut.Markup, Does.Contain("Last Updated"));
        Assert.That(cut.Markup, Does.Contain("2024"));
    }
}
