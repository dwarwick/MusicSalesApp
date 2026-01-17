using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class SongStatusServiceTests
{
    private Mock<ILogger<SongStatusService>> _mockLogger;
    private Mock<IEmailService> _mockEmailService;
    private Mock<IAzureStorageService> _mockAzureStorageService;
    private IDbContextFactory<AppDbContext> _contextFactory;
    private SongStatusService _service;
    private AppDbContext _context;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<SongStatusService>>();
        _mockEmailService = new Mock<IEmailService>();
        _mockAzureStorageService = new Mock<IAzureStorageService>();

        // Setup email service to return success
        _mockEmailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        // Setup Azure storage service
        _mockAzureStorageService.Setup(x => x.GetReadSasUri(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(new Uri("https://test.blob.core.windows.net/test/image.jpg"));

        // Use in-memory database for testing
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        _contextFactory = new TestDbContextFactory(options);
        _context = new AppDbContext(options);
        
        _service = new SongStatusService(
            _contextFactory, 
            _mockLogger.Object, 
            _mockEmailService.Object,
            _mockAzureStorageService.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task DisableSongAsync_DisablesSongAndCreatesHistory()
    {
        // Arrange
        var song = new SongMetadata
        {
            BlobPath = "test.mp3",
            Mp3BlobPath = "test.mp3",
            IsEnabled = true,
            IsActive = true
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.DisableSongAsync(song.Id, "Test reason", 1, "https://test.com");

        // Assert
        Assert.That(result, Is.True);

        // Verify song was disabled
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var updatedSong = await verifyContext.SongMetadata.FindAsync(song.Id);
        Assert.That(updatedSong, Is.Not.Null);
        Assert.That(updatedSong.IsEnabled, Is.False);
        Assert.That(updatedSong.StatusReason, Is.EqualTo("Test reason"));

        // Verify history was created
        var history = await verifyContext.SongStatusHistories
            .Where(h => h.SongMetadataId == song.Id)
            .FirstOrDefaultAsync();
        Assert.That(history, Is.Not.Null);
        Assert.That(history.IsEnabled, Is.False);
        Assert.That(history.Reason, Is.EqualTo("Test reason"));
        Assert.That(history.ChangedByUserId, Is.EqualTo(1));
    }

    [Test]
    public async Task DisableSongAsync_RemovesSongFromPlaylists()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "test@test.com", Email = "test@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var song = new SongMetadata
        {
            BlobPath = "test.mp3",
            Mp3BlobPath = "test.mp3",
            IsEnabled = true,
            IsActive = true
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        var playlist = new Playlist
        {
            UserId = user.Id,
            PlaylistName = "Test Playlist"
        };
        _context.Playlists.Add(playlist);
        await _context.SaveChangesAsync();

        var userPlaylist = new UserPlaylist
        {
            UserId = user.Id,
            PlaylistId = playlist.Id,
            SongMetadataId = song.Id
        };
        _context.UserPlaylists.Add(userPlaylist);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.DisableSongAsync(song.Id, "Test reason", 1, "https://test.com");

        // Assert
        Assert.That(result, Is.True);

        // Verify song was removed from playlist
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var playlistEntry = await verifyContext.UserPlaylists
            .Where(up => up.SongMetadataId == song.Id)
            .FirstOrDefaultAsync();
        Assert.That(playlistEntry, Is.Null);
    }

    [Test]
    public async Task EnableSongAsync_EnablesSongAndCreatesHistory()
    {
        // Arrange
        var song = new SongMetadata
        {
            BlobPath = "test.mp3",
            Mp3BlobPath = "test.mp3",
            IsEnabled = false,
            StatusReason = "Previous reason",
            IsActive = true
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.EnableSongAsync(song.Id, "Re-enabled because issue resolved", 1, "https://test.com");

        // Assert
        Assert.That(result, Is.True);

        // Verify song was enabled
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var updatedSong = await verifyContext.SongMetadata.FindAsync(song.Id);
        Assert.That(updatedSong, Is.Not.Null);
        Assert.That(updatedSong.IsEnabled, Is.True);
        Assert.That(updatedSong.StatusReason, Is.EqualTo("Re-enabled because issue resolved"));

        // Verify history was created
        var history = await verifyContext.SongStatusHistories
            .Where(h => h.SongMetadataId == song.Id)
            .FirstOrDefaultAsync();
        Assert.That(history, Is.Not.Null);
        Assert.That(history.IsEnabled, Is.True);
        Assert.That(history.Reason, Is.EqualTo("Re-enabled because issue resolved"));
    }

    [Test]
    public async Task DisableSongAsync_ReturnsFalse_WhenSongNotFound()
    {
        // Act
        var result = await _service.DisableSongAsync(999, "Test reason", 1, "https://test.com");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task EnableSongAsync_ReturnsFalse_WhenSongNotFound()
    {
        // Act
        var result = await _service.EnableSongAsync(999, "Test reason", 1, "https://test.com");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task GetSongStatusHistoryAsync_ReturnsHistoryForSong()
    {
        // Arrange
        var song = new SongMetadata
        {
            BlobPath = "test.mp3",
            Mp3BlobPath = "test.mp3",
            IsEnabled = true,
            IsActive = true
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        var history1 = new SongStatusHistory
        {
            SongMetadataId = song.Id,
            IsEnabled = false,
            Reason = "Disabled for review",
            ChangedAt = DateTime.UtcNow.AddDays(-1),
            ChangedByUserId = 1
        };
        var history2 = new SongStatusHistory
        {
            SongMetadataId = song.Id,
            IsEnabled = true,
            Reason = "Re-enabled after review",
            ChangedAt = DateTime.UtcNow,
            ChangedByUserId = 1
        };
        _context.SongStatusHistories.AddRange(history1, history2);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetSongStatusHistoryAsync(song.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].Reason, Is.EqualTo("Re-enabled after review")); // Most recent first
        Assert.That(result[1].Reason, Is.EqualTo("Disabled for review"));
    }

    [Test]
    public async Task GetAllStatusHistoryAsync_ReturnsAllHistory()
    {
        // Arrange
        var song1 = new SongMetadata
        {
            BlobPath = "test1.mp3",
            Mp3BlobPath = "test1.mp3",
            IsEnabled = true,
            IsActive = true
        };
        var song2 = new SongMetadata
        {
            BlobPath = "test2.mp3",
            Mp3BlobPath = "test2.mp3",
            IsEnabled = false,
            IsActive = true
        };
        _context.SongMetadata.AddRange(song1, song2);
        await _context.SaveChangesAsync();

        var history1 = new SongStatusHistory
        {
            SongMetadataId = song1.Id,
            IsEnabled = false,
            Reason = "Reason 1",
            ChangedAt = DateTime.UtcNow
        };
        var history2 = new SongStatusHistory
        {
            SongMetadataId = song2.Id,
            IsEnabled = false,
            Reason = "Reason 2",
            ChangedAt = DateTime.UtcNow
        };
        _context.SongStatusHistories.AddRange(history1, history2);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAllStatusHistoryAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task DisableSongAsync_SendsEmailToCreator()
    {
        // Arrange
        var user = new ApplicationUser
        {
            UserName = "creator@test.com",
            Email = "creator@test.com"
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            IsActive = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var song = new SongMetadata
        {
            BlobPath = "test.mp3",
            Mp3BlobPath = "test.mp3",
            SongTitle = "Test Song",
            IsEnabled = true,
            IsActive = true,
            CreatorId = creator.Id
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        // Act
        await _service.DisableSongAsync(song.Id, "Copyright violation", 1, "https://test.com");

        // Assert - Verify email was sent
        _mockEmailService.Verify(x => x.SendEmailAsync(
            "creator@test.com",
            It.Is<string>(s => s.Contains("Disabled")),
            It.Is<string>(s => s.Contains("Copyright violation"))),
            Times.Once);
    }

    private class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext()
        {
            return new AppDbContext(_options);
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AppDbContext(_options));
        }
    }
}
