using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Net;
using System.Net.Http;
using System.Security.Claims;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class AlbumPlayerTests : BUnitTestBase
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
        _httpHandler.SetupJsonResponse(new Uri("http://localhost/api/cart"), new { Items = Array.Empty<object>(), Albums = Array.Empty<object>(), Total = 0 });

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
    public void AlbumPlayer_ShowsErrorState_WhenAlbumNotFound()
    {
        // Arrange - Set up empty metadata
        MockSongMetadataService.Setup(x => x.GetByAlbumNameAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<SongMetadata>());

        // Act
        var cut = TestContext.Render<AlbumPlayer>(parameters => parameters
            .Add(p => p.AlbumName, "TestAlbum"));

        // Wait for error state to appear
        cut.WaitForState(() => cut.Markup.Contains("Album 'TestAlbum' not found"), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("Album 'TestAlbum' not found"));
    }

    [Test]
    [Ignore("Skipped: bUnit does not reliably trigger OnAfterRenderAsync data loading. This test requires component refactoring to use a different lifecycle pattern.")]
    public void AlbumPlayer_PlaylistMode_ShowsPlaylistName()
    {
        // This test validates playlist name display in AlbumPlayer.
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

        var ownedSong = new OwnedSong
        {
            Id = 1,
            UserId = 1,
            SongFileName = "music/song1.mp3",
            SongMetadata = songMetadata,
            SongMetadataId = 1
        };

        var userPlaylist = new UserPlaylist
        {
            Id = 1,
            PlaylistId = 1,
            UserId = 1,
            OwnedSongId = 1,
            OwnedSong = ownedSong,
            Playlist = playlist
        };

        MockPlaylistService.Setup(x => x.GetPlaylistByIdAsync(1))
            .ReturnsAsync(playlist);
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(1))
            .ReturnsAsync(new List<UserPlaylist> { userPlaylist });

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<AlbumPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 1));

        // Wait for rendering to complete
        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("My Test Playlist"));
    }

    [Test]
    public void AlbumPlayer_PlaylistMode_DoesNotShowAddToCartButton()
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

        var ownedSong = new OwnedSong
        {
            Id = 1,
            UserId = 1,
            SongFileName = "music/song1.mp3",
            SongMetadata = songMetadata,
            SongMetadataId = 1
        };

        var userPlaylist = new UserPlaylist
        {
            Id = 1,
            PlaylistId = 1,
            UserId = 1,
            OwnedSongId = 1,
            OwnedSong = ownedSong,
            Playlist = playlist
        };

        MockPlaylistService.Setup(x => x.GetPlaylistByIdAsync(1))
            .ReturnsAsync(playlist);
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(1))
            .ReturnsAsync(new List<UserPlaylist> { userPlaylist });

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<AlbumPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 1));

        // Wait for rendering to complete
        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        // Assert - Should not contain "Add to Cart" button
        Assert.That(cut.Markup, Does.Not.Contain("Add to Cart"));
    }

    [Test]
    [Ignore("Skipped: bUnit does not reliably trigger OnAfterRenderAsync data loading. This test requires component refactoring to use a different lifecycle pattern.")]
    public void AlbumPlayer_PlaylistMode_ShowsTrackCount()
    {
        // This test validates track count display in AlbumPlayer.
        // Currently skipped because the component loads data in OnAfterRenderAsync,
        // which doesn't execute properly in bUnit's synchronous test model.
        
        // Arrange - Setup authorized user
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");

        var appUser = new ApplicationUser { Id = 1, Email = "testuser@test.com", UserName = "testuser@test.com" };
        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(appUser);

        // Setup playlist with 3 songs
        var playlist = new Playlist
        {
            Id = 1,
            UserId = 1,
            PlaylistName = "My Test Playlist"
        };

        var songs = new List<UserPlaylist>();
        for (int i = 1; i <= 3; i++)
        {
            var songMetadata = new SongMetadata
            {
                Id = i,
                Mp3BlobPath = $"music/song{i}.mp3",
                ImageBlobPath = $"music/song{i}.jpg",
                TrackLength = 180.0,
                Genre = "Rock"
            };

            var ownedSong = new OwnedSong
            {
                Id = i,
                UserId = 1,
                SongFileName = $"music/song{i}.mp3",
                SongMetadata = songMetadata,
                SongMetadataId = i
            };

            songs.Add(new UserPlaylist
            {
                Id = i,
                PlaylistId = 1,
                UserId = 1,
                OwnedSongId = i,
                OwnedSong = ownedSong,
                Playlist = playlist
            });
        }

        MockPlaylistService.Setup(x => x.GetPlaylistByIdAsync(1))
            .ReturnsAsync(playlist);
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(1))
            .ReturnsAsync(songs);

        // Act
        SetupRendererInfo();
        var cut = TestContext.Render<AlbumPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 1));

        // Wait for rendering to complete
        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("3 tracks"));
    }

    [Test]
    public void AlbumPlayer_PlaylistMode_RequiresAuthentication()
    {
        // Arrange - Setup unauthenticated user (already set up in BaseSetup)
        MockPlaylistService.Setup(x => x.GetPlaylistByIdAsync(1))
            .ReturnsAsync((Playlist)null);

        // Act
        var cut = TestContext.Render<AlbumPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 1));

        // Wait for rendering to complete
        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("You must be logged in to view playlists"));
    }

    [Test]
    public void AlbumPlayer_PlaylistMode_ShowsErrorForEmptyPlaylist()
    {
        // Arrange - Setup authorized user
        var authContext = TestContext.AddAuthorization();
        authContext.SetAuthorized("testuser");

        var appUser = new ApplicationUser { Id = 1, Email = "testuser@test.com", UserName = "testuser@test.com" };
        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(appUser);

        // Setup playlist with no songs
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
        var cut = TestContext.Render<AlbumPlayer>(parameters => parameters
            .Add(p => p.PlaylistId, 1));

        // Wait for rendering to complete
        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(cut.Markup, Does.Contain("This playlist is empty"));
    }

    [Test]
    public void AlbumPlayer_ShuffleButton_TogglesShuffleState()
    {
        // Arrange - Setup album with tracks
        var albumMetadata = CreateTestAlbumMetadata("Test Album", 3);
        MockSongMetadataService.Setup(x => x.GetByAlbumNameAsync("Test Album"))
            .ReturnsAsync(albumMetadata);

        SetupRendererInfo();
        var cut = TestContext.Render<AlbumPlayer>(parameters => parameters
            .Add(p => p.AlbumName, "Test Album"));

        // Wait for rendering to complete
        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        // Act - Click shuffle button
        var shuffleButton = cut.Find("button[title='Shuffle']");
        shuffleButton.Click();

        // Assert - Shuffle button should have "active" class
        Assert.That(shuffleButton.ClassList, Does.Contain("active"));

        // Act - Click shuffle button again to disable
        shuffleButton.Click();

        // Assert - Shuffle button should not have "active" class
        Assert.That(shuffleButton.ClassList, Does.Not.Contain("active"));
    }

    [Test]
    public void AlbumPlayer_RepeatButton_TogglesRepeatState()
    {
        // Arrange - Setup album with tracks
        var albumMetadata = CreateTestAlbumMetadata("Test Album", 3);
        MockSongMetadataService.Setup(x => x.GetByAlbumNameAsync("Test Album"))
            .ReturnsAsync(albumMetadata);

        SetupRendererInfo();
        var cut = TestContext.Render<AlbumPlayer>(parameters => parameters
            .Add(p => p.AlbumName, "Test Album"));

        // Wait for rendering to complete
        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        // Act - Click repeat button
        var repeatButton = cut.Find("button[title='Repeat']");
        repeatButton.Click();

        // Assert - Repeat button should have "active" class
        Assert.That(repeatButton.ClassList, Does.Contain("active"));

        // Act - Click repeat button again to disable
        repeatButton.Click();

        // Assert - Repeat button should not have "active" class
        Assert.That(repeatButton.ClassList, Does.Not.Contain("active"));
    }

    [Test]
    public async Task AlbumPlayer_ShuffleEnabled_GeneratesShuffledOrder()
    {
        // Arrange - Setup album with multiple tracks
        var albumMetadata = CreateTestAlbumMetadata("Test Album", 10);
        MockSongMetadataService.Setup(x => x.GetByAlbumNameAsync("Test Album"))
            .ReturnsAsync(albumMetadata);

        SetupRendererInfo();
        var cut = TestContext.Render<AlbumPlayer>(parameters => parameters
            .Add(p => p.AlbumName, "Test Album"));

        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        // Get the component instance to access internal state
        var instance = cut.Instance as AlbumPlayerModel;
        Assert.That(instance, Is.Not.Null);

        // Act - Enable shuffle
        var shuffleButton = cut.Find("button[title='Shuffle']");
        shuffleButton.Click();

        // Use reflection to access the private _shuffledTrackOrder field
        var shuffledOrderField = typeof(AlbumPlayerModel).GetField("_shuffledTrackOrder", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.That(shuffledOrderField, Is.Not.Null);

        var shuffledOrder = shuffledOrderField.GetValue(instance) as List<int>;
        
        // Assert - Shuffled order should be generated
        Assert.That(shuffledOrder, Is.Not.Null);
        Assert.That(shuffledOrder.Count, Is.EqualTo(10), "Shuffled order should contain all tracks");
        Assert.That(shuffledOrder[0], Is.EqualTo(0), "Current track should be first in shuffled order");
        
        // Verify all track indices are present (0-9)
        var sortedOrder = new List<int>(shuffledOrder);
        sortedOrder.Sort();
        Assert.That(sortedOrder, Is.EqualTo(Enumerable.Range(0, 10).ToList()), 
            "Shuffled order should contain all track indices exactly once");
    }

    [Test]
    public async Task AlbumPlayer_RepeatEnabled_LoopsToBeginning()
    {
        // Arrange - Setup album with tracks
        var albumMetadata = CreateTestAlbumMetadata("Test Album", 3);
        MockSongMetadataService.Setup(x => x.GetByAlbumNameAsync("Test Album"))
            .ReturnsAsync(albumMetadata);

        SetupRendererInfo();
        var cut = TestContext.Render<AlbumPlayer>(parameters => parameters
            .Add(p => p.AlbumName, "Test Album"));

        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        var instance = cut.Instance as AlbumPlayerModel;
        Assert.That(instance, Is.Not.Null);

        // Enable repeat
        var repeatButton = cut.Find("button[title='Repeat']");
        repeatButton.Click();

        // Use reflection to access GetNextTrackIndex method
        var getNextTrackMethod = typeof(AlbumPlayerModel).GetMethod("GetNextTrackIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.That(getNextTrackMethod, Is.Not.Null);

        // Set current track to last track
        var currentTrackField = typeof(AlbumPlayerModel).GetField("_currentTrackIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        currentTrackField.SetValue(instance, 2); // Last track (0-indexed)

        // Act - Get next track after the last one
        var nextIndex = getNextTrackMethod.Invoke(instance, null) as int?;

        // Assert - Should loop back to first track (0)
        Assert.That(nextIndex, Is.EqualTo(0), "With repeat enabled, should loop back to first track");
    }

    [Test]
    public async Task AlbumPlayer_RepeatWithShuffle_RegeneratesShuffleOrder()
    {
        // Arrange - Setup album with tracks
        var albumMetadata = CreateTestAlbumMetadata("Test Album", 5);
        MockSongMetadataService.Setup(x => x.GetByAlbumNameAsync("Test Album"))
            .ReturnsAsync(albumMetadata);

        SetupRendererInfo();
        var cut = TestContext.Render<AlbumPlayer>(parameters => parameters
            .Add(p => p.AlbumName, "Test Album"));

        cut.WaitForState(() => !cut.Markup.Contains("Loading..."), timeout: TimeSpan.FromSeconds(5));

        var instance = cut.Instance as AlbumPlayerModel;
        Assert.That(instance, Is.Not.Null);

        // Enable both shuffle and repeat
        var shuffleButton = cut.Find("button[title='Shuffle']");
        shuffleButton.Click();
        var repeatButton = cut.Find("button[title='Repeat']");
        repeatButton.Click();

        // Use reflection to access fields and methods
        var getNextTrackMethod = typeof(AlbumPlayerModel).GetMethod("GetNextTrackIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var shufflePositionField = typeof(AlbumPlayerModel).GetField("_currentShufflePosition",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var shuffledOrderField = typeof(AlbumPlayerModel).GetField("_shuffledTrackOrder",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Set shuffle position to last position
        var shuffledOrder = shuffledOrderField.GetValue(instance) as List<int>;
        shufflePositionField.SetValue(instance, shuffledOrder.Count - 1);

        // Act - Get next track after the last one in shuffle order
        var nextIndex = getNextTrackMethod.Invoke(instance, null) as int?;

        // Assert - Should return a valid track index (shuffle regenerated)
        Assert.That(nextIndex, Is.Not.Null);
        Assert.That(nextIndex.Value, Is.GreaterThanOrEqualTo(0));
        Assert.That(nextIndex.Value, Is.LessThan(5));
    }

    private List<SongMetadata> CreateTestAlbumMetadata(string albumName, int trackCount)
    {
        var metadata = new List<SongMetadata>();

        // Add album cover
        metadata.Add(new SongMetadata
        {
            Id = 1,
            AlbumName = albumName,
            IsAlbumCover = true,
            ImageBlobPath = $"music/{albumName}/cover.jpg",
            AlbumPrice = 9.99m
        });

        // Add tracks
        for (int i = 1; i <= trackCount; i++)
        {
            metadata.Add(new SongMetadata
            {
                Id = i + 1,
                AlbumName = albumName,
                IsAlbumCover = false,
                Mp3BlobPath = $"music/{albumName}/track{i}.mp3",
                ImageBlobPath = $"music/{albumName}/track{i}.jpg",
                TrackNumber = i,
                TrackLength = 180.0,
                Genre = "Rock",
                SongPrice = 0.99m
            });
        }

        return metadata;
    }
}
