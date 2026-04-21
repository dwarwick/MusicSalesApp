using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Hubs;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class PlaylistServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<ILogger<PlaylistService>> _mockLogger;
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<ISongLikeService> _mockSongLikeService;
    private PlaylistService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        // Create in-memory database for testing
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"PlaylistTestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);

        _mockLogger = new Mock<ILogger<PlaylistService>>();
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockSongLikeService = new Mock<ISongLikeService>();
        
        // By default, mock subscription service to return false (no subscription)
        _mockSubscriptionService
            .Setup(s => s.HasActiveSubscriptionAsync(It.IsAny<int>()))
            .ReturnsAsync(false);
        
        // Mock the context factory to return our in-memory context
        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _service = new PlaylistService(
            _mockContextFactory.Object, 
            _mockLogger.Object, 
            _mockSubscriptionService.Object,
            _mockSongLikeService.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task CreatePlaylistAsync_CreatesNewPlaylist()
    {
        // Arrange
        var userId = 1;
        var playlistName = "My Test Playlist";

        // Act
        var result = await _service.CreatePlaylistAsync(userId, playlistName);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.PlaylistName, Is.EqualTo(playlistName));
        Assert.That(result.UserId, Is.EqualTo(userId));
        Assert.That(result.Id, Is.GreaterThan(0));

        // Verify it was saved to the database
        var savedPlaylist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == result.Id);
        Assert.That(savedPlaylist, Is.Not.Null);
        Assert.That(savedPlaylist.PlaylistName, Is.EqualTo(playlistName));
    }

    [Test]
    public async Task GetUserPlaylistsAsync_ReturnsUserPlaylists()
    {
        // Arrange
        var userId = 1;
        var playlist1 = new Playlist { UserId = userId, PlaylistName = "Playlist 1" };
        var playlist2 = new Playlist { UserId = userId, PlaylistName = "Playlist 2" };
        var playlist3 = new Playlist { UserId = 2, PlaylistName = "Other User Playlist" };

        await _context.Playlists.AddRangeAsync(playlist1, playlist2, playlist3);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetUserPlaylistsAsync(userId);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.All(p => p.UserId == userId), Is.True);
    }

    [Test]
    public async Task GetPlaylistByIdAsync_ReturnsCorrectPlaylist()
    {
        // Arrange
        var playlist = new Playlist { UserId = 1, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetPlaylistByIdAsync(playlist.Id);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Id, Is.EqualTo(playlist.Id));
        Assert.That(result.PlaylistName, Is.EqualTo(playlist.PlaylistName));
    }

    [Test]
    public async Task UpdatePlaylistAsync_UpdatesPlaylistName()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Original Name" };
        await _context.Playlists.AddAsync(playlist);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear(); // Clear the tracking to avoid caching issues

        var newName = "Updated Name";

        // Act
        var result = await _service.UpdatePlaylistAsync(playlist.Id, userId, newName);

        // Assert
        Assert.That(result, Is.True);

        // Create new context to verify the update
        using var verifyContext = new AppDbContext(_contextOptions);
        var updated = await verifyContext.Playlists.FindAsync(playlist.Id);
        Assert.That(updated.PlaylistName, Is.EqualTo(newName));
    }

    [Test]
    public async Task UpdatePlaylistAsync_ReturnsFalse_WhenUserDoesNotOwnPlaylist()
    {
        // Arrange
        var playlist = new Playlist { UserId = 1, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);
        await _context.SaveChangesAsync();

        // Act - Try to update with different user
        var result = await _service.UpdatePlaylistAsync(playlist.Id, 2, "New Name");

        // Assert
        Assert.That(result, Is.False);

        var unchanged = await _context.Playlists.FindAsync(playlist.Id);
        Assert.That(unchanged.PlaylistName, Is.EqualTo("Test Playlist"));
    }

    [Test]
    public async Task DeletePlaylistAsync_DeletesPlaylist()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        var result = await _service.DeletePlaylistAsync(playlist.Id, userId);

        // Assert
        Assert.That(result, Is.True);

        // Create new context to verify the deletion
        using var verifyContext = new AppDbContext(_contextOptions);
        var deleted = await verifyContext.Playlists.FindAsync(playlist.Id);
        Assert.That(deleted, Is.Null);
    }

    [Test]
    public async Task DeletePlaylistAsync_ReturnsFalse_WhenUserDoesNotOwnPlaylist()
    {
        // Arrange
        var playlist = new Playlist { UserId = 1, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);
        await _context.SaveChangesAsync();

        // Act - Try to delete with different user
        var result = await _service.DeletePlaylistAsync(playlist.Id, 2);

        // Assert
        Assert.That(result, Is.False);

        var notDeleted = await _context.Playlists.FindAsync(playlist.Id);
        Assert.That(notDeleted, Is.Not.Null);
    }

    [Test]
    public async Task AddSongToPlaylistAsync_AddsSongSuccessfully()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        var songMetadata = new SongMetadata 
        { 
            Mp3BlobPath = "test.mp3", 
            IsAlbumCover = false,
            IsActive = true
        };
        await _context.SongMetadata.AddAsync(songMetadata);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.AddSongToPlaylistAsync(userId, playlist.Id, songMetadata.Id);

        // Assert
        Assert.That(result, Is.True);

        var userPlaylist = await _context.UserPlaylists
            .FirstOrDefaultAsync(up => up.PlaylistId == playlist.Id && up.SongMetadataId == songMetadata.Id);
        Assert.That(userPlaylist, Is.Not.Null);
    }

    [Test]
    public async Task AddSongToPlaylistAsync_ReturnsFalse_WhenSongIsAlbumCover()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        var songMetadata = new SongMetadata 
        { 
            ImageBlobPath = "cover.jpg", 
            IsAlbumCover = true // Album cover should not be added
        };
        await _context.SongMetadata.AddAsync(songMetadata);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.AddSongToPlaylistAsync(userId, playlist.Id, songMetadata.Id);

        // Assert
        Assert.That(result, Is.False);

        var userPlaylist = await _context.UserPlaylists
            .FirstOrDefaultAsync(up => up.PlaylistId == playlist.Id && up.SongMetadataId == songMetadata.Id);
        Assert.That(userPlaylist, Is.Null);
    }

    [Test]
    public async Task RemoveSongFromPlaylistAsync_RemovesSongSuccessfully()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        var songMetadata = new SongMetadata { Mp3BlobPath = "test.mp3", IsAlbumCover = false };
        await _context.SongMetadata.AddAsync(songMetadata);

        var userPlaylist = new UserPlaylist 
        { 
            UserId = userId, 
            PlaylistId = playlist.Id, 
            SongMetadataId = songMetadata.Id 
        };
        await _context.UserPlaylists.AddAsync(userPlaylist);

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        var result = await _service.RemoveSongFromPlaylistAsync(playlist.Id, userPlaylist.Id, userId);

        // Assert
        Assert.That(result, Is.True);

        // Create new context to verify the deletion
        using var verifyContext = new AppDbContext(_contextOptions);
        var removed = await verifyContext.UserPlaylists.FindAsync(userPlaylist.Id);
        Assert.That(removed, Is.Null);
    }

    [Test]
    public async Task GetPlaylistSongsAsync_ReturnsPlaylistSongs()
    {
        // Arrange
        var playlist = new Playlist { UserId = 1, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        var songMetadata1 = new SongMetadata { Mp3BlobPath = "song1.mp3", IsAlbumCover = false };
        var songMetadata2 = new SongMetadata { Mp3BlobPath = "song2.mp3", IsAlbumCover = false };
        await _context.SongMetadata.AddRangeAsync(songMetadata1, songMetadata2);

        var userPlaylist1 = new UserPlaylist { UserId = 1, PlaylistId = playlist.Id, SongMetadataId = songMetadata1.Id };
        var userPlaylist2 = new UserPlaylist { UserId = 1, PlaylistId = playlist.Id, SongMetadataId = songMetadata2.Id };
        await _context.UserPlaylists.AddRangeAsync(userPlaylist1, userPlaylist2);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetPlaylistSongsAsync(playlist.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.All(up => up.PlaylistId == playlist.Id), Is.True);
    }

    [Test]
    public async Task CanAddSongToPlaylistAsync_ReturnsTrue_WhenSongIsNotAlbumCover()
    {
        // Arrange
        var songMetadata = new SongMetadata { Mp3BlobPath = "test.mp3", IsAlbumCover = false };
        await _context.SongMetadata.AddAsync(songMetadata);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.CanAddSongToPlaylistAsync(songMetadata.Id);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task CanAddSongToPlaylistAsync_ReturnsFalse_WhenSongIsAlbumCover()
    {
        // Arrange
        var songMetadata = new SongMetadata { ImageBlobPath = "cover.jpg", IsAlbumCover = true };
        await _context.SongMetadata.AddAsync(songMetadata);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.CanAddSongToPlaylistAsync(songMetadata.Id);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task GetAvailableSongsForPlaylistAsync_ReturnsEmptyList_WhenNoSubscription()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        var song1Metadata = new SongMetadata { Mp3BlobPath = "song1.mp3", IsAlbumCover = false, IsActive = true };
        var song2Metadata = new SongMetadata { Mp3BlobPath = "song2.mp3", IsAlbumCover = false, IsActive = true };
        await _context.SongMetadata.AddRangeAsync(song1Metadata, song2Metadata);
        await _context.SaveChangesAsync();

        // By default, subscription service returns false (no subscription)

        // Act
        var result = await _service.GetAvailableSongsForPlaylistAsync(userId, playlist.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(0)); // Empty list when no subscription
    }

    [Test]
    public async Task GetAvailableSongsForPlaylistAsync_WithSubscription_ReturnsAllCatalogSongs()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        // Songs exist in catalog
        var song1Metadata = new SongMetadata { Mp3BlobPath = "albums/album1/song1.mp3", IsAlbumCover = false, IsActive = true };
        var song2Metadata = new SongMetadata { Mp3BlobPath = "albums/album2/song2.mp3", IsAlbumCover = false, IsActive = true };
        var song3Metadata = new SongMetadata { Mp3BlobPath = "albums/album3/song3.mp3", IsAlbumCover = false, IsActive = true };
        await _context.SongMetadata.AddRangeAsync(song1Metadata, song2Metadata, song3Metadata);
        await _context.SaveChangesAsync();

        // Setup subscription service to return true
        _mockSubscriptionService
            .Setup(s => s.HasActiveSubscriptionAsync(userId))
            .ReturnsAsync(true);

        // Act
        var result = await _service.GetAvailableSongsForPlaylistAsync(userId, playlist.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task GetAvailableSongsForPlaylistAsync_WithSubscription_ExcludesAlbumCovers()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        // Add songs and album covers to catalog
        var songMetadata = new SongMetadata { Mp3BlobPath = "albums/album1/song1.mp3", IsAlbumCover = false, IsActive = true };
        var albumCoverMetadata = new SongMetadata { ImageBlobPath = "albums/album1/cover.jpg", IsAlbumCover = true, IsActive = true };
        await _context.SongMetadata.AddRangeAsync(songMetadata, albumCoverMetadata);
        await _context.SaveChangesAsync();

        // Setup subscription
        _mockSubscriptionService
            .Setup(s => s.HasActiveSubscriptionAsync(userId))
            .ReturnsAsync(true);

        // Act
        var result = await _service.GetAvailableSongsForPlaylistAsync(userId, playlist.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1)); // Only song, not album cover
        Assert.That(result[0].IsAlbumCover, Is.False);
    }

    [Test]
    public async Task GetAvailableSongsForPlaylistAsync_WithSubscription_ExcludesSongsAlreadyInPlaylist()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        // Add songs to catalog
        var song1Metadata = new SongMetadata { Mp3BlobPath = "albums/album1/song1.mp3", IsAlbumCover = false, IsActive = true };
        var song2Metadata = new SongMetadata { Mp3BlobPath = "albums/album2/song2.mp3", IsAlbumCover = false, IsActive = true };
        await _context.SongMetadata.AddRangeAsync(song1Metadata, song2Metadata);
        await _context.SaveChangesAsync();

        // Add song1 to playlist
        var userPlaylist = new UserPlaylist 
        { 
            UserId = userId, 
            PlaylistId = playlist.Id, 
            SongMetadataId = song1Metadata.Id 
        };
        await _context.UserPlaylists.AddAsync(userPlaylist);
        await _context.SaveChangesAsync();

        // Setup subscription
        _mockSubscriptionService
            .Setup(s => s.HasActiveSubscriptionAsync(userId))
            .ReturnsAsync(true);

        // Act
        var result = await _service.GetAvailableSongsForPlaylistAsync(userId, playlist.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1)); // Only song2 should be available
        Assert.That(result[0].Id, Is.EqualTo(song2Metadata.Id));
    }

    [Test]
    public async Task GetPlaylistSongsAsync_ExcludesInactiveSongs()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "test@test.com", Email = "test@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var activeSong = new SongMetadata { Mp3BlobPath = "active.mp3", IsActive = true, IsEnabled = true };
        var inactiveSong = new SongMetadata { Mp3BlobPath = "inactive.mp3", IsActive = false, IsEnabled = true };
        _context.SongMetadata.AddRange(activeSong, inactiveSong);
        await _context.SaveChangesAsync();

        var playlist = new Playlist { UserId = user.Id, PlaylistName = "Test Playlist" };
        _context.Playlists.Add(playlist);
        await _context.SaveChangesAsync();

        _context.UserPlaylists.AddRange(
            new UserPlaylist { UserId = user.Id, PlaylistId = playlist.Id, SongMetadataId = activeSong.Id },
            new UserPlaylist { UserId = user.Id, PlaylistId = playlist.Id, SongMetadataId = inactiveSong.Id }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetPlaylistSongsAsync(playlist.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].SongMetadataId, Is.EqualTo(activeSong.Id));
    }

    [Test]
    public async Task CanAddSongToPlaylistAsync_ReturnsFalse_ForInactiveSong()
    {
        // Arrange
        var song = new SongMetadata
        {
            Mp3BlobPath = "test.mp3",
            IsActive = false,
            IsEnabled = true,
            IsAlbumCover = false
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.CanAddSongToPlaylistAsync(song.Id);

        // Assert
        Assert.That(result, Is.False, "Inactive songs should not be addable to playlists");
    }
}

