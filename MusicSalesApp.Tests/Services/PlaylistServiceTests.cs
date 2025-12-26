using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
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
            IsAlbumCover = false 
        };
        await _context.SongMetadata.AddAsync(songMetadata);

        var ownedSong = new OwnedSong 
        { 
            UserId = userId, 
            SongFileName = "test.mp3",
            SongMetadataId = songMetadata.Id
        };
        await _context.OwnedSongs.AddAsync(ownedSong);

        await _context.SaveChangesAsync();

        // Need to reload to get navigation properties
        ownedSong = await _context.OwnedSongs
            .Include(os => os.SongMetadata)
            .FirstAsync(os => os.Id == ownedSong.Id);

        // Act
        var result = await _service.AddSongToPlaylistAsync(userId, playlist.Id, ownedSong.Id);

        // Assert
        Assert.That(result, Is.True);

        var userPlaylist = await _context.UserPlaylists
            .FirstOrDefaultAsync(up => up.PlaylistId == playlist.Id && up.OwnedSongId == ownedSong.Id);
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

        var ownedSong = new OwnedSong 
        { 
            UserId = userId, 
            SongFileName = "cover.jpg",
            SongMetadataId = songMetadata.Id
        };
        await _context.OwnedSongs.AddAsync(ownedSong);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.AddSongToPlaylistAsync(userId, playlist.Id, ownedSong.Id);

        // Assert
        Assert.That(result, Is.False);

        var userPlaylist = await _context.UserPlaylists
            .FirstOrDefaultAsync(up => up.PlaylistId == playlist.Id && up.OwnedSongId == ownedSong.Id);
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

        var ownedSong = new OwnedSong { UserId = userId, SongFileName = "test.mp3", SongMetadataId = songMetadata.Id };
        await _context.OwnedSongs.AddAsync(ownedSong);

        var userPlaylist = new UserPlaylist 
        { 
            UserId = userId, 
            PlaylistId = playlist.Id, 
            OwnedSongId = ownedSong.Id 
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

        var ownedSong1 = new OwnedSong { UserId = 1, SongFileName = "song1.mp3", SongMetadataId = songMetadata1.Id };
        var ownedSong2 = new OwnedSong { UserId = 1, SongFileName = "song2.mp3", SongMetadataId = songMetadata2.Id };
        await _context.OwnedSongs.AddRangeAsync(ownedSong1, ownedSong2);

        var userPlaylist1 = new UserPlaylist { UserId = 1, PlaylistId = playlist.Id, OwnedSongId = ownedSong1.Id };
        var userPlaylist2 = new UserPlaylist { UserId = 1, PlaylistId = playlist.Id, OwnedSongId = ownedSong2.Id };
        await _context.UserPlaylists.AddRangeAsync(userPlaylist1, userPlaylist2);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetPlaylistSongsAsync(playlist.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.All(up => up.PlaylistId == playlist.Id), Is.True);
    }

    [Test]
    public async Task CanAddSongToPlaylistAsync_ReturnsTrue_WhenUserOwnsSongAndNotAlbumCover()
    {
        // Arrange
        var userId = 1;
        var songMetadata = new SongMetadata { Mp3BlobPath = "test.mp3", IsAlbumCover = false };
        await _context.SongMetadata.AddAsync(songMetadata);

        var ownedSong = new OwnedSong { UserId = userId, SongFileName = "test.mp3", SongMetadataId = songMetadata.Id };
        await _context.OwnedSongs.AddAsync(ownedSong);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.CanAddSongToPlaylistAsync(userId, ownedSong.Id);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task CanAddSongToPlaylistAsync_ReturnsFalse_WhenSongIsAlbumCover()
    {
        // Arrange
        var userId = 1;
        var songMetadata = new SongMetadata { ImageBlobPath = "cover.jpg", IsAlbumCover = true };
        await _context.SongMetadata.AddAsync(songMetadata);

        var ownedSong = new OwnedSong { UserId = userId, SongFileName = "cover.jpg", SongMetadataId = songMetadata.Id };
        await _context.OwnedSongs.AddAsync(ownedSong);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.CanAddSongToPlaylistAsync(userId, ownedSong.Id);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanAddSongToPlaylistAsync_ReturnsFalse_WhenUserDoesNotOwnSong()
    {
        // Arrange
        var songMetadata = new SongMetadata { Mp3BlobPath = "test.mp3", IsAlbumCover = false };
        await _context.SongMetadata.AddAsync(songMetadata);

        var ownedSong = new OwnedSong { UserId = 1, SongFileName = "test.mp3", SongMetadataId = songMetadata.Id };
        await _context.OwnedSongs.AddAsync(ownedSong);

        await _context.SaveChangesAsync();

        // Act - Check with different user
        var result = await _service.CanAddSongToPlaylistAsync(2, ownedSong.Id);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task GetAvailableSongsForPlaylistAsync_ReturnsOnlyValidSongs()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        var song1Metadata = new SongMetadata { Mp3BlobPath = "song1.mp3", IsAlbumCover = false };
        var song2Metadata = new SongMetadata { Mp3BlobPath = "song2.mp3", IsAlbumCover = false };
        var albumCoverMetadata = new SongMetadata { ImageBlobPath = "cover.jpg", IsAlbumCover = true };
        await _context.SongMetadata.AddRangeAsync(song1Metadata, song2Metadata, albumCoverMetadata);

        var ownedSong1 = new OwnedSong { UserId = userId, SongFileName = "song1.mp3", SongMetadataId = song1Metadata.Id };
        var ownedSong2 = new OwnedSong { UserId = userId, SongFileName = "song2.mp3", SongMetadataId = song2Metadata.Id };
        var ownedAlbumCover = new OwnedSong { UserId = userId, SongFileName = "cover.jpg", SongMetadataId = albumCoverMetadata.Id };
        await _context.OwnedSongs.AddRangeAsync(ownedSong1, ownedSong2, ownedAlbumCover);

        // Add song1 to playlist
        var userPlaylist = new UserPlaylist { UserId = userId, PlaylistId = playlist.Id, OwnedSongId = ownedSong1.Id };
        await _context.UserPlaylists.AddAsync(userPlaylist);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableSongsForPlaylistAsync(userId, playlist.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1)); // Only song2 should be available
        Assert.That(result[0].Id, Is.EqualTo(ownedSong2.Id));
        Assert.That(result[0].SongMetadata.IsAlbumCover, Is.False);
    }

    [Test]
    public async Task GetAvailableSongsForPlaylistAsync_WithSubscription_ReturnsAllCatalogSongs()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        // User owns song1
        var song1Metadata = new SongMetadata { Mp3BlobPath = "albums/album1/song1.mp3", IsAlbumCover = false };
        await _context.SongMetadata.AddAsync(song1Metadata);
        var ownedSong1 = new OwnedSong 
        { 
            UserId = userId, 
            SongFileName = "song1.mp3", 
            SongMetadataId = song1Metadata.Id,
            PayPalOrderId = "ORDER123" // Purchased song
        };
        await _context.OwnedSongs.AddAsync(ownedSong1);

        // Song2 and Song3 exist in catalog but not owned by user
        var song2Metadata = new SongMetadata { Mp3BlobPath = "albums/album2/song2.mp3", IsAlbumCover = false };
        var song3Metadata = new SongMetadata { Mp3BlobPath = "albums/album3/song3.mp3", IsAlbumCover = false };
        await _context.SongMetadata.AddRangeAsync(song2Metadata, song3Metadata);

        await _context.SaveChangesAsync();

        // Setup subscription service to return true
        _mockSubscriptionService
            .Setup(s => s.HasActiveSubscriptionAsync(userId))
            .ReturnsAsync(true);

        // Act
        var result = await _service.GetAvailableSongsForPlaylistAsync(userId, playlist.Id);

        // Assert
        // Should have 3 songs: owned song1 + subscription-based song2 and song3
        Assert.That(result, Has.Count.EqualTo(3));
        
        // Verify song1 is the purchased one
        var song1Result = result.FirstOrDefault(s => s.SongMetadataId == song1Metadata.Id);
        Assert.That(song1Result, Is.Not.Null);
        Assert.That(song1Result.PayPalOrderId, Is.EqualTo("ORDER123"));

        // Verify song2 and song3 were created as subscription-based (no PayPalOrderId)
        var song2Result = result.FirstOrDefault(s => s.SongMetadataId == song2Metadata.Id);
        Assert.That(song2Result, Is.Not.Null);
        Assert.That(song2Result.PayPalOrderId, Is.Null);
        Assert.That(song2Result.UserId, Is.EqualTo(userId));

        var song3Result = result.FirstOrDefault(s => s.SongMetadataId == song3Metadata.Id);
        Assert.That(song3Result, Is.Not.Null);
        Assert.That(song3Result.PayPalOrderId, Is.Null);
        Assert.That(song3Result.UserId, Is.EqualTo(userId));
    }

    [Test]
    public async Task GetAvailableSongsForPlaylistAsync_WithSubscription_ExcludesAlbumCovers()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        // Add songs and album covers to catalog
        var songMetadata = new SongMetadata { Mp3BlobPath = "albums/album1/song1.mp3", IsAlbumCover = false };
        var albumCoverMetadata = new SongMetadata { ImageBlobPath = "albums/album1/cover.jpg", IsAlbumCover = true };
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
        Assert.That(result[0].SongMetadata.IsAlbumCover, Is.False);
    }

    [Test]
    public async Task GetAvailableSongsForPlaylistAsync_WithSubscription_ExcludesSongsAlreadyInPlaylist()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        // Add songs to catalog
        var song1Metadata = new SongMetadata { Mp3BlobPath = "albums/album1/song1.mp3", IsAlbumCover = false };
        var song2Metadata = new SongMetadata { Mp3BlobPath = "albums/album2/song2.mp3", IsAlbumCover = false };
        await _context.SongMetadata.AddRangeAsync(song1Metadata, song2Metadata);

        // Create OwnedSong for song1 and add it to playlist
        var ownedSong1 = new OwnedSong 
        { 
            UserId = userId, 
            SongFileName = "song1.mp3", 
            SongMetadataId = song1Metadata.Id,
            PayPalOrderId = null // Subscription-based
        };
        await _context.OwnedSongs.AddAsync(ownedSong1);

        var userPlaylist = new UserPlaylist 
        { 
            UserId = userId, 
            PlaylistId = playlist.Id, 
            OwnedSongId = ownedSong1.Id 
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
        Assert.That(result[0].SongMetadataId, Is.EqualTo(song2Metadata.Id));
    }

    [Test]
    public async Task GetAvailableSongsForPlaylistAsync_WithSubscription_ReuseExistingVirtualOwnedSong()
    {
        // Arrange
        var userId = 1;
        var playlist = new Playlist { UserId = userId, PlaylistName = "Test Playlist" };
        await _context.Playlists.AddAsync(playlist);

        // Add song to catalog
        var songMetadata = new SongMetadata { Mp3BlobPath = "albums/album1/song1.mp3", IsAlbumCover = false };
        await _context.SongMetadata.AddAsync(songMetadata);

        // User already has a virtual OwnedSong for this (subscription-based)
        var existingOwnedSong = new OwnedSong 
        { 
            UserId = userId, 
            SongFileName = "song1.mp3", 
            SongMetadataId = songMetadata.Id,
            PayPalOrderId = null // Subscription-based, created earlier
        };
        await _context.OwnedSongs.AddAsync(existingOwnedSong);

        await _context.SaveChangesAsync();

        // Get the ID of the existing OwnedSong before the test
        var existingId = existingOwnedSong.Id;

        // Setup subscription
        _mockSubscriptionService
            .Setup(s => s.HasActiveSubscriptionAsync(userId))
            .ReturnsAsync(true);

        // Act
        var result = await _service.GetAvailableSongsForPlaylistAsync(userId, playlist.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(existingId)); // Should reuse existing OwnedSong
        
        // Verify no duplicate was created
        var allOwnedSongsForUser = await _context.OwnedSongs
            .Where(os => os.UserId == userId && os.SongMetadataId == songMetadata.Id)
            .ToListAsync();
        Assert.That(allOwnedSongsForUser, Has.Count.EqualTo(1));
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

        // Create real SongLikeService for integration testing
        _songLikeService = new SongLikeService(_mockContextFactory.Object);

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
    public async Task SyncLikedSongsPlaylistAsync_AddsLikedSongs_ForUserWithOwnedSongs()
    {
        // Arrange
        var userId = 1;

        // Create song metadata
        var song1 = new SongMetadata { Mp3BlobPath = "song1.mp3", IsAlbumCover = false };
        var song2 = new SongMetadata { Mp3BlobPath = "song2.mp3", IsAlbumCover = false };
        await _context.SongMetadata.AddRangeAsync(song1, song2);
        await _context.SaveChangesAsync();

        // User owns these songs
        var ownedSong1 = new OwnedSong
        {
            UserId = userId,
            SongFileName = "song1.mp3",
            SongMetadataId = song1.Id,
            PayPalOrderId = "ORDER123"
        };
        var ownedSong2 = new OwnedSong
        {
            UserId = userId,
            SongFileName = "song2.mp3",
            SongMetadataId = song2.Id,
            PayPalOrderId = "ORDER456"
        };
        await _context.OwnedSongs.AddRangeAsync(ownedSong1, ownedSong2);
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
            .Include(up => up.OwnedSong)
            .Where(up => up.PlaylistId == playlist.Id)
            .ToListAsync();

        Assert.That(playlistSongs, Has.Count.EqualTo(1));
        Assert.That(playlistSongs[0].OwnedSong.SongMetadataId, Is.EqualTo(song1.Id));
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

        // User owns the song
        var ownedSong1 = new OwnedSong
        {
            UserId = userId,
            SongFileName = "song1.mp3",
            SongMetadataId = song1.Id,
            PayPalOrderId = "ORDER123"
        };
        await _context.OwnedSongs.AddAsync(ownedSong1);
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
