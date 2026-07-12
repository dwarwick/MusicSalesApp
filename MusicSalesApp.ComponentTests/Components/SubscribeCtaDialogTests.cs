using Bunit;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.Models;
using NUnit.Framework;
using System.Security.Claims;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class SubscribeCtaDialogTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        SetupRendererInfo();
        MockPayPalSubscriptionManagementService
            .Setup(x => x.GetOfferQuoteAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOfferQuote(hasFreeTrial: false, isFirstTimeSubscriber: true));
    }

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

        // Assert - Keep the dialog concise while including the offer essentials.
        Assert.That(cut.Markup, Does.Not.Contain("cta-icon"), "Should not have music note icon");
        Assert.That(cut.Markup, Does.Not.Contain("cta-benefits"), "Should not have benefits checklist");
        Assert.That(cut.Markup, Does.Contain("Support independent music"));
        Assert.That(cut.Markup, Does.Not.Contain("Full-length streaming"), "Should not have benefits text");
    }

    [Test]
    public async Task SubscribeCtaDialog_AdvertisesTrial_ToAnonymousVisitors()
    {
        MockPayPalSubscriptionManagementService
            .Setup(x => x.GetOfferQuoteAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOfferQuote(hasFreeTrial: true, isFirstTimeSubscriber: true));
        var cut = TestContext.Render<SubscribeCtaDialog>(parameters => parameters
            .Add(p => p.IsAuthenticated, false)
            .Add(p => p.HasActiveSubscription, false));

        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Start Your 3-Day Free Trial"));
            Assert.That(cut.Markup, Does.Contain("Support independent music"));
            Assert.That(cut.Markup, Does.Contain("stream the full catalog"));
            Assert.That(cut.Markup, Does.Contain("3 days free"));
            Assert.That(cut.Markup, Does.Contain("$0.99"));
            Assert.That(cut.Markup, Does.Contain("per month"));
            Assert.That(cut.Markup, Does.Contain("Cancel anytime"));
            Assert.That(cut.Markup, Does.Contain("Register for Free Trial"));
        });
    }

    [Test]
    public async Task SubscribeCtaDialog_UsesNoTrialQuote_ForReturningSubscriber()
    {
        const int userId = 17;
        const string userEmail = "returning@streamtunes.test";
        SetupAuthorizedUser(userId, userEmail);
        MockUserManager
            .Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(new ApplicationUser { Id = userId, UserName = userEmail });
        MockPayPalSubscriptionManagementService
            .Setup(x => x.GetOfferQuoteAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOfferQuote(hasFreeTrial: false, isFirstTimeSubscriber: false));
        var cut = TestContext.Render<SubscribeCtaDialog>(parameters => parameters
            .Add(p => p.IsAuthenticated, true)
            .Add(p => p.HasActiveSubscription, false));

        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Unlimited Music Streaming"));
            Assert.That(cut.Markup, Does.Contain("$0.99"));
            Assert.That(cut.Markup, Does.Contain("per month"));
            Assert.That(cut.Markup, Does.Not.Contain("Free Trial"));
            Assert.That(cut.Markup, Does.Not.Contain("days free"));
        });
        MockPayPalSubscriptionManagementService.Verify(
            x => x.GetOfferQuoteAsync(userId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task SubscribeCtaDialog_DoesNotLoadOrAdvertiseOffer_WhenAccessIsStillActive()
    {
        var cut = TestContext.Render<SubscribeCtaDialog>(parameters => parameters
            .Add(p => p.IsAuthenticated, true)
            .Add(p => p.HasActiveSubscription, true));

        await cut.InvokeAsync(() => cut.Instance.OnPreviewEndedAsync());

        Assert.That(cut.Markup, Does.Not.Contain("Free Trial"));
        MockPayPalSubscriptionManagementService.Verify(
            x => x.GetOfferQuoteAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static PayPalWebOfferQuote CreateOfferQuote(bool hasFreeTrial, bool isFirstTimeSubscriber)
        => new()
        {
            PlanId = hasFreeTrial ? "P-TRIAL" : "P-MONTHLY",
            PlanName = hasFreeTrial ? "Trial plan" : "Monthly plan",
            RegularPrice = 0.99m,
            CurrencyCode = PayPalSubscriptionDefaults.UsdCurrencyCode,
            IntervalUnit = PayPalBillingIntervals.Month,
            IntervalCount = 1,
            TrialDays = hasFreeTrial ? 3 : null,
            SettingsVersion = 7,
            IsFirstTimeSubscriber = isFirstTimeSubscriber,
            IsConfigured = true
        };
}