// Tests for Liked Songs Playlist functionality
[TestFixture]
public class LikedSongsPlaylistTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<ILogger<PlaylistService>> _mockLogger;
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<ISongLikeService> _mockSongLikeService;
    private Mock<IHubContext<LikeCountHub>> _mockHubContext;
    private PlaylistService _playlistService;
    private SongLikeService _songLikeService;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        // Create in-memory database for testing
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"LikedSongsTestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);

        _mockLogger = new Mock<ILogger<PlaylistService>>();
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockSongLikeService = new Mock<ISongLikeService>();
        
        // Mock the context factory to return our in-memory context
        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        // Mock hub context for SongLikeService
        _mockHubContext = new Mock<IHubContext<LikeCountHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        // Create real SongLikeService for integration testing
        _songLikeService = new SongLikeService(_mockContextFactory.Object, _mockHubContext.Object);

        _playlistService = new PlaylistService(
            _mockContextFactory.Object, 
            _mockLogger.Object, 
            _mockSubscriptionService.Object,
            _songLikeService);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task GetOrCreateLikedSongsPlaylistAsync_CreatesNewPlaylist_WhenNotExists()
    {
        // Arrange
        var userId = 1;

        // Act
        var playlist = await _playlistService.GetOrCreateLikedSongsPlaylistAsync(userId);

        // Assert
        Assert.That(playlist, Is.Not.Null);
        Assert.That(playlist.PlaylistName, Is.EqualTo("Liked Songs"));
        Assert.That(playlist.UserId, Is.EqualTo(userId));
        Assert.That(playlist.IsSystemGenerated, Is.True);
        Assert.That(playlist.Id, Is.GreaterThan(0));
    }

    [Test]
    public async Task GetOrCreateLikedSongsPlaylistAsync_ReturnsExistingPlaylist_WhenExists()
    {
        // Arrange
        var userId = 1;
        var existingPlaylist = new Playlist
        {
            UserId = userId,
            PlaylistName = "Liked Songs",
            IsSystemGenerated = true,
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };
        await _context.Playlists.AddAsync(existingPlaylist);
        await _context.SaveChangesAsync();

        // Act
        var playlist = await _playlistService.GetOrCreateLikedSongsPlaylistAsync(userId);

        // Assert
        Assert.That(playlist, Is.Not.Null);
        Assert.That(playlist.Id, Is.EqualTo(existingPlaylist.Id));
        Assert.That(playlist.CreatedAt, Is.EqualTo(existingPlaylist.CreatedAt));
    }

    [Test]
    public async Task SyncLikedSongsPlaylistAsync_AddsLikedSongs()
    {
        // Arrange
        var userId = 1;

        // Create song metadata
        var song1 = new SongMetadata { Mp3BlobPath = "song1.mp3", IsAlbumCover = false };
        var song2 = new SongMetadata { Mp3BlobPath = "song2.mp3", IsAlbumCover = false };
        await _context.SongMetadata.AddRangeAsync(song1, song2);
        await _context.SaveChangesAsync();

        // User likes song1
        await _songLikeService.ToggleLikeAsync(userId, song1.Id);

        // Act
        await _playlistService.SyncLikedSongsPlaylistAsync(userId);

        // Assert
        var playlist = await _context.Playlists
            .FirstOrDefaultAsync(p => p.UserId == userId && p.IsSystemGenerated);
        Assert.That(playlist, Is.Not.Null);

        var playlistSongs = await _context.UserPlaylists
            .Include(up => up.SongMetadata)
            .Where(up => up.PlaylistId == playlist.Id)
            .ToListAsync();

        Assert.That(playlistSongs, Has.Count.EqualTo(1));
        Assert.That(playlistSongs[0].SongMetadataId, Is.EqualTo(song1.Id));
    }

    [Test]
    public async Task SyncLikedSongsPlaylistAsync_RemovesUnlikedSongs()
    {
        // Arrange
        var userId = 1;

        // Create song metadata
        var song1 = new SongMetadata { Mp3BlobPath = "song1.mp3", IsAlbumCover = false };
        await _context.SongMetadata.AddAsync(song1);
        await _context.SaveChangesAsync();

        // Like and sync
        await _songLikeService.ToggleLikeAsync(userId, song1.Id);
        await _playlistService.SyncLikedSongsPlaylistAsync(userId);

        // Verify song is in playlist
        var playlist = await _context.Playlists
            .FirstOrDefaultAsync(p => p.UserId == userId && p.IsSystemGenerated);
        var playlistSongs = await _context.UserPlaylists
            .Where(up => up.PlaylistId == playlist.Id)
            .ToListAsync();
        Assert.That(playlistSongs, Has.Count.EqualTo(1));

        // Unlike the song
        await _songLikeService.ToggleLikeAsync(userId, song1.Id);

        // Act
        await _playlistService.SyncLikedSongsPlaylistAsync(userId);

        // Assert
        playlistSongs = await _context.UserPlaylists
            .Where(up => up.PlaylistId == playlist.Id)
            .ToListAsync();
        Assert.That(playlistSongs, Has.Count.EqualTo(0));
    }

    [Test]
    public async Task UpdatePlaylistAsync_ReturnsFalse_ForSystemGeneratedPlaylist()
    {
        // Arrange
        var userId = 1;
        var systemPlaylist = new Playlist
        {
            UserId = userId,
            PlaylistName = "Liked Songs",
            IsSystemGenerated = true
        };
        await _context.Playlists.AddAsync(systemPlaylist);
        await _context.SaveChangesAsync();

        // Act
        var result = await _playlistService.UpdatePlaylistAsync(systemPlaylist.Id, userId, "New Name");

        // Assert
        Assert.That(result, Is.False);

        // Verify name didn't change
        var playlist = await _context.Playlists.FindAsync(systemPlaylist.Id);
        Assert.That(playlist.PlaylistName, Is.EqualTo("Liked Songs"));
    }

    [Test]
    public async Task DeletePlaylistAsync_ReturnsFalse_ForSystemGeneratedPlaylist()
    {
        // Arrange
        var userId = 1;
        var systemPlaylist = new Playlist
        {
            UserId = userId,
            PlaylistName = "Liked Songs",
            IsSystemGenerated = true
        };
        await _context.Playlists.AddAsync(systemPlaylist);
        await _context.SaveChangesAsync();

        // Act
        var result = await _playlistService.DeletePlaylistAsync(systemPlaylist.Id, userId);

        // Assert
        Assert.That(result, Is.False);

        // Verify playlist still exists
        var playlist = await _context.Playlists.FindAsync(systemPlaylist.Id);
        Assert.That(playlist, Is.Not.Null);
    }

    [Test]
    public async Task GetUserLikedSongIdsAsync_ReturnsOnlyLikedSongs()
    {
        // Arrange
        var userId = 1;
        var song1 = new SongMetadata { Mp3BlobPath = "song1.mp3" };
        var song2 = new SongMetadata { Mp3BlobPath = "song2.mp3" };
        var song3 = new SongMetadata { Mp3BlobPath = "song3.mp3" };
        await _context.SongMetadata.AddRangeAsync(song1, song2, song3);
        await _context.SaveChangesAsync();

        // Like song1 and song2, dislike song3
        await _songLikeService.ToggleLikeAsync(userId, song1.Id);
        await _songLikeService.ToggleLikeAsync(userId, song2.Id);
        await _songLikeService.ToggleDislikeAsync(userId, song3.Id);

        // Act
        var likedSongIds = await _songLikeService.GetUserLikedSongIdsAsync(userId);

        // Assert
        Assert.That(likedSongIds, Has.Count.EqualTo(2));
        Assert.That(likedSongIds, Contains.Item(song1.Id));
        Assert.That(likedSongIds, Contains.Item(song2.Id));
        Assert.That(likedSongIds, Does.Not.Contain(song3.Id));
    }
}

