using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using MusicSalesApp.Components.Pages.Public;
using MusicSalesApp.Components.Players;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Net;
using System.Net.Http;
using System.Security.Claims;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class PlaylistPlayerTests : BUnitTestBase
{
    private Mock<IJSRuntime> _mockJsRuntime;
    private Mock<IJSObjectReference> _mockJsModule;
    private StubHttpMessageHandler _httpHandler;
    private HttpClient _httpClient;

    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        _mockJsRuntime = new Mock<IJSRuntime>();
        _mockJsModule = new Mock<IJSObjectReference>();

        // Mock JS module import
        _mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(
                "import",
                It.IsAny<object[]>()))
            .ReturnsAsync(_mockJsModule.Object);

        TestContext.Services.AddSingleton<IJSRuntime>(_mockJsRuntime.Object);

        // Setup HTTP client with stub handler
        _httpHandler = new StubHttpMessageHandler();
        _httpClient = new HttpClient(_httpHandler) { BaseAddress = new Uri("http://localhost/") };

        // Setup default responses for API endpoints
        _httpHandler.SetupJsonResponse(new Uri("http://localhost/api/cart/owned"), Array.Empty<string>());
        _httpHandler.SetupJsonResponse(new Uri("http://localhost/api/cart"), new { Items = Array.Empty<object>(), Total = 0 });

        TestContext.Services.AddSingleton<HttpClient>(_httpClient);
    }

    [TearDown]
    public override void BaseTearDown()
    {
        _httpClient?.Dispose();
        _httpHandler?.Dispose();
        base.BaseTearDown();
    }

    [Test]
    public void PlaylistPlayer_ShowsErrorState_WhenNoPlaylistId()
    {
        // Arrange - Setup authorized user
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");

        var appUser = new ApplicationUser { Id = 1, Email = "testuser@test.com", UserName = "testuser@test.com" };
        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(appUser);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<PlaylistPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, null));

        // Wait for error state to appear
        cut.WaitForState(() => cut.Markup.Contains("No playlist ID provided"), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("No playlist ID provided"));
    }

    [Test]
    public void PlaylistPlayer_RequiresAuthentication()
    {
        // Arrange - User is not authenticated (default state from BUnitTestBase)
        
        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<PlaylistPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 1));

        // Wait for error state to appear
        cut.WaitForState(() => cut.Markup.Contains("logged in"), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("You must be logged in to view playlists"));
    }

    [Test]
    public void PlaylistPlayer_ShowsErrorState_WhenPlaylistNotFound()
    {
        // Arrange - Setup authorized user
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");

        var appUser = new ApplicationUser { Id = 1, Email = "testuser@test.com", UserName = "testuser@test.com" };
        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(appUser);

        MockPlaylistService.Setup(x => x.GetPlaylistByIdAsync(999))
            .ReturnsAsync((Playlist)null);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<PlaylistPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 999));

        // Wait for error state to appear
        cut.WaitForState(() => cut.Markup.Contains("Playlist not found"), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("Playlist not found"));
    }

    [Test]
    public void PlaylistPlayer_ShowsErrorState_WhenPlaylistIsEmpty()
    {
        // Arrange - Setup authorized user
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");

        var appUser = new ApplicationUser { Id = 1, Email = "testuser@test.com", UserName = "testuser@test.com" };
        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(appUser);

        var playlist = new Playlist
        {
            Id = 1,
            UserId = 1,
            PlaylistName = "Empty Playlist"
        };

        MockPlaylistService.Setup(x => x.GetPlaylistByIdAsync(1))
            .ReturnsAsync(playlist);
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(1))
            .ReturnsAsync(new List<UserPlaylist>());

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<PlaylistPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 1));

        // Wait for error state to appear
        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("This playlist is empty"));
    }

    [Test]
    [Ignore("Skipped: bUnit does not reliably trigger OnAfterRenderAsync data loading. This test requires component refactoring to use a different lifecycle pattern.")]
    public void PlaylistPlayer_ShowsPlaylistName()
    {
        // This test validates playlist name display in PlaylistPlayer.
        // Currently skipped because the component loads data in OnAfterRenderAsync,
        // which doesn't execute properly in bUnit's synchronous test model.
        
        // Arrange - Setup authorized user
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");

        var appUser = new ApplicationUser { Id = 1, Email = "testuser@test.com", UserName = "testuser@test.com" };
        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(appUser);

        // Setup playlist data
        var playlist = new Playlist
        {
            Id = 1,
            UserId = 1,
            PlaylistName = "My Test Playlist"
        };

        var songMetadata = new SongMetadata
        {
            Id = 1,
            Mp3BlobPath = "music/song1.mp3",
            ImageBlobPath = "music/song1.jpg",
            TrackLength = 180.0,
            Genre = "Rock"
        };

        var userPlaylist = new UserPlaylist
        {
            Id = 1,
            PlaylistId = 1,
            UserId = 1,
            SongMetadataId = 1,
            SongMetadata = songMetadata,
            Playlist = playlist
        };

        MockPlaylistService.Setup(x => x.GetPlaylistByIdAsync(1))
            .ReturnsAsync(playlist);
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(1))
            .ReturnsAsync(new List<UserPlaylist> { userPlaylist });

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<PlaylistPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 1));

        // Wait for rendering to complete
        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("My Test Playlist"));
    }

    [Test]
    public void PlaylistPlayer_RecommendedMode_RequiresAuthentication()
    {
        // Arrange - User is not authenticated (default state from BUnitTestBase)
        
        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<PlaylistPlayer>(parameters => parameters
            .Add(p => p.RecommendedUserId, 1));

        // Wait for error state to appear
        cut.WaitForState(() => cut.Markup.Contains("logged in"), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("You must be logged in to view recommended playlists"));
    }

    [Test]
    public void PlaylistPlayer_RecommendedMode_OnlyShowsOwnRecommendations()
    {
        // Arrange - Setup authorized user
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");

        var appUser = new ApplicationUser { Id = 1, Email = "testuser@test.com", UserName = "testuser@test.com" };
        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(appUser);

        // Act - Try to view another user's recommendations
        SetupRendererInfo();
        var cut = TestContext.Render<PlaylistPlayer>(parameters => parameters
            .Add(p => p.RecommendedUserId, 999)); // Different user ID

        // Wait for error state to appear
        cut.WaitForState(() => cut.Markup.Contains("own recommended playlist"), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("You can only view your own recommended playlist"));
    }

    [Test]
    [Ignore("Skipped: bUnit does not reliably trigger OnAfterRenderAsync data loading. This test requires component refactoring to use a different lifecycle pattern.")]
    public void PlaylistPlayer_ShowsPreviewLabel_WhenNotSubscribed()
    {
        // Arrange - Setup authorized user
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");

        var appUser = new ApplicationUser { Id = 1, Email = "testuser@test.com", UserName = "testuser@test.com" };
        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(appUser);

        // Setup playlist data
        var playlist = new Playlist
        {
            Id = 1,
            UserId = 1,
            PlaylistName = "My Test Playlist"
        };

        var songMetadata = new SongMetadata
        {
            Id = 1,
            Mp3BlobPath = "music/song1.mp3",
            ImageBlobPath = "music/song1.jpg",
            TrackLength = 180.0,
            Genre = "Rock"
        };

        var userPlaylist = new UserPlaylist
        {
            Id = 1,
            PlaylistId = 1,
            UserId = 1,
            SongMetadataId = 1,
            SongMetadata = songMetadata,
            Playlist = playlist
        };

        MockPlaylistService.Setup(x => x.GetPlaylistByIdAsync(1))
            .ReturnsAsync(playlist);
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(1))
            .ReturnsAsync(new List<UserPlaylist> { userPlaylist });

        // Not subscribed (default)
        MockSubscriptionService.Setup(x => x.HasActiveSubscriptionAsync(1))
            .ReturnsAsync(false);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<PlaylistPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 1));

        // Wait for rendering to complete
        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        // Assert - Should show preview label
        Assert.That(cut.Markup, Does.Contain("Preview Only"));
    }

    [Test]
    public void PlaylistPlayer_DeniesAccessToOtherUsersPlaylist()
    {
        // Arrange - Setup authorized user
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");

        var appUser = new ApplicationUser { Id = 1, Email = "testuser@test.com", UserName = "testuser@test.com" };
        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(appUser);

        // Playlist belongs to a different user
        var playlist = new Playlist
        {
            Id = 1,
            UserId = 999, // Different user
            PlaylistName = "Other User's Playlist"
        };

        MockPlaylistService.Setup(x => x.GetPlaylistByIdAsync(1))
            .ReturnsAsync(playlist);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<PlaylistPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 1));

        // Wait for error state to appear
        cut.WaitForState(() => cut.Markup.Contains("do not have access"), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("You do not have access to this playlist"));
    }

    [Test]
    public void PlaylistPlayer_BindsTipStatusFromQueryString()
    {
        // Arrange – navigate to a URL that contains tip return query params
        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/playlist/1?tip_status=approved&token=EC-TESTTOKEN123");

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<PlaylistPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 1));

        // Assert – the routable wrapper should have bound both query params
        var instance = cut.Instance;
        Assert.That(instance.TipStatus, Is.EqualTo("approved"));
        Assert.That(instance.TipPayPalToken, Is.EqualTo("EC-TESTTOKEN123"));
    }

    [Test]
    public void PlaylistPlayer_ForwardsTipParamsToInteractiveChild()
    {
        // Arrange
        var nav = TestContext.Services.GetService<NavigationManager>();
        nav.NavigateTo("/playlist/1?tip_status=cancelled&token=EC-CANCELTOKEN456");

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<PlaylistPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 1));

        // Assert – the interactive child should receive the params as [Parameter] values
        var child = cut.FindComponent<PlaylistPlayerInteractive>();
        Assert.That(child.Instance.TipStatus, Is.EqualTo("cancelled"));
        Assert.That(child.Instance.TipPayPalToken, Is.EqualTo("EC-CANCELTOKEN456"));
    }
}
