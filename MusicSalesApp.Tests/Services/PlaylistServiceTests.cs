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
        
        // Mock the context factory to return our in-memory context
        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _service = new PlaylistService(_mockContextFactory.Object, _mockLogger.Object);
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
}
