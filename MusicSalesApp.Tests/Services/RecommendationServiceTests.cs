using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class RecommendationServiceTests
{
    private DbContextOptions<AppDbContext> _dbContextOptions;
    private Mock<ILogger<RecommendationService>> _mockLogger;
    private Mock<ITopStreamedPlaylistService> _mockTopStreamedPlaylistService;

    [SetUp]
    public void Setup()
    {
        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        _mockLogger = new Mock<ILogger<RecommendationService>>();
        _mockTopStreamedPlaylistService = new Mock<ITopStreamedPlaylistService>();
    }

    private IDbContextFactory<AppDbContext> CreateDbContextFactory()
    {
        var mockFactory = new Mock<IDbContextFactory<AppDbContext>>();
        mockFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_dbContextOptions));
        return mockFactory.Object;
    }

    private async Task<AppDbContext> CreateAndSeedDatabase(
        List<SongMetadata> songs = null,
        List<SongLike> likes = null)
    {
        var context = new AppDbContext(_dbContextOptions);
        
        if (songs != null)
        {
            context.SongMetadata.AddRange(songs);
        }
        
        if (likes != null)
        {
            context.SongLikes.AddRange(likes);
        }
        
        await context.SaveChangesAsync();
        return context;
    }

    [Test]
    public async Task GenerateRecommendationsAsync_NoLikes_ReturnsPopularSongs()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "song1.mp3", IsAlbumCover = false, NumberOfStreams = 100 },
            new() { Id = 2, Mp3BlobPath = "song2.mp3", IsAlbumCover = false, NumberOfStreams = 50 },
            new() { Id = 3, Mp3BlobPath = "song3.mp3", IsAlbumCover = false, NumberOfStreams = 200 }
        };
        
        var likes = new List<SongLike>
        {
            new() { Id = 1, UserId = 2, SongMetadataId = 3, IsLike = true },
            new() { Id = 2, UserId = 3, SongMetadataId = 3, IsLike = true }
        };
        
        await CreateAndSeedDatabase(songs, likes);
        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        var result = await service.GenerateRecommendationsAsync(userId: 1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.LessThanOrEqualTo(20));
    }

    [Test]
    public async Task GenerateRecommendationsAsync_WithLikes_ReturnsCollaborativeRecommendations()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "song1.mp3", IsAlbumCover = false },
            new() { Id = 2, Mp3BlobPath = "song2.mp3", IsAlbumCover = false },
            new() { Id = 3, Mp3BlobPath = "song3.mp3", IsAlbumCover = false },
            new() { Id = 4, Mp3BlobPath = "song4.mp3", IsAlbumCover = false }
        };
        
        var likes = new List<SongLike>
        {
            // User 1 likes song 1
            new() { Id = 1, UserId = 1, SongMetadataId = 1, IsLike = true },
            // User 2 also likes song 1 AND likes song 2 (collaborative filtering)
            new() { Id = 2, UserId = 2, SongMetadataId = 1, IsLike = true },
            new() { Id = 3, UserId = 2, SongMetadataId = 2, IsLike = true },
            // User 3 also likes song 1 AND likes song 3
            new() { Id = 4, UserId = 3, SongMetadataId = 1, IsLike = true },
            new() { Id = 5, UserId = 3, SongMetadataId = 3, IsLike = true }
        };
        
        await CreateAndSeedDatabase(songs, likes);
        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        var result = await service.GenerateRecommendationsAsync(userId: 1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.GreaterThan(0));
        
        // Song 2 and 3 should be recommended (liked by similar users)
        var recommendedSongIds = result.Select(r => r.SongMetadataId).ToList();
        Assert.That(recommendedSongIds, Does.Contain(2).Or.Contain(3));
        
        // Song 1 should NOT be recommended (user already likes it)
        Assert.That(recommendedSongIds, Does.Not.Contain(1));
    }

    [Test]
    public async Task GenerateRecommendationsAsync_ExcludesDislikedSongs()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "song1.mp3", IsAlbumCover = false },
            new() { Id = 2, Mp3BlobPath = "song2.mp3", IsAlbumCover = false },
            new() { Id = 3, Mp3BlobPath = "song3.mp3", IsAlbumCover = false }
        };
        
        var likes = new List<SongLike>
        {
            // User 1 likes song 1 but dislikes song 2
            new() { Id = 1, UserId = 1, SongMetadataId = 1, IsLike = true },
            new() { Id = 2, UserId = 1, SongMetadataId = 2, IsLike = false }, // Dislike
            // User 2 likes both song 1 and song 2 (would normally recommend song 2)
            new() { Id = 3, UserId = 2, SongMetadataId = 1, IsLike = true },
            new() { Id = 4, UserId = 2, SongMetadataId = 2, IsLike = true }
        };
        
        await CreateAndSeedDatabase(songs, likes);
        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        var result = await service.GenerateRecommendationsAsync(userId: 1);

        // Assert
        var recommendedSongIds = result.Select(r => r.SongMetadataId).ToList();
        
        // Song 2 should NOT be recommended (user disliked it)
        Assert.That(recommendedSongIds, Does.Not.Contain(2));
    }

    [Test]
    public async Task GetRecommendedPlaylistAsync_ReturnsCachedRecommendations()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "song1.mp3", IsAlbumCover = false }
        };
        var context = await CreateAndSeedDatabase(songs);
        
        // Add cached recommendations
        context.RecommendedPlaylists.Add(new RecommendedPlaylist
        {
            UserId = 1,
            SongMetadataId = 1,
            DisplayOrder = 1,
            GeneratedAt = DateTime.UtcNow,
            Score = 1.0
        });
        await context.SaveChangesAsync();

        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        var result = await service.GetRecommendedPlaylistAsync(userId: 1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].SongMetadataId, Is.EqualTo(1));
    }

    [Test]
    public async Task GetRecommendedPlaylistAsync_ReturnsEmptyWhenNoCachedRecommendations()
    {
        // Arrange
        await CreateAndSeedDatabase();
        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        var result = await service.GetRecommendedPlaylistAsync(userId: 1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetRecommendedPlaylistAsync_FiltersOutInactiveSongs()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "active.mp3", IsAlbumCover = false, IsActive = true, IsEnabled = true },
            new() { Id = 2, Mp3BlobPath = "inactive.mp3", IsAlbumCover = false, IsActive = false, IsEnabled = true }
        };
        var context = await CreateAndSeedDatabase(songs);
        
        context.RecommendedPlaylists.AddRange(
            new RecommendedPlaylist { UserId = 1, SongMetadataId = 1, DisplayOrder = 1, GeneratedAt = DateTime.UtcNow, Score = 2.0 },
            new RecommendedPlaylist { UserId = 1, SongMetadataId = 2, DisplayOrder = 2, GeneratedAt = DateTime.UtcNow, Score = 1.0 }
        );
        await context.SaveChangesAsync();

        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        var result = await service.GetRecommendedPlaylistAsync(userId: 1);

        // Assert
        var ids = result.Select(r => r.SongMetadataId).ToList();
        Assert.That(ids, Does.Contain(1));
        Assert.That(ids, Does.Not.Contain(2));
    }

    [Test]
    public async Task GenerateAllRecommendationsAsync_GeneratesForAllUsersWithLikes()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "song1.mp3", IsAlbumCover = false, NumberOfStreams = 100 },
            new() { Id = 2, Mp3BlobPath = "song2.mp3", IsAlbumCover = false, NumberOfStreams = 50 }
        };
        
        var likes = new List<SongLike>
        {
            new() { Id = 1, UserId = 1, SongMetadataId = 1, IsLike = true },
            new() { Id = 2, UserId = 2, SongMetadataId = 2, IsLike = true }
        };
        
        await CreateAndSeedDatabase(songs, likes);
        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        await service.GenerateAllRecommendationsAsync();

        // Assert — both users should now have recommendations
        await using var context = new AppDbContext(_dbContextOptions);
        var user1Recs = await context.RecommendedPlaylists.Where(r => r.UserId == 1).ToListAsync();
        var user2Recs = await context.RecommendedPlaylists.Where(r => r.UserId == 2).ToListAsync();
        Assert.That(user1Recs, Is.Not.Empty, "User 1 should have recommendations");
        Assert.That(user2Recs, Is.Not.Empty, "User 2 should have recommendations");
    }

    [Test]
    public async Task GenerateRecommendationsAsync_ClearsExistingRecommendations()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "song1.mp3", IsAlbumCover = false }
        };
        
        var context = await CreateAndSeedDatabase(songs);
        
        // Add existing recommendations
        context.RecommendedPlaylists.Add(new RecommendedPlaylist
        {
            UserId = 1,
            SongMetadataId = 1,
            DisplayOrder = 1,
            GeneratedAt = DateTime.UtcNow.AddDays(-1),
            Score = 1.0
        });
        await context.SaveChangesAsync();

        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        var result = await service.GenerateRecommendationsAsync(userId: 1);

        // Assert
        // Verify old recommendations were cleared and new ones generated
        Assert.That(result, Is.Not.Null);
        
        // Verify timestamps are fresh
        foreach (var rec in result)
        {
            Assert.That(rec.GeneratedAt, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));
        }
    }

    [Test]
    public async Task GenerateRecommendationsAsync_ExcludesAlbumCovers()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "song1.mp3", IsAlbumCover = false, NumberOfStreams = 100 },
            new() { Id = 2, ImageBlobPath = "cover.jpg", IsAlbumCover = true }, // Album cover
            new() { Id = 3, Mp3BlobPath = "song3.mp3", IsAlbumCover = false, NumberOfStreams = 50 }
        };
        
        await CreateAndSeedDatabase(songs);
        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        var result = await service.GenerateRecommendationsAsync(userId: 1);

        // Assert
        var recommendedSongIds = result.Select(r => r.SongMetadataId).ToList();
        
        // Album cover (Id: 2) should NOT be in recommendations
        Assert.That(recommendedSongIds, Does.Not.Contain(2));
    }

    [Test]
    public async Task GenerateRecommendationsAsync_ReturnsMaxOf20Songs()
    {
        // Arrange
        var songs = Enumerable.Range(1, 30)
            .Select(i => new SongMetadata 
            { 
                Id = i, 
                Mp3BlobPath = $"song{i}.mp3", 
                IsAlbumCover = false,
                NumberOfStreams = 100 - i
            })
            .ToList();
        
        // User 2 likes all 30 songs
        var likes = songs
            .Select((s, i) => new SongLike 
            { 
                Id = i + 1, 
                UserId = 2, 
                SongMetadataId = s.Id, 
                IsLike = true 
            })
            .ToList();
        
        await CreateAndSeedDatabase(songs, likes);
        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        var result = await service.GenerateRecommendationsAsync(userId: 1);

        // Assert
        Assert.That(result.Count, Is.LessThanOrEqualTo(20));
    }

    [Test]
    public async Task GenerateRecommendationsAsync_ExcludesInactiveSongs()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "song1.mp3", IsAlbumCover = false, IsActive = true, NumberOfStreams = 100 },
            new() { Id = 2, Mp3BlobPath = "song2.mp3", IsAlbumCover = false, IsActive = false, NumberOfStreams = 200 }, // Inactive
            new() { Id = 3, Mp3BlobPath = "song3.mp3", IsAlbumCover = false, IsActive = true, NumberOfStreams = 50 }
        };

        var likes = new List<SongLike>
        {
            new() { Id = 1, UserId = 2, SongMetadataId = 2, IsLike = true },
            new() { Id = 2, UserId = 3, SongMetadataId = 2, IsLike = true }
        };

        await CreateAndSeedDatabase(songs, likes);
        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        var result = await service.GenerateRecommendationsAsync(userId: 1);

        // Assert
        var recommendedSongIds = result.Select(r => r.SongMetadataId).ToList();
        Assert.That(recommendedSongIds, Does.Not.Contain(2), "Inactive songs should not appear in recommendations");
    }

    [Test]
    public async Task GenerateRecommendationsAsync_ExcludesInactiveSongs_FromCollaborativeFiltering()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "song1.mp3", IsAlbumCover = false, IsActive = true },
            new() { Id = 2, Mp3BlobPath = "song2.mp3", IsAlbumCover = false, IsActive = false }, // Inactive
            new() { Id = 3, Mp3BlobPath = "song3.mp3", IsAlbumCover = false, IsActive = true }
        };

        var likes = new List<SongLike>
        {
            // User 1 likes song 1
            new() { Id = 1, UserId = 1, SongMetadataId = 1, IsLike = true },
            // User 2 likes song 1 AND song 2 (inactive) - collaborative filtering would recommend song 2
            new() { Id = 2, UserId = 2, SongMetadataId = 1, IsLike = true },
            new() { Id = 3, UserId = 2, SongMetadataId = 2, IsLike = true },
            // User 3 likes song 1 AND song 3
            new() { Id = 4, UserId = 3, SongMetadataId = 1, IsLike = true },
            new() { Id = 5, UserId = 3, SongMetadataId = 3, IsLike = true }
        };

        await CreateAndSeedDatabase(songs, likes);
        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        var result = await service.GenerateRecommendationsAsync(userId: 1);

        // Assert
        var recommendedSongIds = result.Select(r => r.SongMetadataId).ToList();
        Assert.That(recommendedSongIds, Does.Not.Contain(2), "Inactive song should be excluded from collaborative filtering");
        Assert.That(recommendedSongIds, Does.Contain(3), "Active song should still be recommended");
    }

    [Test]
    public async Task GenerateRecommendationsAsync_PersistsRemovalEvenWhenNoNewRecommendationsGenerated()
    {
        // Arrange — seed only an inactive song so generation yields zero results.
        // The pre-existing cached recommendation for that song must still be deleted.
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "inactive.mp3", IsAlbumCover = false, IsActive = false, IsEnabled = false }
        };
        var context = await CreateAndSeedDatabase(songs);

        context.RecommendedPlaylists.Add(new RecommendedPlaylist
        {
            UserId = 1,
            SongMetadataId = 1,
            DisplayOrder = 1,
            GeneratedAt = DateTime.UtcNow.AddDays(-1),
            Score = 5.0
        });
        await context.SaveChangesAsync();

        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        var result = await service.GenerateRecommendationsAsync(userId: 1);

        // Assert — result is empty (no playable songs) …
        Assert.That(result, Is.Empty);

        // … but the stale recommendation must have been deleted from the database
        await using var verifyContext = new AppDbContext(_dbContextOptions);
        var remaining = await verifyContext.RecommendedPlaylists.Where(r => r.UserId == 1).ToListAsync();
        Assert.That(remaining, Is.Empty, "Stale recommendations should be removed even when no new ones are generated");
    }

    [Test]
    public async Task GenerateAllRecommendationsAsync_SkipsUsersWithOnlyDislikes()
    {
        // Arrange — user 1 has only dislikes, user 2 has a like.
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "song1.mp3", IsAlbumCover = false, NumberOfStreams = 10 },
            new() { Id = 2, Mp3BlobPath = "song2.mp3", IsAlbumCover = false, NumberOfStreams = 20 }
        };

        var likes = new List<SongLike>
        {
            new() { Id = 1, UserId = 1, SongMetadataId = 1, IsLike = false }, // dislike only
            new() { Id = 2, UserId = 2, SongMetadataId = 2, IsLike = true }
        };

        await CreateAndSeedDatabase(songs, likes);
        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act
        await service.GenerateAllRecommendationsAsync();

        // Assert — user 2 gets recommendations, user 1 (dislike-only) does not
        await using var verifyContext = new AppDbContext(_dbContextOptions);
        var user1Recs = await verifyContext.RecommendedPlaylists.Where(r => r.UserId == 1).ToListAsync();
        var user2Recs = await verifyContext.RecommendedPlaylists.Where(r => r.UserId == 2).ToListAsync();

        Assert.That(user1Recs, Is.Empty, "Dislike-only user should not have recommendations generated");
        Assert.That(user2Recs, Is.Not.Empty, "User with a like should have recommendations generated");
    }

    [Test]
    public async Task GenerateRecommendationsAsync_ExcludesInactiveSongsWhenPreviouslyCached()
    {
        // Arrange - seed an inactive song that already has a cached recommendation entry
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "song1.mp3", IsAlbumCover = false, IsActive = false, IsEnabled = true }
        };
        var context = await CreateAndSeedDatabase(songs);

        context.RecommendedPlaylists.Add(new RecommendedPlaylist
        {
            UserId = 1,
            SongMetadataId = 1,
            DisplayOrder = 1,
            GeneratedAt = DateTime.UtcNow,
            Score = 5.0
        });
        await context.SaveChangesAsync();

        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act - GenerateRecommendationsAsync clears old cached entries and rebuilds; inactive
        // songs must not appear in the regenerated results.
        var result = await service.GenerateRecommendationsAsync(userId: 1);

        // Assert
        var recommendedSongIds = result.Select(r => r.SongMetadataId).ToList();
        Assert.That(recommendedSongIds, Does.Not.Contain(1), "Inactive song should not appear even if previously cached");
    }

    [Test]
    public async Task GetRecommendedPlaylistAsync_RegeneratesWhenAllCachedSongsBecomeInactiveOrDisabled()
    {
        // Arrange - seed one active and one inactive song; only the inactive one has a cached
        // recommendation so that after filtering the cached list is empty and a regeneration
        // must occur (returning the active song instead).
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "inactive.mp3", IsAlbumCover = false, IsActive = false, IsEnabled = true, NumberOfStreams = 10 },
            new() { Id = 2, Mp3BlobPath = "active.mp3",   IsAlbumCover = false, IsActive = true,  IsEnabled = true, NumberOfStreams = 100 }
        };
        var context = await CreateAndSeedDatabase(songs);

        // Only the inactive song is in the cache
        context.RecommendedPlaylists.Add(new RecommendedPlaylist
        {
            UserId = 1,
            SongMetadataId = 1,
            DisplayOrder = 1,
            GeneratedAt = DateTime.UtcNow,
            Score = 5.0
        });
        await context.SaveChangesAsync();

        var service = new RecommendationService(CreateDbContextFactory(), _mockTopStreamedPlaylistService.Object, _mockLogger.Object);

        // Act - call GenerateRecommendationsAsync directly (in DEBUG builds GetRecommendedPlaylistAsync
        // always regenerates, so this directly exercises the regeneration path that the production
        // RELEASE fallback relies on when all cached entries are inactive/disabled).
        var result = await service.GenerateRecommendationsAsync(userId: 1);

        // Assert - the inactive song must not appear; the active song should be returned
        var recommendedSongIds = result.Select(r => r.SongMetadataId).ToList();
        Assert.That(recommendedSongIds, Does.Not.Contain(1), "Inactive song should not be recommended after regeneration");
        Assert.That(recommendedSongIds, Does.Contain(2), "Active song should appear in regenerated recommendations");
    }
}
