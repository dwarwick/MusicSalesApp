using Bunit;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class UserRefundPolicyTests : BUnitTestBase
{
    [Test]
    public void UserRefundPolicy_Renders()
    {
        // Act
        var cut = TestContext.Render<UserRefundPolicy>();

        // Assert - Check for key elements
        Assert.That(cut.Markup, Does.Contain("User Refund Policy"));
        Assert.That(cut.Markup, Does.Contain("Streamtunes"));
    }

    [Test]
    public void UserRefundPolicy_HasEffectiveDate()
    {
        // Act
        var cut = TestContext.Render<UserRefundPolicy>();

        // Assert - Verify effective date
        Assert.That(cut.Markup, Does.Contain("Effective Date"));
        Assert.That(cut.Markup, Does.Contain("2026"));
    }

    [Test]
    public void UserRefundPolicy_ContainsNonRefundableStatement()
    {
        // Act
        var cut = TestContext.Render<UserRefundPolicy>();

        // Assert - Verify non-refundable statement
        Assert.That(cut.Markup, Does.Contain("non-refundable"));
        Assert.That(cut.Markup, Does.Contain("subscription payments"));
    }

    [Test]
    public void UserRefundPolicy_ContainsNoRefundsFor()
    {
        // Act
        var cut = TestContext.Render<UserRefundPolicy>();

        // Assert - Verify no refunds list
        Assert.That(cut.Markup, Does.Contain("Partial billing periods"));
        Assert.That(cut.Markup, Does.Contain("Unused streaming time"));
        Assert.That(cut.Markup, Does.Contain("Account inactivity"));
    }

    [Test]
    public void UserRefundPolicy_ContainsCancellationInformation()
    {
        // Act
        var cut = TestContext.Render<UserRefundPolicy>();

        // Assert - Verify cancellation information
        Assert.That(cut.Markup, Does.Contain("canceled at any time"));
        Assert.That(cut.Markup, Does.Contain("prevent future charges"));
    }
}
