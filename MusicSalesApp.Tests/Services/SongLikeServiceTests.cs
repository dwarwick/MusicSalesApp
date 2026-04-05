using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Hubs;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class SongLikeServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<IHubContext<LikeCountHub>> _mockHubContext;
    private SongLikeService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        // Create in-memory database for testing
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"SongLikeTestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);

        // Mock the context factory to return our in-memory context
        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        // Mock IHubContext for SignalR broadcasting
        _mockHubContext = new Mock<IHubContext<LikeCountHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        _service = new SongLikeService(_mockContextFactory.Object, _mockHubContext.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task GetLikeCountsAsync_NoLikesOrDislikes_ReturnsZeroCounts()
    {
        // Arrange
        var songMetadataId = 1;

        // Act
        var (likeCount, dislikeCount) = await _service.GetLikeCountsAsync(songMetadataId);

        // Assert
        Assert.That(likeCount, Is.EqualTo(0));
        Assert.That(dislikeCount, Is.EqualTo(0));
    }

    [Test]
    public async Task GetLikeCountsAsync_WithLikesAndDislikes_ReturnsCorrectCounts()
    {
        // Arrange
        var songMetadataId = 1;
        await _context.SongLikes.AddRangeAsync(
            new SongLike { UserId = 1, SongMetadataId = songMetadataId, IsLike = true },
            new SongLike { UserId = 2, SongMetadataId = songMetadataId, IsLike = true },
            new SongLike { UserId = 3, SongMetadataId = songMetadataId, IsLike = false },
            new SongLike { UserId = 4, SongMetadataId = songMetadataId, IsLike = false },
            new SongLike { UserId = 5, SongMetadataId = songMetadataId, IsLike = false }
        );
        await _context.SaveChangesAsync();

        // Act
        var (likeCount, dislikeCount) = await _service.GetLikeCountsAsync(songMetadataId);

        // Assert
        Assert.That(likeCount, Is.EqualTo(2));
        Assert.That(dislikeCount, Is.EqualTo(3));
    }

    [Test]
    public async Task GetLikeCountsAsync_OnlyCountsForSpecificSong()
    {
        // Arrange
        var songMetadataId = 1;
        var otherSongId = 2;
        await _context.SongLikes.AddRangeAsync(
            new SongLike { UserId = 1, SongMetadataId = songMetadataId, IsLike = true },
            new SongLike { UserId = 2, SongMetadataId = otherSongId, IsLike = true },
            new SongLike { UserId = 3, SongMetadataId = songMetadataId, IsLike = false }
        );
        await _context.SaveChangesAsync();

        // Act
        var (likeCount, dislikeCount) = await _service.GetLikeCountsAsync(songMetadataId);

        // Assert
        Assert.That(likeCount, Is.EqualTo(1));
        Assert.That(dislikeCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GetUserLikeStatusAsync_NoPreference_ReturnsNull()
    {
        // Arrange
        var userId = 1;
        var songMetadataId = 1;

        // Act
        var status = await _service.GetUserLikeStatusAsync(userId, songMetadataId);

        // Assert
        Assert.That(status, Is.Null);
    }

    [Test]
    public async Task GetUserLikeStatusAsync_UserLiked_ReturnsTrue()
    {
        // Arrange
        var userId = 1;
        var songMetadataId = 1;
        await _context.SongLikes.AddAsync(new SongLike
        {
            UserId = userId,
            SongMetadataId = songMetadataId,
            IsLike = true
        });
        await _context.SaveChangesAsync();

        // Act
        var status = await _service.GetUserLikeStatusAsync(userId, songMetadataId);

        // Assert
        Assert.That(status, Is.True);
    }

    [Test]
    public async Task GetUserLikeStatusAsync_UserDisliked_ReturnsFalse()
    {
        // Arrange
        var userId = 1;
        var songMetadataId = 1;
        await _context.SongLikes.AddAsync(new SongLike
        {
            UserId = userId,
            SongMetadataId = songMetadataId,
            IsLike = false
        });
        await _context.SaveChangesAsync();

        // Act
        var status = await _service.GetUserLikeStatusAsync(userId, songMetadataId);

        // Assert
        Assert.That(status, Is.False);
    }

    [Test]
    public async Task ToggleLikeAsync_NoExistingPreference_CreatesLike()
    {
        // Arrange
        var userId = 1;
        var songMetadataId = 1;

        // Act
        var result = await _service.ToggleLikeAsync(userId, songMetadataId);

        // Assert
        Assert.That(result, Is.True);
        var savedLike = await _context.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);
        Assert.That(savedLike, Is.Not.Null);
        Assert.That(savedLike.IsLike, Is.True);
    }

    [Test]
    public async Task ToggleLikeAsync_AlreadyLiked_RemovesLike()
    {
        // Arrange
        var userId = 1;
        var songMetadataId = 1;
        await _context.SongLikes.AddAsync(new SongLike
        {
            UserId = userId,
            SongMetadataId = songMetadataId,
            IsLike = true
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ToggleLikeAsync(userId, songMetadataId);

        // Assert
        Assert.That(result, Is.False);
        var savedLike = await _context.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);
        Assert.That(savedLike, Is.Null);
    }

    [Test]
    public async Task ToggleLikeAsync_PreviouslyDisliked_ChangesToLike()
    {
        // Arrange
        var userId = 1;
        var songMetadataId = 1;
        await _context.SongLikes.AddAsync(new SongLike
        {
            UserId = userId,
            SongMetadataId = songMetadataId,
            IsLike = false
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        var result = await _service.ToggleLikeAsync(userId, songMetadataId);

        // Assert
        Assert.That(result, Is.True);
        
        // Create new context to verify the update
        using var verifyContext = new AppDbContext(_contextOptions);
        var savedLike = await verifyContext.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);
        Assert.That(savedLike, Is.Not.Null);
        Assert.That(savedLike.IsLike, Is.True);
    }

    [Test]
    public async Task ToggleDislikeAsync_NoExistingPreference_CreatesDislike()
    {
        // Arrange
        var userId = 1;
        var songMetadataId = 1;

        // Act
        var result = await _service.ToggleDislikeAsync(userId, songMetadataId);

        // Assert
        Assert.That(result, Is.True);
        var savedLike = await _context.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);
        Assert.That(savedLike, Is.Not.Null);
        Assert.That(savedLike.IsLike, Is.False);
    }

    [Test]
    public async Task ToggleDislikeAsync_AlreadyDisliked_RemovesDislike()
    {
        // Arrange
        var userId = 1;
        var songMetadataId = 1;
        await _context.SongLikes.AddAsync(new SongLike
        {
            UserId = userId,
            SongMetadataId = songMetadataId,
            IsLike = false
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ToggleDislikeAsync(userId, songMetadataId);

        // Assert
        Assert.That(result, Is.False);
        var savedLike = await _context.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);
        Assert.That(savedLike, Is.Null);
    }

    [Test]
    public async Task ToggleDislikeAsync_PreviouslyLiked_ChangesToDislike()
    {
        // Arrange
        var userId = 1;
        var songMetadataId = 1;
        await _context.SongLikes.AddAsync(new SongLike
        {
            UserId = userId,
            SongMetadataId = songMetadataId,
            IsLike = true
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        var result = await _service.ToggleDislikeAsync(userId, songMetadataId);

        // Assert
        Assert.That(result, Is.True);
        
        // Create new context to verify the update
        using var verifyContext = new AppDbContext(_contextOptions);
        var savedLike = await verifyContext.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);
        Assert.That(savedLike, Is.Not.Null);
        Assert.That(savedLike.IsLike, Is.False);
    }

    [Test]
    public async Task ToggleLikeAsync_UpdatesTimestampCorrectly()
    {
        // Arrange
        var userId = 1;
        var songMetadataId = 1;
        var originalTime = DateTime.UtcNow.AddMinutes(-5);
        await _context.SongLikes.AddAsync(new SongLike
        {
            UserId = userId,
            SongMetadataId = songMetadataId,
            IsLike = false,
            CreatedAt = originalTime,
            UpdatedAt = originalTime
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        await _service.ToggleLikeAsync(userId, songMetadataId);

        // Assert
        // Create new context to verify the update
        using var verifyContext = new AppDbContext(_contextOptions);
        var updatedLike = await verifyContext.SongLikes
            .FirstOrDefaultAsync(sl => sl.UserId == userId && sl.SongMetadataId == songMetadataId);
        Assert.That(updatedLike, Is.Not.Null);
        Assert.That(updatedLike.CreatedAt, Is.EqualTo(originalTime));
        Assert.That(updatedLike.UpdatedAt, Is.GreaterThan(originalTime));
    }

    [Test]
    public async Task MultipleUsers_CanLikeSameSong()
    {
        // Arrange
        var songMetadataId = 1;

        // Act
        await _service.ToggleLikeAsync(1, songMetadataId);
        await _service.ToggleLikeAsync(2, songMetadataId);
        await _service.ToggleLikeAsync(3, songMetadataId);

        // Assert
        var (likeCount, dislikeCount) = await _service.GetLikeCountsAsync(songMetadataId);
        Assert.That(likeCount, Is.EqualTo(3));
        Assert.That(dislikeCount, Is.EqualTo(0));
    }

    [Test]
    public async Task SingleUser_CanLikeMultipleSongs()
    {
        // Arrange
        var userId = 1;

        // Act
        await _service.ToggleLikeAsync(userId, 1);
        await _service.ToggleLikeAsync(userId, 2);
        await _service.ToggleLikeAsync(userId, 3);

        // Assert
        var status1 = await _service.GetUserLikeStatusAsync(userId, 1);
        var status2 = await _service.GetUserLikeStatusAsync(userId, 2);
        var status3 = await _service.GetUserLikeStatusAsync(userId, 3);
        Assert.That(status1, Is.True);
        Assert.That(status2, Is.True);
        Assert.That(status3, Is.True);
    }

    [Test]
    public async Task GetBulkLikeCountsAsync_EmptyList_ReturnsEmptyDictionary()
    {
        // Act
        var result = await _service.GetBulkLikeCountsAsync(new List<int>());

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetBulkLikeCountsAsync_NoLikes_ReturnsEmptyDictionary()
    {
        // Act
        var result = await _service.GetBulkLikeCountsAsync(new List<int> { 1, 2, 3 });

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetBulkLikeCountsAsync_WithLikes_ReturnsCorrectCounts()
    {
        // Arrange
        await _context.SongLikes.AddRangeAsync(
            new SongLike { UserId = 1, SongMetadataId = 10, IsLike = true },
            new SongLike { UserId = 2, SongMetadataId = 10, IsLike = true },
            new SongLike { UserId = 3, SongMetadataId = 10, IsLike = false },
            new SongLike { UserId = 1, SongMetadataId = 20, IsLike = true },
            new SongLike { UserId = 1, SongMetadataId = 30, IsLike = false }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetBulkLikeCountsAsync(new List<int> { 10, 20, 30 });

        // Assert - only counts likes (IsLike=true), not dislikes
        Assert.That(result[10], Is.EqualTo(2));
        Assert.That(result[20], Is.EqualTo(1));
        Assert.That(result.ContainsKey(30), Is.False); // no likes, only dislikes
    }

    [Test]
    public async Task GetBulkLikeCountsAsync_OnlyCountsRequestedSongs()
    {
        // Arrange
        await _context.SongLikes.AddRangeAsync(
            new SongLike { UserId = 1, SongMetadataId = 100, IsLike = true },
            new SongLike { UserId = 2, SongMetadataId = 200, IsLike = true },
            new SongLike { UserId = 3, SongMetadataId = 300, IsLike = true }
        );
        await _context.SaveChangesAsync();

        // Act - only request songs 100 and 300
        var result = await _service.GetBulkLikeCountsAsync(new List<int> { 100, 300 });

        // Assert
        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[100], Is.EqualTo(1));
        Assert.That(result[300], Is.EqualTo(1));
        Assert.That(result.ContainsKey(200), Is.False);
    }

    [Test]
    public async Task GetBulkLikeCountsAsync_MultipleLikesPerSong_CountsCorrectly()
    {
        // Arrange
        await _context.SongLikes.AddRangeAsync(
            new SongLike { UserId = 1, SongMetadataId = 50, IsLike = true },
            new SongLike { UserId = 2, SongMetadataId = 50, IsLike = true },
            new SongLike { UserId = 3, SongMetadataId = 50, IsLike = true },
            new SongLike { UserId = 4, SongMetadataId = 50, IsLike = true },
            new SongLike { UserId = 5, SongMetadataId = 50, IsLike = true }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetBulkLikeCountsAsync(new List<int> { 50 });

        // Assert
        Assert.That(result[50], Is.EqualTo(5));
    }
}
