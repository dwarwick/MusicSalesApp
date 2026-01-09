using Bunit;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class CreatorAgreementTests : BUnitTestBase
{
    [Test]
    public void CreatorAgreement_Renders()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Check for key elements
        Assert.That(cut.Markup, Does.Contain("Creator Agreement"));
        Assert.That(cut.Markup, Does.Contain("Streamtunes"));
    }

    [Test]
    public void CreatorAgreement_HasEffectiveDate()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify effective date
        Assert.That(cut.Markup, Does.Contain("Effective Date"));
        Assert.That(cut.Markup, Does.Contain("2026"));
    }

    [Test]
    public void CreatorAgreement_ContainsPlatformDescription()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify platform description section
        Assert.That(cut.Markup, Does.Contain("Platform Description"));
        Assert.That(cut.Markup, Does.Contain("subscription-based music streaming service"));
    }

    [Test]
    public void CreatorAgreement_ContainsEligibilityRequirements()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify eligibility section
        Assert.That(cut.Markup, Does.Contain("Eligibility and Account Setup"));
        Assert.That(cut.Markup, Does.Contain("at least 18 years old"));
        Assert.That(cut.Markup, Does.Contain("payment provider"));
    }

    [Test]
    public void CreatorAgreement_ContainsOwnershipAndLicenseGrant()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify ownership section
        Assert.That(cut.Markup, Does.Contain("Ownership and License Grant"));
        Assert.That(cut.Markup, Does.Contain("retain full ownership"));
    }

    [Test]
    public void CreatorAgreement_ContainsRoyaltiesAndPayouts()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify royalties section
        Assert.That(cut.Markup, Does.Contain("Royalties and Payouts"));
        Assert.That(cut.Markup, Does.Contain("30 seconds"));
        Assert.That(cut.Markup, Does.Contain("weekly basis"));
    }

    [Test]
    public void CreatorAgreement_ContainsTaxInformation()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify tax section
        Assert.That(cut.Markup, Does.Contain("Taxes"));
        Assert.That(cut.Markup, Does.Contain("independent contractors"));
        Assert.That(cut.Markup, Does.Contain("1099"));
    }

    [Test]
    public void CreatorAgreement_ContainsContentStandards()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify content standards section
        Assert.That(cut.Markup, Does.Contain("Content Standards"));
        Assert.That(cut.Markup, Does.Contain("copyrights"));
    }

    [Test]
    public void CreatorAgreement_ContainsDMCACompliance()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify DMCA section
        Assert.That(cut.Markup, Does.Contain("DMCA"));
        Assert.That(cut.Markup, Does.Contain("Digital Millennium Copyright Act"));
        Assert.That(cut.Markup, Does.Contain("customerservice@streamtunes.net"));
    }

    [Test]
    public void CreatorAgreement_ContainsRefundPolicy()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify refund policy section
        Assert.That(cut.Markup, Does.Contain("Refund Policy"));
        Assert.That(cut.Markup, Does.Contain("non-refundable"));
    }

    [Test]
    public void CreatorAgreement_ContainsSuspensionAndTermination()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify suspension section
        Assert.That(cut.Markup, Does.Contain("Suspension and Termination"));
        Assert.That(cut.Markup, Does.Contain("violate"));
    }

    [Test]
    public void CreatorAgreement_ContainsLimitationOfLiability()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify liability section
        Assert.That(cut.Markup, Does.Contain("Limitation of Liability"));
        Assert.That(cut.Markup, Does.Contain("lost profits"));
    }

    [Test]
    public void CreatorAgreement_ContainsIndemnification()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify indemnification section
        Assert.That(cut.Markup, Does.Contain("Indemnification"));
        Assert.That(cut.Markup, Does.Contain("hold harmless"));
    }

    [Test]
    public void CreatorAgreement_ContainsGoverningLaw()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify governing law section
        Assert.That(cut.Markup, Does.Contain("Governing Law"));
        Assert.That(cut.Markup, Does.Contain("State of Nevada"));
    }

    [Test]
    public void CreatorAgreement_ContainsPrivacyNote()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify privacy note section
        Assert.That(cut.Markup, Does.Contain("Privacy Note"));
        Assert.That(cut.Markup, Does.Contain("payment processor"));
    }

    [Test]
    public void CreatorAgreement_ContainsChangesClause()
    {
        // Act
        var cut = TestContext.Render<CreatorAgreement>();

        // Assert - Verify changes section
        Assert.That(cut.Markup, Does.Contain("Changes"));
        Assert.That(cut.Markup, Does.Contain("update this Agreement"));
    }
}
