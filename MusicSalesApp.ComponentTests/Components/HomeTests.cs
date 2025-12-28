using Bunit;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages;
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
        Assert.That(cut.Markup, Does.Contain("Your Music."));
        Assert.That(cut.Markup, Does.Contain("Unlimited."));
        Assert.That(cut.Markup, Does.Contain("Stream Tunes"));
    }

    [Test]
    public void Home_ShowsHeroSection_WithCallToAction()
    {
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify hero section content
        Assert.That(cut.Markup, Does.Contain("hero-section"));
        Assert.That(cut.Markup, Does.Contain("hero-title"));
        Assert.That(cut.Markup, Does.Contain("Start Free Trial"));
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
        Assert.That(cut.Markup, Does.Contain("Listen to samples"));
        Assert.That(cut.Markup, Does.Contain("View All"));
    }

    [Test]
    public void Home_ShowsSubscriptionCtaSection_ForNonSubscribers()
    {
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - Verify subscription CTA section is present
        Assert.That(cut.Markup, Does.Contain("Ready to unlock unlimited music?"));
        Assert.That(cut.Markup, Does.Contain("subscription-benefits"));
        Assert.That(cut.Markup, Does.Contain("Full-length streaming"));
        Assert.That(cut.Markup, Does.Contain("Get Started Free"));
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
