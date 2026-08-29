using Bunit;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Public;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class HomeTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        SetupRendererInfo();
        MockPayPalSubscriptionManagementService
            .Setup(x => x.GetOfferQuoteAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOfferQuote(hasFreeTrial: false, isFirstTimeSubscriber: true, regularPrice: 2.99m));
    }

    [Test]
    public void Home_Renders()
    {
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Check for key elements in the redesigned home page
        Assert.That(cut.Markup, Does.Contain("Discover your"));
        Assert.That(cut.Markup, Does.Contain("next favorite artist"));
        Assert.That(cut.Markup, Does.Contain("stream the full catalog"));
    }

    [Test]
    public void Home_ShowsHeroSection_WithCallToAction()
    {
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify hero section content
        Assert.That(cut.Markup, Does.Contain("hero-section"));
        Assert.That(cut.Markup, Does.Contain("hero-title"));
        var browseButton = cut.Find("a.hero-browse-music-button");
        var loginButton = cut.Find("a.hero-login-button");

        Assert.That(browseButton.TextContent, Does.Contain("Browse Music"));
        Assert.That(loginButton.TextContent, Does.Contain("Log In"));

        // These two used to assert hero-secondary-cta, which is the VIOLET reserved by AGENTS.md
        // for creator and account actions. Browse Music is the primary listener action on the
        // landing page, so the old assertions were pinning the defect in place.
        Assert.That(browseButton.ClassList.Contains("hero-secondary-cta"), Is.False,
            "Browse Music is a listener action and must not wear the creator violet");
        Assert.That(browseButton.ClassList.Contains("cta-outline"), Is.True);
        Assert.That(loginButton.ClassList.Contains("cta-quiet"), Is.True);
        Assert.That(cut.Markup, Does.Contain("Log In or Register to Get Started"));
    }

    /// <summary>
    /// A subscriber gets no hero at all - Browse Music included.
    /// </summary>
    /// <remarks>
    /// This test used to assert the opposite: the hero's Browse Music button was the one thing the
    /// block still did for a subscriber, since every other CTA in it is already hidden from them. The
    /// hero is now dropped for subscribers outright, so the page opens on Featured Music and their
    /// playlists. They keep a route into the library through "View All Music" in the Featured Music
    /// header directly above, which is why losing this button costs them nothing.
    /// </remarks>
    [Test]
    public void Home_HidesBrowseMusicButton_ForAuthenticatedSubscribers()
    {
        // Arrange
        const int userId = 1;
        SetupAuthorizedUser(userId, "test@user.com");

        var testUser = new ApplicationUser
        {
            Id = userId,
            UserName = "test@user.com"
        };

        var likedSongsPlaylist = new Playlist
        {
            Id = 7,
            UserId = userId,
            PlaylistName = "Liked Songs",
            IsSystemGenerated = true,
            CreatedAt = DateTime.UtcNow
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(testUser);
        MockUserManager.Setup(x => x.IsInRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>())).ReturnsAsync(false);
        MockSubscriptionService.Setup(x => x.HasActiveSubscriptionAsync(userId)).ReturnsAsync(true);
        MockPlaylistService.Setup(x => x.GetOrCreateLikedSongsPlaylistAsync(userId)).ReturnsAsync(likedSongsPlaylist);
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(likedSongsPlaylist.Id)).ReturnsAsync(new List<UserPlaylist>());

        // Act
        var cut = TestContext.Render<Home>();

        // Assert
        cut.WaitForAssertion(() =>
            MockSubscriptionService.Verify(x => x.HasActiveSubscriptionAsync(userId), Times.Once));

        cut.WaitForAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cut.FindAll("a.hero-browse-music-button"), Is.Empty);
                Assert.That(cut.FindAll(".hero-section"), Is.Empty);
                // The library is still one tap away, from the section above.
                Assert.That(cut.Markup, Does.Contain("View All Music"));
            });
        });
    }

    [Test]
    public void Home_ShowsFeaturesSection_ForNonSubscribers()
    {
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify features section is present
        Assert.That(cut.Markup, Does.Contain("Why Stream Tunes?"));
        Assert.That(cut.Markup, Does.Contain("Unlimited Streaming"));
        Assert.That(cut.Markup, Does.Contain("Personal Playlists"));
        Assert.That(cut.Markup, Does.Contain("Cancel Anytime"));
    }

    [Test]
    public void Home_ShowsFeaturedMusicSection()
    {
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify featured music section is present
        Assert.That(cut.Markup, Does.Contain("Featured Music"));
        Assert.That(cut.Markup, Does.Contain("Explore the full library"));
        Assert.That(cut.Markup, Does.Contain("View All"));
    }

    [Test]
    public void Home_ShowsSubscriberCta_ForNonSubscribers()
    {
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify subscriber CTA card is present and fills the CTA section by itself
        Assert.That(cut.Markup, Does.Contain("Unlimited Music Streaming"));
        Assert.That(cut.Markup, Does.Contain("subscriber-cta"));
        Assert.That(cut.Markup, Does.Contain("Full-length streaming"));
        Assert.That(cut.Markup, Does.Contain("Log In or Register to Get Started"));
        Assert.That(cut.FindAll(".cta-split-section .cta-card"), Has.Count.EqualTo(1));
    }

    [Test]
    public void Home_HidesCreatorCta_ForNonCreators()
    {
        // Arrange - Setup non-authenticated user (default state)
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify Creator CTA was removed from the home page
        Assert.That(cut.Markup, Does.Not.Contain("creator-cta"));
        Assert.That(cut.Markup, Does.Not.Contain("Monetize Your Music"));
        Assert.That(cut.Markup, Does.Not.Contain("Original music is welcome"));
        Assert.That(cut.Markup, Does.Not.Contain("Get your songs heard worldwide"));
        Assert.That(cut.Markup, Does.Not.Contain("Earn per stream"));
        Assert.That(cut.Markup, Does.Not.Contain("Keep 100% control of your music rights"));
        Assert.That(cut.Markup, Does.Not.Contain("Quick upload process"));
        Assert.That(cut.Markup, Does.Not.Contain("No cost to join"));
    }

    [Test]
    public void Home_ShowsSubscriberAuthButtons_WhenNotAuthenticated()
    {
        // Arrange - Setup non-authenticated user (default state)
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify subscriber login/register buttons remain for non-authenticated users
        Assert.That(cut.Markup, Does.Contain("Log In or Register to Get Started"));
        Assert.That(cut.Markup, Does.Contain("Log In"));
        Assert.That(cut.Markup, Does.Contain("Register"));
        Assert.That(cut.Markup, Does.Contain("href=\"/login?returnUrl=%2Fmanage-account%23subscription\""));
        Assert.That(cut.Markup, Does.Contain("href=\"/register?returnUrl=%2Fmanage-account%23subscription\""));
        Assert.That(cut.Markup, Does.Not.Contain("%2FCreatorSettings"));
    }

    [Test]
    public void Home_VerifiedNonSubscriberCta_LinksToTheSubscriptionSection()
    {
        const int userId = 1;
        SetupAuthorizedUser(userId, "test@user.com");

        var testUser = new ApplicationUser
        {
            Id = userId,
            UserName = "test@user.com",
            Email = "test@user.com",
            EmailConfirmed = true
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(testUser);
        MockUserManager.Setup(x => x.IsInRoleAsync(It.IsAny<ApplicationUser>(), It.IsAny<string>())).ReturnsAsync(false);
        MockSubscriptionService.Setup(x => x.HasActiveSubscriptionAsync(userId)).ReturnsAsync(false);

        var cut = TestContext.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Subscribe with PayPal"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("href=\"/manage-account#subscription\""));
        Assert.That(cut.Markup, Does.Not.Contain("href=\"/CreatorSettings\""));
    }

    [Test]
    public void Home_ShowsOnlySubscriberCta_ForNonSubscribers()
    {
        // Arrange - Setup non-authenticated user (default state)
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify only the subscriber CTA is shown
        Assert.That(cut.Markup, Does.Contain("cta-split-section"));
        Assert.That(cut.Markup, Does.Contain("subscriber-cta"));
        Assert.That(cut.Markup, Does.Contain("Unlimited Music Streaming"));
        Assert.That(cut.Markup, Does.Not.Contain("creator-cta"));
        Assert.That(cut.Markup, Does.Not.Contain("Monetize Your Music"));
        Assert.That(cut.FindAll(".cta-split-section .cta-card"), Has.Count.EqualTo(1));
    }

    [Test]
    public void Home_AdvertisesConfiguredTrial_ToAnonymousVisitors()
    {
        MockPayPalSubscriptionManagementService
            .Setup(x => x.GetOfferQuoteAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOfferQuote(hasFreeTrial: true, isFirstTimeSubscriber: true, regularPrice: 0.99m));

        var cut = TestContext.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Try Unlimited Music Free for 3 days"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Support independent music"));
            Assert.That(cut.Markup, Does.Contain("stream the full catalog free for 3 days"));
            Assert.That(cut.Markup, Does.Contain("3 days free"));
            Assert.That(cut.Markup, Does.Contain("$0.99"));
            Assert.That(cut.Markup, Does.Contain("per month"));
            Assert.That(cut.Markup, Does.Contain("Cancel anytime"));
            Assert.That(cut.Markup, Does.Contain("Register for Free Trial"));
        });
        MockPayPalSubscriptionManagementService.Verify(
            x => x.GetOfferQuoteAsync(null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void Home_AdvertisesTrial_ToEligibleAuthenticatedNonSubscriber()
    {
        const int userId = 41;
        SetupAuthenticatedNonSubscriber(userId);
        MockPayPalSubscriptionManagementService
            .Setup(x => x.GetOfferQuoteAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOfferQuote(hasFreeTrial: true, isFirstTimeSubscriber: true, regularPrice: 2.99m));

        var cut = TestContext.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Start My Free Trial"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Start Your 3-Day Free Trial"));
        Assert.That(cut.Markup, Does.Contain("$2.99"));
        Assert.That(cut.Markup, Does.Contain("per month"));
        Assert.That(cut.Markup, Does.Not.Contain("Log In or Register to Start Your Free Trial"));
    }

    [Test]
    public void Home_ShowsNoTrialQuote_ToReturningNonSubscriber()
    {
        const int userId = 42;
        SetupAuthenticatedNonSubscriber(userId);
        MockPayPalSubscriptionManagementService
            .Setup(x => x.GetOfferQuoteAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOfferQuote(hasFreeTrial: false, isFirstTimeSubscriber: false, regularPrice: 0.99m));

        var cut = TestContext.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Subscribe with PayPal"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("$0.99"));
            Assert.That(cut.Markup, Does.Contain("per month"));
            Assert.That(cut.Markup, Does.Contain("Cancel anytime"));
            Assert.That(cut.Markup, Does.Not.Contain("Free Trial"));
            Assert.That(cut.Markup, Does.Not.Contain("days free"));
        });
    }

    // ---- The marketing hero ------------------------------------------------------

    [Test]
    public void Home_HidesTheHero_FromASignedInSubscriber()
    {
        // They have already subscribed, so the page opens on Featured Music and their playlists
        // rather than on a pitch.
        const int userId = 77;
        SetupAuthenticatedNonSubscriber(userId);
        MockSubscriptionService.Setup(x => x.HasActiveSubscriptionAsync(userId)).ReturnsAsync(true);

        var cut = TestContext.Render<Home>();
        cut.WaitForAssertion(() =>
            MockSubscriptionService.Verify(x => x.HasActiveSubscriptionAsync(userId), Times.Once));

        cut.WaitForAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cut.FindAll(".hero-section"), Is.Empty);
                Assert.That(cut.Markup, Does.Not.Contain("next favorite artist"));
                Assert.That(cut.FindAll("a.hero-browse-music-button"), Is.Empty);
            });
        });
    }

    [Test]
    public void Home_KeepsFeaturedMusic_ForASignedInSubscriber()
    {
        // Hiding the hero must not take the rest of the page with it.
        const int userId = 78;
        SetupAuthenticatedNonSubscriber(userId);
        MockSubscriptionService.Setup(x => x.HasActiveSubscriptionAsync(userId)).ReturnsAsync(true);

        var cut = TestContext.Render<Home>();
        cut.WaitForAssertion(() =>
            MockSubscriptionService.Verify(x => x.HasActiveSubscriptionAsync(userId), Times.Once));

        Assert.That(cut.Markup, Does.Contain("featured-music-section"));
    }

    [Test]
    public void Home_KeepsTheHero_ForASignedInNonSubscriber()
    {
        const int userId = 79;
        SetupAuthenticatedNonSubscriber(userId);

        var cut = TestContext.Render<Home>();
        cut.WaitForAssertion(() =>
            MockSubscriptionService.Verify(x => x.HasActiveSubscriptionAsync(userId), Times.Once));

        Assert.That(cut.Markup, Does.Contain("hero-section"));
    }

    [Test]
    public void Home_KeepsTheHero_ForAnAnonymousVisitor()
    {
        // Also the prerender case: the hero has to be in the server-rendered markup or crawlers get
        // a home page with no h1 at all.
        var cut = TestContext.Render<Home>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("hero-section"));
            Assert.That(cut.Markup, Does.Contain("next favorite artist"));
        });
    }

    [Test]
    public void Home_HidesTrialAdvertising_WhenSubscriptionStillProvidesAccess()
    {
        const int userId = 43;
        SetupAuthenticatedNonSubscriber(userId);
        MockSubscriptionService
            .Setup(x => x.HasActiveSubscriptionAsync(userId))
            .ReturnsAsync(true);

        var cut = TestContext.Render<Home>();
        cut.WaitForAssertion(() =>
            MockSubscriptionService.Verify(x => x.HasActiveSubscriptionAsync(userId), Times.Once));

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".cta-split-section"), Is.Empty);
            Assert.That(cut.Markup, Does.Not.Contain("Free Trial"));
            Assert.That(cut.Markup, Does.Not.Contain("Subscribe with PayPal"));
        });
        MockPayPalSubscriptionManagementService.Verify(
            x => x.GetOfferQuoteAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task HomeUserPlaylists_ShowsLikedSongsPlaylist_WhenUserIsAuthenticated()
    {
        // Was [Ignore]d as "bUnit does not reliably trigger OnAfterRenderAsync". The real cause
        // was that Home is static SSR, so that hook never ran there - in production either, which
        // is why this section rendered for nobody. It is its own interactive island now, and
        // rendering the island directly exercises the lifecycle properly.
        
        // Arrange
        SetupRendererInfo();
        
        var userId = 1;
        var testUser = new ApplicationUser { Id = userId, UserName = "test@user.com" };

        // SetupAuthorizedUser rather than a bare MockAuthStateProvider.Setup: BUnitTestBase calls
        // TestContext.AddAuthorization() AFTER registering the mock, so bUnit's own provider is what
        // the component actually resolves. Configuring only the mock leaves it unauthenticated -
        // which is the real reason these two tests never passed.
        SetupAuthorizedUser(userId, "test@user.com");
        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(testUser);

        var likedSongsPlaylist = new Playlist
        {
            Id = 1,
            UserId = userId,
            PlaylistName = "Liked Songs",
            IsSystemGenerated = true,
            CreatedAt = DateTime.UtcNow
        };

        var playlistSongs = new List<UserPlaylist>
        {
            new UserPlaylist { Id = 1, PlaylistId = 1, UserId = userId }
        };

        MockPlaylistService.Setup(x => x.GetOrCreateLikedSongsPlaylistAsync(userId)).ReturnsAsync(likedSongsPlaylist);
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(1)).ReturnsAsync(playlistSongs);
        MockRecommendationService.Setup(x => x.GetRecommendedPlaylistAsync(userId)).ReturnsAsync(new List<RecommendedPlaylist>());
        MockSubscriptionService.Setup(x => x.HasActiveSubscriptionAsync(userId)).ReturnsAsync(false);

        // Act
        var cut = TestContext.Render<HomeUserPlaylists>();

        // Let OnAfterRenderAsync and both of its awaits settle.
        await cut.InvokeAsync(() => { });

        // Assert
        Assert.That(cut.Markup, Does.Contain("Liked Songs"));
        Assert.That(cut.Markup, Does.Contain("Songs you've liked"));
    }

    [Test]
    public async Task HomeUserPlaylists_DoesNotShowLikedSongsPlaylist_WhenEmpty()
    {
        // Companion to the test above - same reason it was ignored, same fix.
        
        // Arrange
        SetupRendererInfo();
        
        var userId = 1;
        var testUser = new ApplicationUser { Id = userId, UserName = "test@user.com" };

        // SetupAuthorizedUser rather than a bare MockAuthStateProvider.Setup: BUnitTestBase calls
        // TestContext.AddAuthorization() AFTER registering the mock, so bUnit's own provider is what
        // the component actually resolves. Configuring only the mock leaves it unauthenticated -
        // which is the real reason these two tests never passed.
        SetupAuthorizedUser(userId, "test@user.com");
        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(testUser);

        var likedSongsPlaylist = new Playlist
        {
            Id = 1,
            UserId = userId,
            PlaylistName = "Liked Songs",
            IsSystemGenerated = true,
            CreatedAt = DateTime.UtcNow
        };

        MockPlaylistService.Setup(x => x.GetOrCreateLikedSongsPlaylistAsync(userId)).ReturnsAsync(likedSongsPlaylist);
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(1)).ReturnsAsync(new List<UserPlaylist>());
        MockRecommendationService.Setup(x => x.GetRecommendedPlaylistAsync(userId)).ReturnsAsync(new List<RecommendedPlaylist>());
        MockSubscriptionService.Setup(x => x.HasActiveSubscriptionAsync(userId)).ReturnsAsync(false);

        // Act
        var cut = TestContext.Render<HomeUserPlaylists>();

        // Let OnAfterRenderAsync and both of its awaits settle.
        await cut.InvokeAsync(() => { });

        // Assert - Liked Songs should not be shown when the playlist is empty
        Assert.That(cut.Markup, Does.Not.Contain("Liked Songs"));
    }

    private void SetupAuthenticatedNonSubscriber(int userId)
    {
        const string userEmail = "listener@streamtunes.test";
        SetupAuthorizedUser(userId, userEmail);
        var testUser = new ApplicationUser
        {
            Id = userId,
            UserName = userEmail,
            Email = userEmail,
            EmailConfirmed = true
        };

        MockUserManager
            .Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);
        MockUserManager
            .Setup(x => x.IsInRoleAsync(testUser, Roles.NonValidatedUser))
            .ReturnsAsync(false);
        MockSubscriptionService
            .Setup(x => x.HasActiveSubscriptionAsync(userId))
            .ReturnsAsync(false);
        MockRecommendationService
            .Setup(x => x.GetRecommendedPlaylistAsync(userId))
            .ReturnsAsync(new List<RecommendedPlaylist>());
        MockPlaylistService
            .Setup(x => x.GetOrCreateLikedSongsPlaylistAsync(userId))
            .ReturnsAsync(new Playlist
            {
                Id = userId,
                UserId = userId,
                PlaylistName = "Liked Songs",
                IsSystemGenerated = true
            });
        MockPlaylistService
            .Setup(x => x.GetPlaylistSongsAsync(userId))
            .ReturnsAsync(new List<UserPlaylist>());
    }

    private static PayPalWebOfferQuote CreateOfferQuote(
        bool hasFreeTrial,
        bool isFirstTimeSubscriber,
        decimal regularPrice)
        => new()
        {
            PlanId = hasFreeTrial ? "P-TRIAL" : "P-MONTHLY",
            PlanName = hasFreeTrial ? "Trial plan" : "Monthly plan",
            RegularPrice = regularPrice,
            CurrencyCode = PayPalSubscriptionDefaults.UsdCurrencyCode,
            IntervalUnit = PayPalBillingIntervals.Month,
            IntervalCount = 1,
            TrialDays = hasFreeTrial ? 3 : null,
            SettingsVersion = 7,
            IsFirstTimeSubscriber = isFirstTimeSubscriber
        };
}
