using Bunit;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Public;
using MusicSalesApp.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class HomeTests : BUnitTestBase
{
    [Test]
    public void Home_Renders()
    {
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Check for key elements in the redesigned home page
        Assert.That(cut.Markup, Does.Contain("Discover your"));
        Assert.That(cut.Markup, Does.Contain("next favorite artist"));
        Assert.That(cut.Markup, Does.Contain("Stream all songs"));
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
        Assert.That(browseButton.ClassList.Contains("hero-secondary-cta"), Is.True);
        Assert.That(loginButton.TextContent, Does.Contain("Log In"));
        Assert.That(loginButton.ClassList.Contains("hero-secondary-cta"), Is.True);
        Assert.That(cut.Markup, Does.Contain("Log In or Register to Get Started"));
    }

    [Test]
    public void Home_ShowsBrowseMusicButton_ForAuthenticatedSubscribers()
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
        var browseButton = cut.Find("a.hero-browse-music-button");
        Assert.That(browseButton.TextContent, Does.Contain("Browse Music"));
        Assert.That(browseButton.GetAttribute("href"), Is.EqualTo("/music-library"));
        Assert.That(browseButton.ClassList.Contains("hero-secondary-cta"), Is.True);
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
        Assert.That(cut.Markup, Does.Contain("Subscribe for unlimited access"));
        Assert.That(cut.Markup, Does.Contain("View All"));
    }

    [Test]
    public void Home_ShowsSubscriberCta_ForNonSubscribers()
    {
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify subscriber CTA card is present (not the old green box)
        Assert.That(cut.Markup, Does.Contain("Unlimited Music Streaming"));
        Assert.That(cut.Markup, Does.Contain("subscriber-cta"));
        Assert.That(cut.Markup, Does.Contain("Full-length streaming"));
        Assert.That(cut.Markup, Does.Contain("Log In or Register to Get Started"));
    }

    [Test]
    public void Home_ShowsCreatorCta_ForNonCreators()
    {
        // Arrange - Setup non-authenticated user (default state)
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify Creator CTA is present for non-creators
        Assert.That(cut.Markup, Does.Contain("Monetize Your Music"));
        Assert.That(cut.Markup, Does.Contain("Original music is welcome"));
        Assert.That(cut.Markup, Does.Contain("Get your songs heard worldwide"));
        Assert.That(cut.Markup, Does.Contain("Earn per stream"));
        Assert.That(cut.Markup, Does.Contain("Keep 100% control of your music rights"));
        Assert.That(cut.Markup, Does.Contain("Quick upload process"));
        Assert.That(cut.Markup, Does.Contain("No cost to join"));
        Assert.That(cut.Markup, Does.Contain("href=\"/learnmore\""));
    }

    [Test]
    public void Home_ShowsSignInButton_ForCreatorCta_WhenNotAuthenticated()
    {
        // Arrange - Setup non-authenticated user (default state)
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify new login/register buttons for non-authenticated users
        Assert.That(cut.Markup, Does.Contain("Log In or Register to Get Started"));
        Assert.That(cut.Markup, Does.Contain("Log In"));
        Assert.That(cut.Markup, Does.Contain("Register"));
        Assert.That(cut.Markup, Does.Contain("href=\"/login?returnUrl=%2FCreatorSettings\""));
    }

    [Test]
    public void Home_VerifiedNonCreatorCta_LinksToCreatorSettings()
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
        cut.WaitForState(() => cut.Markup.Contains("Click Here to Get Started"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("href=\"/CreatorSettings\""));
    }

    [Test]
    public void Home_ShowsBothCtas_ForNonSubscriberNonCreator()
    {
        // Arrange - Setup non-authenticated user (default state)
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify both CTAs are shown
        Assert.That(cut.Markup, Does.Contain("cta-split-section"));
        Assert.That(cut.Markup, Does.Contain("creator-cta"));
        Assert.That(cut.Markup, Does.Contain("subscriber-cta"));
        Assert.That(cut.Markup, Does.Contain("Monetize Your Music"));
        Assert.That(cut.Markup, Does.Contain("Unlimited Music Streaming"));
    }

    [Test]
    [Ignore("Skipped: bUnit does not reliably trigger OnAfterRenderAsync data loading. This test requires component refactoring to use a different lifecycle pattern.")]
    public void Home_ShowsLikedSongsPlaylist_WhenUserIsAuthenticated()
    {
        // This test validates that authenticated users see the Liked Songs playlist on the home page.
        // Currently skipped because the Home component loads data in OnAfterRenderAsync,
        // which doesn't execute properly in bUnit's synchronous test model.
        
        // Arrange
        SetupRendererInfo();
        
        var userId = 1;
        var testUser = new ApplicationUser { Id = userId, UserName = "test@user.com" };

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "test@user.com")
        }, "TestAuthType");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(claimsPrincipal);
        MockAuthStateProvider.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(authState);
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
        var cut = TestContext.Render<Home>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Liked Songs"));
        Assert.That(cut.Markup, Does.Contain("Songs you've liked"));
    }

    [Test]
    [Ignore("Skipped: bUnit does not reliably trigger OnAfterRenderAsync data loading. This test requires component refactoring to use a different lifecycle pattern.")]
    public async Task Home_DoesNotShowLikedSongsPlaylist_WhenEmpty()
    {
        // This test validates that Liked Songs playlist is hidden when empty.
        // Currently skipped because the Home component loads data in OnAfterRenderAsync,
        // which doesn't execute properly in bUnit's synchronous test model.
        
        // Arrange
        SetupRendererInfo();
        
        var userId = 1;
        var testUser = new ApplicationUser { Id = userId, UserName = "test@user.com" };

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "test@user.com")
        }, "TestAuthType");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(claimsPrincipal);
        MockAuthStateProvider.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(authState);
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
        var cut = TestContext.Render<Home>();
        
        // Wait for OnAfterRenderAsync to complete
        await cut.InvokeAsync(() => { });

        // Assert - Liked Songs should not be shown when the playlist is empty
        Assert.That(cut.Markup, Does.Not.Contain("Liked Songs"));
    }
}
