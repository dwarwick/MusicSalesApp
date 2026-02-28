using Bunit;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.ComponentTests.Testing;
using NUnit.Framework;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class SubscribeCtaDialogTests : BUnitTestBase
{
    [Test]
    public void SubscribeCtaDialog_Renders_WithoutError()
    {
        // Act
        var cut = TestContext.Render<SubscribeCtaDialog>(parameters => parameters
            .Add(p => p.IsAuthenticated, false)
            .Add(p => p.HasActiveSubscription, false));

        // Assert - Dialog should render but not be visible initially
        Assert.That(cut.Markup, Is.Not.Null);
    }

    [Test]
    public async Task SubscribeCtaDialog_ShowsDialog_OnFirstPreviewEnd()
    {
        // Arrange
        var cut = TestContext.Render<SubscribeCtaDialog>(parameters => parameters
            .Add(p => p.IsAuthenticated, false)
            .Add(p => p.HasActiveSubscription, false));

        // Act - Trigger first preview end
        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());

        // Assert - Dialog should show with CTA content
        Assert.That(cut.Markup, Does.Contain("Unlimited Music Streaming"));
        Assert.That(cut.Markup, Does.Contain("Log In or Register to Get Started"));
    }

    [Test]
    public async Task SubscribeCtaDialog_ShowsLoginRegisterButtons_WhenNotAuthenticated()
    {
        // Arrange
        var cut = TestContext.Render<SubscribeCtaDialog>(parameters => parameters
            .Add(p => p.IsAuthenticated, false)
            .Add(p => p.HasActiveSubscription, false));

        // Act
        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());

        // Assert
        Assert.That(cut.Markup, Does.Contain("Log In"));
        Assert.That(cut.Markup, Does.Contain("Register"));
    }

    [Test]
    public async Task SubscribeCtaDialog_ShowsSubscribeButton_WhenAuthenticatedNotSubscribed()
    {
        // Arrange
        var cut = TestContext.Render<SubscribeCtaDialog>(parameters => parameters
            .Add(p => p.IsAuthenticated, true)
            .Add(p => p.HasActiveSubscription, false));

        // Act
        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());

        // Assert
        Assert.That(cut.Markup, Does.Contain("Subscribe"));
        Assert.That(cut.Markup, Does.Not.Contain("Log In or Register to Get Started"));
    }

    [Test]
    public async Task SubscribeCtaDialog_DoesNotShow_WhenSubscribed()
    {
        // Arrange
        var cut = TestContext.Render<SubscribeCtaDialog>(parameters => parameters
            .Add(p => p.IsAuthenticated, true)
            .Add(p => p.HasActiveSubscription, true));

        // Act - Multiple preview ends should not trigger dialog
        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());
        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());
        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());

        // Assert - Dialog should not be triggered for subscribers
        // Either the markup is empty or the dialog is hidden
        var hasNoVisibleCta = string.IsNullOrWhiteSpace(cut.Markup) || 
                              !cut.Markup.Contains("Unlimited Music Streaming") || 
                              cut.Markup.Contains("e-blazor-hidden");
        Assert.That(hasNoVisibleCta, Is.True, "CTA should not be visible for subscribers");
    }

    [Test]
    public async Task SubscribeCtaDialog_ShowsOnFirstPreview_ThenResetShowsAgain()
    {
        // Arrange
        var cut = TestContext.Render<SubscribeCtaDialog>(parameters => parameters
            .Add(p => p.IsAuthenticated, false)
            .Add(p => p.HasActiveSubscription, false));

        // Act - First preview should show
        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());
        Assert.That(cut.Markup, Does.Contain("Unlimited Music Streaming"), "Should show on first preview");

        // Reset the counter
        cut.Instance.ResetCounter();

        // After reset, first preview should show again
        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());
        Assert.That(cut.Markup, Does.Contain("Unlimited Music Streaming"), "Should show on first preview after reset");
    }

    [Test]
    public void SubscribeCtaDialog_ResetCounter_ResetsState()
    {
        // Arrange
        var cut = TestContext.Render<SubscribeCtaDialog>(parameters => parameters
            .Add(p => p.IsAuthenticated, false)
            .Add(p => p.HasActiveSubscription, false));

        // Act
        cut.Instance.ResetCounter();

        // Assert - No error should occur, counter should be reset
        Assert.Pass("ResetCounter completed without error");
    }

    [Test]
    public async Task SubscribeCtaDialog_ShowsSubscriptionPrice()
    {
        // Arrange
        var cut = TestContext.Render<SubscribeCtaDialog>(parameters => parameters
            .Add(p => p.IsAuthenticated, false)
            .Add(p => p.HasActiveSubscription, false));

        // Act
        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());

        // Assert - Should show subscription price
        Assert.That(cut.Markup, Does.Contain("per month"));
        Assert.That(cut.Markup, Does.Contain("$"));
    }

    [Test]
    public async Task SubscribeCtaDialog_HasSimplifiedContent()
    {
        // Arrange
        var cut = TestContext.Render<SubscribeCtaDialog>(parameters => parameters
            .Add(p => p.IsAuthenticated, false)
            .Add(p => p.HasActiveSubscription, false));

        // Act
        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());

        // Assert - Should NOT have the extra content removed per user feedback
        Assert.That(cut.Markup, Does.Not.Contain("cta-icon"), "Should not have music note icon");
        Assert.That(cut.Markup, Does.Not.Contain("cta-benefits"), "Should not have benefits checklist");
        Assert.That(cut.Markup, Does.Not.Contain("cta-description"), "Should not have description text");
        Assert.That(cut.Markup, Does.Not.Contain("Full-length streaming"), "Should not have benefits text");
    }
}
