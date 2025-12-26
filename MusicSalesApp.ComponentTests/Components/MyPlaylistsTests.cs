using Bunit;
using Moq;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class MyPlaylistsTests : BUnitTestBase
{
    [Test]
    public void MyPlaylists_ShowsLikedSongsPlaylist()
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

        // Mock playlists including Liked Songs
        var playlists = new List<Playlist>
        {
            new Playlist
            {
                Id = 1,
                UserId = userId,
                PlaylistName = "Liked Songs",
                IsSystemGenerated = true,
                CreatedAt = DateTime.UtcNow
            },
            new Playlist
            {
                Id = 2,
                UserId = userId,
                PlaylistName = "My Playlist",
                IsSystemGenerated = false,
                CreatedAt = DateTime.UtcNow
            }
        };

        MockPlaylistService.Setup(x => x.GetUserPlaylistsAsync(userId)).ReturnsAsync(playlists);
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(It.IsAny<int>())).ReturnsAsync(new List<UserPlaylist>());
        MockSubscriptionService.Setup(x => x.HasActiveSubscriptionAsync(userId)).ReturnsAsync(false);
        MockCartService.Setup(x => x.GetOwnedSongsAsync(userId)).ReturnsAsync(new List<string>());
        MockRecommendationService.Setup(x => x.GetRecommendedPlaylistAsync(userId)).ReturnsAsync(new List<RecommendedPlaylist>());

        // Act
        var cut = TestContext.Render<MyPlaylists>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Liked Songs"));
        Assert.That(cut.Markup, Does.Contain("My Playlist"));
    }

    [Test]
    public void MyPlaylists_LikedSongsPlaylist_NoEditDeleteButtons()
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

        var likedSongsPlaylist = new Playlist
        {
            Id = 1,
            UserId = userId,
            PlaylistName = "Liked Songs",
            IsSystemGenerated = true,
            CreatedAt = DateTime.UtcNow
        };

        MockPlaylistService.Setup(x => x.GetUserPlaylistsAsync(userId)).ReturnsAsync(new List<Playlist> { likedSongsPlaylist });
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(1)).ReturnsAsync(new List<UserPlaylist>());
        MockSubscriptionService.Setup(x => x.HasActiveSubscriptionAsync(userId)).ReturnsAsync(false);
        MockCartService.Setup(x => x.GetOwnedSongsAsync(userId)).ReturnsAsync(new List<string>());
        MockRecommendationService.Setup(x => x.GetRecommendedPlaylistAsync(userId)).ReturnsAsync(new List<RecommendedPlaylist>());

        // Act
        var cut = TestContext.Render<MyPlaylists>();

        // Assert - Liked Songs card should not contain the actions div with edit/delete buttons
        var markup = cut.Markup;
        Assert.That(markup, Does.Contain("Liked Songs"));
        
        // Check that the edit/delete buttons are not present for the system-generated playlist
        // The markup should contain the card but NOT the playlists-page-actions div with buttons
        var likedSongsCard = cut.FindAll(".liked-songs-playlist-card");
        Assert.That(likedSongsCard, Has.Count.EqualTo(1), "Liked Songs playlist card should be rendered");
        
        // Verify no edit/delete buttons in the Liked Songs card
        var editButtons = likedSongsCard[0].QuerySelectorAll(".fa-edit");
        var deleteButtons = likedSongsCard[0].QuerySelectorAll(".fa-trash");
        Assert.That(editButtons.Length, Is.EqualTo(0), "Liked Songs playlist should not have edit button");
        Assert.That(deleteButtons.Length, Is.EqualTo(0), "Liked Songs playlist should not have delete button");
    }

    [Test]
    public void MyPlaylists_RegularPlaylist_HasEditDeleteButtons()
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

        var regularPlaylist = new Playlist
        {
            Id = 2,
            UserId = userId,
            PlaylistName = "My Playlist",
            IsSystemGenerated = false,
            CreatedAt = DateTime.UtcNow
        };

        MockPlaylistService.Setup(x => x.GetUserPlaylistsAsync(userId)).ReturnsAsync(new List<Playlist> { regularPlaylist });
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(2)).ReturnsAsync(new List<UserPlaylist>());
        MockSubscriptionService.Setup(x => x.HasActiveSubscriptionAsync(userId)).ReturnsAsync(false);
        MockCartService.Setup(x => x.GetOwnedSongsAsync(userId)).ReturnsAsync(new List<string>());
        MockRecommendationService.Setup(x => x.GetRecommendedPlaylistAsync(userId)).ReturnsAsync(new List<RecommendedPlaylist>());

        // Act
        var cut = TestContext.Render<MyPlaylists>();

        // Assert
        var markup = cut.Markup;
        Assert.That(markup, Does.Contain("My Playlist"));
        
        // Regular playlists should have edit/delete buttons
        var editButtons = cut.FindAll(".fa-edit");
        var deleteButtons = cut.FindAll(".fa-trash");
        Assert.That(editButtons, Has.Count.GreaterThan(0), "Regular playlist should have edit button");
        Assert.That(deleteButtons, Has.Count.GreaterThan(0), "Regular playlist should have delete button");
    }
}
