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

        // Assert
        Assert.That(cut.Markup, Does.Contain("Welcome to Stream Tunes!"));
    }

    [Test]
    public void Home_ShowsLikedSongsPlaylist_WhenUserIsAuthenticated()
    {
        // Arrange
        SetupRendererInfo(); // Required for Syncfusion components
        
        var userId = 1;
        var testUser = new ApplicationUser { Id = userId, UserName = "test@user.com" };

        // Mock authenticated user
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "test@user.com")
        }, "TestAuthType");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(claimsPrincipal);
        MockAuthStateProvider.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(authState);
        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(testUser);

        // Mock Liked Songs playlist
        var likedSongsPlaylist = new Playlist
        {
            Id = 1,
            UserId = userId,
            PlaylistName = "Liked Songs",
            IsSystemGenerated = true,
            CreatedAt = DateTime.UtcNow
        };

        // Mock playlist with some songs
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
    public void Home_DoesNotShowLikedSongsPlaylist_WhenEmpty()
    {
        // Arrange
        SetupRendererInfo(); // Required for Syncfusion components
        
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

        // Mock Liked Songs playlist with no songs
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

        // Assert - Liked Songs should not be shown when the playlist is empty
        Assert.That(cut.Markup, Does.Not.Contain("Liked Songs"));
    }

    [Test]
    public void Home_ContainsFeaturedMusicSection()
    {
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - should have Featured Music section
        Assert.That(cut.Markup, Does.Contain("Featured Music"));
    }

    [Test]
    public void Home_ContainsMusicLibraryComponent()
    {
        // Act
        var cut = TestContext.Render<Home>();

        // Assert - should have the music-cards-grid which is from MusicLibrary component
        Assert.That(cut.Markup, Does.Contain("music-cards-grid"));
    }
}
