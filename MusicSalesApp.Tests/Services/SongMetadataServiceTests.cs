using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class SongMetadataServiceTests
{
    private Mock<ILogger<SongMetadataService>> _mockLogger;
    private IDbContextFactory<AppDbContext> _contextFactory;
    private SongMetadataService _service;
    private AppDbContext _context;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<SongMetadataService>>();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        _contextFactory = new TestDbContextFactory(options);
        _context = new AppDbContext(options);

        _service = new SongMetadataService(_contextFactory, _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    #region UpsertAsync Tests

    [Test]
    public async Task UpsertAsync_NewSong_SetsIsActiveTrue()
    {
        // Arrange
        var metadata = new SongMetadata
        {
            BlobPath = "newsong/newsong.mp3",
            Mp3BlobPath = "newsong/newsong.mp3",
            Genre = "Rock",
            CreatorId = 1
        };

        // Act
        var result = await _service.UpsertAsync(metadata);

        // Assert
        Assert.That(result.IsActive, Is.True);
        Assert.That(result.IsEnabled, Is.True);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.SongMetadata.FirstAsync(s => s.BlobPath == "newsong/newsong.mp3");
        Assert.That(saved.IsActive, Is.True);
        Assert.That(saved.IsEnabled, Is.True);
    }

    [Test]
    public async Task UpsertAsync_ExistingInactiveSong_RestoresIsActive()
    {
        // Arrange — simulate a song that was deactivated when creator stopped selling
        var existingSong = new SongMetadata
        {
            BlobPath = "mysong/mysong.mp3",
            Mp3BlobPath = "mysong/mysong.mp3",
            Genre = "Pop",
            IsActive = false,
            IsEnabled = true,
            CreatorId = 1
        };
        _context.SongMetadata.Add(existingSong);
        await _context.SaveChangesAsync();

        // Act — creator re-signs up and re-uploads the same file
        var newMetadata = new SongMetadata
        {
            BlobPath = "mysong/mysong.mp3",
            Mp3BlobPath = "mysong/mysong.mp3",
            Genre = "Pop",
            CreatorId = 1
        };
        var result = await _service.UpsertAsync(newMetadata);

        // Assert — song should be reactivated
        Assert.That(result.IsActive, Is.True, "UpsertAsync should restore IsActive to true when updating an inactive song");
        Assert.That(result.IsEnabled, Is.True);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.SongMetadata.FirstAsync(s => s.BlobPath == "mysong/mysong.mp3");
        Assert.That(saved.IsActive, Is.True, "Persisted song should have IsActive = true");
        Assert.That(saved.IsEnabled, Is.True, "Persisted song should have IsEnabled = true");
    }

    [Test]
    public async Task UpsertAsync_ExistingInactiveAndDisabledSong_RestoresBothFlags()
    {
        // Arrange — song was both deactivated and admin-disabled
        var existingSong = new SongMetadata
        {
            BlobPath = "disabled/disabled.mp3",
            Mp3BlobPath = "disabled/disabled.mp3",
            Genre = "Jazz",
            IsActive = false,
            IsEnabled = false,
            CreatorId = 2
        };
        _context.SongMetadata.Add(existingSong);
        await _context.SaveChangesAsync();

        // Act
        var newMetadata = new SongMetadata
        {
            BlobPath = "disabled/disabled.mp3",
            Mp3BlobPath = "disabled/disabled.mp3",
            Genre = "Jazz",
            CreatorId = 2
        };
        var result = await _service.UpsertAsync(newMetadata);

        // Assert
        Assert.That(result.IsActive, Is.True, "Re-upload should restore IsActive");
        Assert.That(result.IsEnabled, Is.True, "Re-upload should restore IsEnabled");
    }

    [Test]
    public async Task UpsertAsync_ExistingActiveSong_KeepsActiveAndUpdatesFields()
    {
        // Arrange
        var existingSong = new SongMetadata
        {
            BlobPath = "active/active.mp3",
            Mp3BlobPath = "active/active.mp3",
            Genre = "Rock",
            AlbumName = "Old Album",
            IsActive = true,
            IsEnabled = true,
            CreatorId = 1
        };
        _context.SongMetadata.Add(existingSong);
        await _context.SaveChangesAsync();

        // Act — update with new metadata
        var updatedMetadata = new SongMetadata
        {
            BlobPath = "active/active.mp3",
            Mp3BlobPath = "active/active.mp3",
            Genre = "Country",
            AlbumName = "New Album",
            CreatorId = 1
        };
        var result = await _service.UpsertAsync(updatedMetadata);

        // Assert — fields updated, still active
        Assert.That(result.IsActive, Is.True);
        Assert.That(result.IsEnabled, Is.True);
        Assert.That(result.Genre, Is.EqualTo("Country"));
        Assert.That(result.AlbumName, Is.EqualTo("New Album"));
    }

    [Test]
    public async Task UpsertAsync_MatchesOnMp3BlobPath_RestoresInactiveSong()
    {
        // Arrange — existing song matched via Mp3BlobPath
        var existingSong = new SongMetadata
        {
            BlobPath = "old-path",
            Mp3BlobPath = "shared/track.mp3",
            Genre = "Electronic",
            IsActive = false,
            IsEnabled = true,
            CreatorId = 3
        };
        _context.SongMetadata.Add(existingSong);
        await _context.SaveChangesAsync();

        // Act — new upload whose BlobPath matches existing Mp3BlobPath
        var newMetadata = new SongMetadata
        {
            BlobPath = "shared/track.mp3",
            Mp3BlobPath = "shared/track.mp3",
            Genre = "Electronic",
            CreatorId = 3
        };
        var result = await _service.UpsertAsync(newMetadata);

        // Assert
        Assert.That(result.IsActive, Is.True, "Should match on Mp3BlobPath and reactivate");
    }

    [Test]
    public async Task UpsertAsync_MatchesOnImageBlobPath_RestoresInactiveSong()
    {
        // Arrange — existing song matched via ImageBlobPath
        var existingSong = new SongMetadata
        {
            BlobPath = "old-path",
            ImageBlobPath = "album/cover.jpg",
            IsAlbumCover = true,
            IsActive = false,
            IsEnabled = true,
            CreatorId = 3
        };
        _context.SongMetadata.Add(existingSong);
        await _context.SaveChangesAsync();

        // Act
        var newMetadata = new SongMetadata
        {
            BlobPath = "album/cover.jpg",
            ImageBlobPath = "album/cover.jpg",
            IsAlbumCover = true,
            CreatorId = 3
        };
        var result = await _service.UpsertAsync(newMetadata);

        // Assert
        Assert.That(result.IsActive, Is.True, "Should match on ImageBlobPath and reactivate");
    }

    #endregion

    #region GetAllAsync / GetCreatorSongsAsync Filter Tests

    [Test]
    public async Task GetAllAsync_ExcludesInactiveSongs()
    {
        // Arrange
        var activeSong = new SongMetadata
        {
            BlobPath = "active/song.mp3",
            Mp3BlobPath = "active/song.mp3",
            IsActive = true,
            IsEnabled = true,
            CreatorId = null // admin-uploaded
        };
        var inactiveSong = new SongMetadata
        {
            BlobPath = "inactive/song.mp3",
            Mp3BlobPath = "inactive/song.mp3",
            IsActive = false,
            IsEnabled = true,
            CreatorId = null
        };
        _context.SongMetadata.AddRange(activeSong, inactiveSong);
        await _context.SaveChangesAsync();

        // Act
        var results = await _service.GetAllAsync();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].BlobPath, Is.EqualTo("active/song.mp3"));
    }

    [Test]
    public async Task GetAllAsync_ExcludesDisabledSongs()
    {
        // Arrange
        var enabledSong = new SongMetadata
        {
            BlobPath = "enabled/song.mp3",
            Mp3BlobPath = "enabled/song.mp3",
            IsActive = true,
            IsEnabled = true,
            CreatorId = null
        };
        var disabledSong = new SongMetadata
        {
            BlobPath = "disabled/song.mp3",
            Mp3BlobPath = "disabled/song.mp3",
            IsActive = true,
            IsEnabled = false,
            CreatorId = null
        };
        _context.SongMetadata.AddRange(enabledSong, disabledSong);
        await _context.SaveChangesAsync();

        // Act
        var results = await _service.GetAllAsync();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].BlobPath, Is.EqualTo("enabled/song.mp3"));
    }

    [Test]
    public async Task GetAllAsync_ExcludesSongsFromInactiveCreator()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "creator@test.com", Email = "creator@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            User = user,
            IsActive = false,
            PayPalEmail = "paypal@test.com"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var song = new SongMetadata
        {
            BlobPath = "creatorSong/song.mp3",
            Mp3BlobPath = "creatorSong/song.mp3",
            IsActive = true,
            IsEnabled = true,
            CreatorId = creator.Id
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        // Act
        var results = await _service.GetAllAsync();

        // Assert — song should not appear because creator is inactive
        Assert.That(results, Has.Count.EqualTo(0));
    }

    [Test]
    public async Task GetAllAsync_IncludesSongsFromActiveCreator()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "active@test.com", Email = "active@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            User = user,
            IsActive = true,
            PayPalEmail = "paypal@test.com"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var song = new SongMetadata
        {
            BlobPath = "activeCreator/song.mp3",
            Mp3BlobPath = "activeCreator/song.mp3",
            IsActive = true,
            IsEnabled = true,
            CreatorId = creator.Id
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        // Act
        var results = await _service.GetAllAsync();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].BlobPath, Is.EqualTo("activeCreator/song.mp3"));
    }

    [Test]
    public async Task GetAllIncludingDisabledAsync_IncludesDisabledButNotInactive()
    {
        // Arrange
        var activeSong = new SongMetadata
        {
            BlobPath = "active/song.mp3",
            Mp3BlobPath = "active/song.mp3",
            IsActive = true,
            IsEnabled = true,
            CreatorId = null
        };
        var disabledSong = new SongMetadata
        {
            BlobPath = "disabled/song.mp3",
            Mp3BlobPath = "disabled/song.mp3",
            IsActive = true,
            IsEnabled = false,
            CreatorId = null
        };
        var inactiveSong = new SongMetadata
        {
            BlobPath = "inactive/song.mp3",
            Mp3BlobPath = "inactive/song.mp3",
            IsActive = false,
            IsEnabled = true,
            CreatorId = null
        };
        _context.SongMetadata.AddRange(activeSong, disabledSong, inactiveSong);
        await _context.SaveChangesAsync();

        // Act
        var results = await _service.GetAllIncludingDisabledAsync();

        // Assert — includes disabled but excludes inactive
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.Any(s => s.BlobPath == "active/song.mp3"), Is.True);
        Assert.That(results.Any(s => s.BlobPath == "disabled/song.mp3"), Is.True);
    }

    #endregion

    #region Deactivation → Re-upload Regression Test

    [Test]
    public async Task UpsertAsync_AfterCreatorDeactivation_ReuploadedSongBecomesVisible()
    {
        // Arrange — set up a creator with an active song
        var user = new ApplicationUser { UserName = "returning@test.com", Email = "returning@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            User = user,
            IsActive = true,
            PayPalEmail = "paypal@test.com"
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        var song = new SongMetadata
        {
            BlobPath = "returning/song.mp3",
            Mp3BlobPath = "returning/song.mp3",
            Genre = "Rock",
            IsActive = true,
            IsEnabled = true,
            CreatorId = creator.Id
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        // Step 1: Simulate creator deactivation (marks songs inactive)
        song.IsActive = false;
        _context.SongMetadata.Update(song);
        creator.IsActive = false;
        _context.Creators.Update(creator);
        await _context.SaveChangesAsync();

        // Step 2: Creator re-signs up → creator becomes active again
        creator.IsActive = true;
        _context.Creators.Update(creator);
        await _context.SaveChangesAsync();

        // Step 3: Creator re-uploads the same song
        var reUploadMetadata = new SongMetadata
        {
            BlobPath = "returning/song.mp3",
            Mp3BlobPath = "returning/song.mp3",
            Genre = "Rock",
            CreatorId = creator.Id
        };
        await _service.UpsertAsync(reUploadMetadata);

        // Assert — the song should now be visible in GetAllAsync (the full pipeline)
        var allSongs = await _service.GetAllAsync();
        Assert.That(allSongs, Has.Count.EqualTo(1), "Re-uploaded song should appear in GetAllAsync after creator reactivation");
        Assert.That(allSongs[0].BlobPath, Is.EqualTo("returning/song.mp3"));
        Assert.That(allSongs[0].IsActive, Is.True);
        Assert.That(allSongs[0].IsEnabled, Is.True);
    }

    #endregion

    #region DeleteAsync Tests

    [Test]
    public async Task DeleteAsync_ExistingSong_ReturnsTrue()
    {
        // Arrange
        var song = new SongMetadata
        {
            BlobPath = "todelete/song.mp3",
            Mp3BlobPath = "todelete/song.mp3",
            IsActive = true
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.DeleteAsync("todelete/song.mp3");

        // Assert
        Assert.That(result, Is.True);

        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var deleted = await verifyContext.SongMetadata
            .FirstOrDefaultAsync(s => s.BlobPath == "todelete/song.mp3");
        Assert.That(deleted, Is.Null);
    }

    [Test]
    public async Task DeleteAsync_NonExistentSong_ReturnsFalse()
    {
        // Act
        var result = await _service.DeleteAsync("nonexistent/song.mp3");

        // Assert
        Assert.That(result, Is.False);
    }

    #endregion

    #region FindExistingSongTitlesAsync Tests

    [Test]
    public async Task FindExistingSongTitlesAsync_EmptyInput_ReturnsEmpty()
    {
        // Act
        var result = await _service.FindExistingSongTitlesAsync(new List<string>());

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task FindExistingSongTitlesAsync_NoMatchingSongs_ReturnsEmpty()
    {
        // Arrange
        _context.SongMetadata.Add(new SongMetadata
        {
            BlobPath = "existing/existing.mp3",
            Mp3BlobPath = "existing/existing.mp3",
            IsActive = true,
            IsEnabled = true,
            CreatorId = null
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.FindExistingSongTitlesAsync(new[] { "nonexistent" });

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task FindExistingSongTitlesAsync_MatchesDerivedTitle_ReturnsDuplicate()
    {
        // Arrange - song title derived from Mp3BlobPath "Mercy Found Me/Mercy Found Me.mp3" → "Mercy Found Me"
        _context.SongMetadata.Add(new SongMetadata
        {
            BlobPath = "Mercy Found Me/Mercy Found Me.mp3",
            Mp3BlobPath = "Mercy Found Me/Mercy Found Me.mp3",
            IsActive = true,
            IsEnabled = true,
            CreatorId = null
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.FindExistingSongTitlesAsync(new[] { "Mercy Found Me" });

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain("Mercy Found Me"));
    }

    [Test]
    public async Task FindExistingSongTitlesAsync_MatchesExplicitSongTitle_ReturnsDuplicate()
    {
        // Arrange - SongTitle explicitly set
        _context.SongMetadata.Add(new SongMetadata
        {
            BlobPath = "song1/song1.mp3",
            Mp3BlobPath = "song1/song1.mp3",
            SongTitle = "My Custom Title",
            IsActive = true,
            IsEnabled = true,
            CreatorId = null
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.FindExistingSongTitlesAsync(new[] { "My Custom Title" });

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain("My Custom Title"));
    }

    [Test]
    public async Task FindExistingSongTitlesAsync_CaseInsensitive_ReturnsDuplicate()
    {
        // Arrange
        _context.SongMetadata.Add(new SongMetadata
        {
            BlobPath = "Mercy Found Me/Mercy Found Me.mp3",
            Mp3BlobPath = "Mercy Found Me/Mercy Found Me.mp3",
            IsActive = true,
            IsEnabled = true,
            CreatorId = null
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.FindExistingSongTitlesAsync(new[] { "mercy found me" });

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task FindExistingSongTitlesAsync_ExcludesInactiveSongs()
    {
        // Arrange
        _context.SongMetadata.Add(new SongMetadata
        {
            BlobPath = "inactive/inactive.mp3",
            Mp3BlobPath = "inactive/inactive.mp3",
            IsActive = false,
            IsEnabled = true,
            CreatorId = null
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.FindExistingSongTitlesAsync(new[] { "inactive" });

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task FindExistingSongTitlesAsync_ExcludesAlbumCovers()
    {
        // Arrange
        _context.SongMetadata.Add(new SongMetadata
        {
            BlobPath = "album/cover.jpg",
            ImageBlobPath = "album/cover.jpg",
            Mp3BlobPath = null,
            IsAlbumCover = true,
            IsActive = true,
            IsEnabled = true,
            CreatorId = null
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.FindExistingSongTitlesAsync(new[] { "cover" });

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task FindExistingSongTitlesAsync_MultipleTitles_ReturnsOnlyExisting()
    {
        // Arrange
        _context.SongMetadata.Add(new SongMetadata
        {
            BlobPath = "Song A/Song A.mp3",
            Mp3BlobPath = "Song A/Song A.mp3",
            IsActive = true,
            IsEnabled = true,
            CreatorId = null
        });
        _context.SongMetadata.Add(new SongMetadata
        {
            BlobPath = "Song C/Song C.mp3",
            Mp3BlobPath = "Song C/Song C.mp3",
            IsActive = true,
            IsEnabled = true,
            CreatorId = null
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.FindExistingSongTitlesAsync(new[] { "Song A", "Song B", "Song C" });

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain("Song A"));
        Assert.That(result, Does.Contain("Song C"));
        Assert.That(result, Does.Not.Contain("Song B"));
    }

    #endregion

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
