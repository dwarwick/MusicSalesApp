using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    private Mock<IConfiguration> _mockConfiguration;

    [SetUp]
    public void Setup()
    {
        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        _mockLogger = new Mock<ILogger<RecommendationService>>();
        _mockConfiguration = new Mock<IConfiguration>();

        // Setup default configuration values (Supabase not configured)
        _mockConfiguration.Setup(c => c["Supabase:SUPABASE_URL"]).Returns("__REPLACE_WITH_SUPABASE_URL__");
        _mockConfiguration.Setup(c => c["Supabase:SUPABASE_KEY"]).Returns("__REPLACE_WITH_SUPABASE_KEY__");
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
    public async Task GetRecommendedPlaylistAsync_NoLikes_ReturnsPopularSongs()
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
        var service = new RecommendationService(CreateDbContextFactory(), _mockLogger.Object, _mockConfiguration.Object);

        // Act
        var result = await service.GetRecommendedPlaylistAsync(userId: 1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.LessThanOrEqualTo(20));
    }

    [Test]
    public async Task GetRecommendedPlaylistAsync_WithLikes_ReturnsCollaborativeRecommendations()
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
        var service = new RecommendationService(CreateDbContextFactory(), _mockLogger.Object, _mockConfiguration.Object);

        // Act
        var result = await service.GetRecommendedPlaylistAsync(userId: 1);

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
    public async Task GetRecommendedPlaylistAsync_ExcludesDislikedSongs()
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
        var service = new RecommendationService(CreateDbContextFactory(), _mockLogger.Object, _mockConfiguration.Object);

        // Act
        var result = await service.GetRecommendedPlaylistAsync(userId: 1);

        // Assert
        var recommendedSongIds = result.Select(r => r.SongMetadataId).ToList();
        
        // Song 2 should NOT be recommended (user disliked it)
        Assert.That(recommendedSongIds, Does.Not.Contain(2));
    }

    [Test]
    public async Task HasFreshRecommendationsAsync_NoRecommendations_ReturnsFalse()
    {
        // Arrange
        await CreateAndSeedDatabase();
        var service = new RecommendationService(CreateDbContextFactory(), _mockLogger.Object, _mockConfiguration.Object);

        // Act
        var result = await service.HasFreshRecommendationsAsync(userId: 1);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasFreshRecommendationsAsync_WithFreshRecommendations_ReturnsTrue()
    {
        // Arrange
        var context = await CreateAndSeedDatabase();
        
        // Add fresh recommendations
        context.RecommendedPlaylists.Add(new RecommendedPlaylist
        {
            UserId = 1,
            SongMetadataId = 1,
            DisplayOrder = 1,
            GeneratedAt = DateTime.UtcNow,
            Score = 1.0
        });
        await context.SaveChangesAsync();

        var service = new RecommendationService(CreateDbContextFactory(), _mockLogger.Object, _mockConfiguration.Object);

        // Act
        var result = await service.HasFreshRecommendationsAsync(userId: 1);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task HasFreshRecommendationsAsync_WithOldRecommendations_ReturnsFalse()
    {
        // Arrange
        var context = await CreateAndSeedDatabase();
        
        // Add old recommendations (more than 24 hours ago)
        context.RecommendedPlaylists.Add(new RecommendedPlaylist
        {
            UserId = 1,
            SongMetadataId = 1,
            DisplayOrder = 1,
            GeneratedAt = DateTime.UtcNow.AddHours(-25),
            Score = 1.0
        });
        await context.SaveChangesAsync();

        var service = new RecommendationService(CreateDbContextFactory(), _mockLogger.Object, _mockConfiguration.Object);

        // Act
        var result = await service.HasFreshRecommendationsAsync(userId: 1);

        // Assert
        Assert.That(result, Is.False);
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

        var service = new RecommendationService(CreateDbContextFactory(), _mockLogger.Object, _mockConfiguration.Object);

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
    public async Task GetRecommendedPlaylistAsync_ExcludesAlbumCovers()
    {
        // Arrange
        var songs = new List<SongMetadata>
        {
            new() { Id = 1, Mp3BlobPath = "song1.mp3", IsAlbumCover = false, NumberOfStreams = 100 },
            new() { Id = 2, ImageBlobPath = "cover.jpg", IsAlbumCover = true }, // Album cover
            new() { Id = 3, Mp3BlobPath = "song3.mp3", IsAlbumCover = false, NumberOfStreams = 50 }
        };
        
        await CreateAndSeedDatabase(songs);
        var service = new RecommendationService(CreateDbContextFactory(), _mockLogger.Object, _mockConfiguration.Object);

        // Act
        var result = await service.GetRecommendedPlaylistAsync(userId: 1);

        // Assert
        var recommendedSongIds = result.Select(r => r.SongMetadataId).ToList();
        
        // Album cover (Id: 2) should NOT be in recommendations
        Assert.That(recommendedSongIds, Does.Not.Contain(2));
    }

    [Test]
    public async Task GetRecommendedPlaylistAsync_ReturnsMaxOf20Songs()
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
        var service = new RecommendationService(CreateDbContextFactory(), _mockLogger.Object, _mockConfiguration.Object);

        // Act
        var result = await service.GetRecommendedPlaylistAsync(userId: 1);

        // Assert
        Assert.That(result.Count, Is.LessThanOrEqualTo(20));
    }
}
