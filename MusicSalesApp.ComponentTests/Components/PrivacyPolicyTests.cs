using Bunit;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class PrivacyPolicyTests : BUnitTestBase
{
    [Test]
    public void PrivacyPolicy_Renders()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Check for key elements
        Assert.That(cut.Markup, Does.Contain("Privacy Policy"));
        Assert.That(cut.Markup, Does.Contain("Streamtunes"));
    }

    [Test]
    public void PrivacyPolicy_ContainsIntroduction()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Verify introduction section
        Assert.That(cut.Markup, Does.Contain("Introduction"));
        Assert.That(cut.Markup, Does.Contain("committed to protecting your privacy"));
    }

    [Test]
    public void PrivacyPolicy_ContainsInformationWeCollect()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Verify information collection section
        Assert.That(cut.Markup, Does.Contain("Information We Collect"));
        Assert.That(cut.Markup, Does.Contain("Email Address"));
        Assert.That(cut.Markup, Does.Contain("Password"));
        Assert.That(cut.Markup, Does.Contain("Passkey Credentials"));
    }

    [Test]
    public void PrivacyPolicy_ContainsPayPalIntegration()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Verify PayPal integration section
        Assert.That(cut.Markup, Does.Contain("Payment Processing"));
        Assert.That(cut.Markup, Does.Contain("PayPal"));
        Assert.That(cut.Markup, Does.Contain("Order ID"));
    }

    [Test]
    public void PrivacyPolicy_ContainsHowWeUseInformation()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Verify usage section
        Assert.That(cut.Markup, Does.Contain("How We Use Your Information"));
        Assert.That(cut.Markup, Does.Contain("Account Management"));
    }

    [Test]
    public void PrivacyPolicy_ContainsSharingAndDisclosure()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Verify sharing section
        Assert.That(cut.Markup, Does.Contain("Information Sharing"));
        Assert.That(cut.Markup, Does.Contain("We Do NOT Sell"));
    }

    [Test]
    public void PrivacyPolicy_ContainsDataSecurity()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Verify security section
        Assert.That(cut.Markup, Does.Contain("Data Security"));
        Assert.That(cut.Markup, Does.Contain("Password Security"));
        Assert.That(cut.Markup, Does.Contain("Encryption"));
    }

    [Test]
    public void PrivacyPolicy_ContainsDataRetention()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Verify data retention section
        Assert.That(cut.Markup, Does.Contain("Data Retention"));
        Assert.That(cut.Markup, Does.Contain("Account Deletion"));
    }

    [Test]
    public void PrivacyPolicy_ContainsUserRights()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Verify user rights section
        Assert.That(cut.Markup, Does.Contain("Your Rights and Choices"));
        Assert.That(cut.Markup, Does.Contain("Access Your Information"));
        Assert.That(cut.Markup, Does.Contain("Delete Your Account"));
    }

    [Test]
    public void PrivacyPolicy_ContainsChildrensPrivacy()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Verify children's privacy section
        Assert.That(cut.Markup, Does.Contain("Children"));
        Assert.That(cut.Markup, Does.Contain("under the age of 13"));
    }

    [Test]
    public void PrivacyPolicy_ContainsContactInformation()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Verify contact information
        Assert.That(cut.Markup, Does.Contain("Contact Us"));
        Assert.That(cut.Markup, Does.Contain("customerservice@streamtunes.net"));
    }

    [Test]
    public void PrivacyPolicy_HasLastUpdatedDate()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Verify last updated date
        Assert.That(cut.Markup, Does.Contain("Last Updated"));
        Assert.That(cut.Markup, Does.Contain("2024"));
    }

    [Test]
    public void PrivacyPolicy_ContainsNevadaReference()
    {
        // Act
        var cut = TestContext.Render<PrivacyPolicy>();

        // Assert - Verify Nevada state reference
        Assert.That(cut.Markup, Does.Contain("Nevada"));
    }
}