// Tests for SortOrder and drag-reorder functionality (added with MAUI playlists feature)
[TestFixture]
public class PlaylistReorderTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<ILogger<PlaylistService>> _mockLogger;
    private Mock<ISubscriptionService> _mockSubscriptionService;
    private Mock<ISongLikeService> _mockSongLikeService;
    private PlaylistService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"ReorderTestDb_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(_contextOptions);

        _mockLogger = new Mock<ILogger<PlaylistService>>();
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockSongLikeService = new Mock<ISongLikeService>();

        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _service = new PlaylistService(
            _mockContextFactory.Object,
            _mockLogger.Object,
            _mockSubscriptionService.Object,
            _mockSongLikeService.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private async Task<(Playlist playlist, List<SongMetadata> songs)> SeedPlaylistWithSongsAsync(int userId, int songCount)
    {
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _context.Playlists.Add(playlist);
        await _context.SaveChangesAsync();

        var songs = new List<SongMetadata>();
        for (int i = 0; i < songCount; i++)
        {
            var song = new SongMetadata
            {
                Mp3BlobPath = $"song{i}.mp3",
                IsActive = true,
                IsEnabled = true,
                IsAlbumCover = false,
                SongTitle = $"Song {i}"
            };
            _context.SongMetadata.Add(song);
            songs.Add(song);
        }
        await _context.SaveChangesAsync();
        return (playlist, songs);
    }

    [Test]
    public async Task AddSongToPlaylistAsync_AssignsIncrementingSortOrder()
    {
        var userId = 1;
        var (playlist, songs) = await SeedPlaylistWithSongsAsync(userId, 3);

        foreach (var song in songs)
        {
            var ok = await _service.AddSongToPlaylistAsync(userId, playlist.Id, song.Id);
            Assert.That(ok, Is.True);
        }

        var entries = await _context.UserPlaylists
            .Where(up => up.PlaylistId == playlist.Id)
            .OrderBy(up => up.Id)
            .ToListAsync();
        Assert.That(entries.Select(e => e.SortOrder), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public async Task GetPlaylistSongsAsync_ReturnsSongsInSortOrder()
    {
        var userId = 1;
        var (playlist, songs) = await SeedPlaylistWithSongsAsync(userId, 3);

        // Insert out of order
        _context.UserPlaylists.AddRange(
            new UserPlaylist { UserId = userId, PlaylistId = playlist.Id, SongMetadataId = songs[0].Id, SortOrder = 3 },
            new UserPlaylist { UserId = userId, PlaylistId = playlist.Id, SongMetadataId = songs[1].Id, SortOrder = 1 },
            new UserPlaylist { UserId = userId, PlaylistId = playlist.Id, SongMetadataId = songs[2].Id, SortOrder = 2 });
        await _context.SaveChangesAsync();

        var result = await _service.GetPlaylistSongsAsync(playlist.Id);
        Assert.That(result.Select(up => up.SongMetadataId),
            Is.EqualTo(new[] { songs[1].Id, songs[2].Id, songs[0].Id }));
    }

    [Test]
    public async Task ReorderPlaylistAsync_RewritesSortOrderSequentially()
    {
        var userId = 1;
        var (playlist, songs) = await SeedPlaylistWithSongsAsync(userId, 3);
        var up1 = new UserPlaylist { UserId = userId, PlaylistId = playlist.Id, SongMetadataId = songs[0].Id, SortOrder = 1 };
        var up2 = new UserPlaylist { UserId = userId, PlaylistId = playlist.Id, SongMetadataId = songs[1].Id, SortOrder = 2 };
        var up3 = new UserPlaylist { UserId = userId, PlaylistId = playlist.Id, SongMetadataId = songs[2].Id, SortOrder = 3 };
        _context.UserPlaylists.AddRange(up1, up2, up3);
        await _context.SaveChangesAsync();

        // Reverse order
        var ok = await _service.ReorderPlaylistAsync(playlist.Id, userId, new[] { up3.Id, up2.Id, up1.Id });
        Assert.That(ok, Is.True);

        using var verify = new AppDbContext(_contextOptions);
        var entries = await verify.UserPlaylists.Where(x => x.PlaylistId == playlist.Id).ToListAsync();
        Assert.That(entries.Single(x => x.Id == up3.Id).SortOrder, Is.EqualTo(1));
        Assert.That(entries.Single(x => x.Id == up2.Id).SortOrder, Is.EqualTo(2));
        Assert.That(entries.Single(x => x.Id == up1.Id).SortOrder, Is.EqualTo(3));
    }

    [Test]
    public async Task ReorderPlaylistAsync_RejectsForeignUser()
    {
        var ownerId = 1;
        var (playlist, songs) = await SeedPlaylistWithSongsAsync(ownerId, 2);
        var up1 = new UserPlaylist { UserId = ownerId, PlaylistId = playlist.Id, SongMetadataId = songs[0].Id, SortOrder = 1 };
        var up2 = new UserPlaylist { UserId = ownerId, PlaylistId = playlist.Id, SongMetadataId = songs[1].Id, SortOrder = 2 };
        _context.UserPlaylists.AddRange(up1, up2);
        await _context.SaveChangesAsync();

        // A different user tries to reorder
        var ok = await _service.ReorderPlaylistAsync(playlist.Id, userId: 999, new[] { up2.Id, up1.Id });
        Assert.That(ok, Is.False);
    }

    [Test]
    public async Task ReorderPlaylistAsync_RejectsIdsNotInPlaylist()
    {
        var userId = 1;
        var (playlist, songs) = await SeedPlaylistWithSongsAsync(userId, 2);
        var up1 = new UserPlaylist { UserId = userId, PlaylistId = playlist.Id, SongMetadataId = songs[0].Id, SortOrder = 1 };
        _context.UserPlaylists.Add(up1);
        await _context.SaveChangesAsync();

        var ok = await _service.ReorderPlaylistAsync(playlist.Id, userId, new[] { up1.Id, 9999 });
        Assert.That(ok, Is.False);
    }
}

// Tests for cleanup hardening added with MAUI playlists feature
[TestFixture]
public class PlaylistCleanupHardeningTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<ILogger<PlaylistCleanupService>> _mockLogger;
    private PlaylistCleanupService _service;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"CleanupTestDb_{Guid.NewGuid()}")
            .Options;

        _mockLogger = new Mock<ILogger<PlaylistCleanupService>>();
        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _service = new PlaylistCleanupService(_mockContextFactory.Object, _mockLogger.Object);
    }

    [Test]
    public async Task Cleanup_DeletesCustomPlaylists_PreservesSystemPlaylist_ForLapsedUser()
    {
        using var seed = new AppDbContext(_contextOptions);
        var userId = 42;
        seed.Subscriptions.Add(new Subscription
        {
            UserId = userId,
            Status = "CANCELLED",
            StartDate = DateTime.UtcNow.AddDays(-60),
            EndDate = DateTime.UtcNow.AddDays(-10)
        });
        var custom = new Playlist { UserId = userId, PlaylistName = "Mine", IsSystemGenerated = false };
        var system = new Playlist { UserId = userId, PlaylistName = "Liked Songs", IsSystemGenerated = true };
        seed.Playlists.AddRange(custom, system);
        await seed.SaveChangesAsync();

        seed.UserPlaylists.AddRange(
            new UserPlaylist { UserId = userId, PlaylistId = custom.Id, SongMetadataId = 1 },
            new UserPlaylist { UserId = userId, PlaylistId = system.Id, SongMetadataId = 2 });
        await seed.SaveChangesAsync();

        var removed = await _service.RemoveNonOwnedSongsFromLapsedSubscriptionsAsync();

        Assert.That(removed, Is.EqualTo(2));

        using var verify = new AppDbContext(_contextOptions);
        var playlists = await verify.Playlists.Where(p => p.UserId == userId).ToListAsync();
        Assert.That(playlists, Has.Count.EqualTo(1));
        Assert.That(playlists[0].IsSystemGenerated, Is.True);
        Assert.That(playlists[0].PlaylistName, Is.EqualTo("Liked Songs"));
        Assert.That(await verify.UserPlaylists.CountAsync(up => up.UserId == userId), Is.EqualTo(0));
    }

    [Test]
    public async Task Cleanup_LeavesActiveSubscriberUntouched()
    {
        using var seed = new AppDbContext(_contextOptions);
        var userId = 7;
        // Lapsed row is what makes cleanup look at the user...
        seed.Subscriptions.Add(new Subscription
        {
            UserId = userId,
            Status = "CANCELLED",
            EndDate = DateTime.UtcNow.AddDays(-5)
        });
        // ...but there's also an active subscription, so the user is skipped.
        seed.Subscriptions.Add(new Subscription
        {
            UserId = userId,
            Status = "ACTIVE",
            EndDate = DateTime.UtcNow.AddDays(30)
        });
        var custom = new Playlist { UserId = userId, PlaylistName = "Keep Me", IsSystemGenerated = false };
        seed.Playlists.Add(custom);
        await seed.SaveChangesAsync();
        seed.UserPlaylists.Add(new UserPlaylist { UserId = userId, PlaylistId = custom.Id, SongMetadataId = 1 });
        await seed.SaveChangesAsync();

        var removed = await _service.RemoveNonOwnedSongsFromLapsedSubscriptionsAsync();
        Assert.That(removed, Is.EqualTo(0));

        using var verify = new AppDbContext(_contextOptions);
        Assert.That(await verify.Playlists.CountAsync(p => p.UserId == userId), Is.EqualTo(1));
        Assert.That(await verify.UserPlaylists.CountAsync(up => up.UserId == userId), Is.EqualTo(1));
    }
}
