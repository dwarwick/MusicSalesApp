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
public class StreamCountServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<ILogger<StreamCountService>> _mockLogger;
    private Mock<IHubContext<StreamCountHub>> _mockHubContext;
    private Mock<IHubClients> _mockHubClients;
    private Mock<IClientProxy> _mockClientProxy;
    private StreamCountService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        // Create in-memory database for testing
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"StreamCountTestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);

        // Mock the context factory to return our in-memory context
        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _mockLogger = new Mock<ILogger<StreamCountService>>();

        // Mock SignalR hub context
        _mockClientProxy = new Mock<IClientProxy>();
        _mockHubClients = new Mock<IHubClients>();
        _mockHubClients.Setup(c => c.All).Returns(_mockClientProxy.Object);
        _mockHubContext = new Mock<IHubContext<StreamCountHub>>();
        _mockHubContext.Setup(c => c.Clients).Returns(_mockHubClients.Object);

        _service = new StreamCountService(_mockContextFactory.Object, _mockLogger.Object, _mockHubContext.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private async Task<SongMetadata> CreateTestSongMetadata(int numberOfStreams = 0)
    {
        using var context = new AppDbContext(_contextOptions);
        var metadata = new SongMetadata
        {
            BlobPath = "test/song.mp3",
            Mp3BlobPath = "test/song.mp3",
            AlbumName = "Test Album",
            NumberOfStreams = numberOfStreams,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.SongMetadata.Add(metadata);
        await context.SaveChangesAsync();
        return metadata;
    }

    private async Task<(SongMetadata metadata, Creator creator, ApplicationUser user)> CreateTestSongWithCreator(int numberOfStreams = 0)
    {
        using var context = new AppDbContext(_contextOptions);
        var user = new ApplicationUser
        {
            UserName = $"creator_{Guid.NewGuid()}@test.com",
            Email = $"creator_{Guid.NewGuid()}@test.com",
            NormalizedEmail = $"CREATOR_{Guid.NewGuid()}@TEST.COM",
            NormalizedUserName = $"CREATOR_{Guid.NewGuid()}@TEST.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            IsActive = true,
            OnboardingStatus = CreatorOnboardingStatus.Completed
        };
        context.Creators.Add(creator);
        await context.SaveChangesAsync();

        var metadata = new SongMetadata
        {
            BlobPath = "test/song.mp3",
            Mp3BlobPath = "test/song.mp3",
            AlbumName = "Test Album",
            NumberOfStreams = numberOfStreams,
            CreatorId = creator.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.SongMetadata.Add(metadata);
        await context.SaveChangesAsync();
        return (metadata, creator, user);
    }

    [Test]
    public async Task GetStreamCountAsync_SongExists_ReturnsStreamCount()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 42);

        // Act
        var result = await _service.GetStreamCountAsync(metadata.Id);

        // Assert
        Assert.That(result, Is.EqualTo(42));
    }

    [Test]
    public async Task GetStreamCountAsync_SongDoesNotExist_ReturnsZero()
    {
        // Arrange
        var nonExistentId = 9999;

        // Act
        var result = await _service.GetStreamCountAsync(nonExistentId);

        // Assert
        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public async Task IncrementStreamCountAsync_SongExists_IncrementsCountAndReturnsNewCount()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 5);

        // Act
        var result = await _service.IncrementStreamCountAsync(metadata.Id);

        // Assert
        Assert.That(result, Is.EqualTo(6));

        // Verify the database was updated
        using var verifyContext = new AppDbContext(_contextOptions);
        var updatedMetadata = await verifyContext.SongMetadata.FindAsync(metadata.Id);
        Assert.That(updatedMetadata?.NumberOfStreams, Is.EqualTo(6));
    }

    [Test]
    public async Task IncrementStreamCountAsync_SongDoesNotExist_ReturnsZero()
    {
        // Arrange
        var nonExistentId = 9999;

        // Act
        var result = await _service.IncrementStreamCountAsync(nonExistentId);

        // Assert
        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public async Task IncrementStreamCountAsync_MultipleIncrements_IncrementsCorrectly()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 0);

        // Act
        var result1 = await _service.IncrementStreamCountAsync(metadata.Id);
        var result2 = await _service.IncrementStreamCountAsync(metadata.Id);
        var result3 = await _service.IncrementStreamCountAsync(metadata.Id);

        // Assert
        Assert.That(result1, Is.EqualTo(1));
        Assert.That(result2, Is.EqualTo(2));
        Assert.That(result3, Is.EqualTo(3));

        // Verify the database was updated
        using var verifyContext = new AppDbContext(_contextOptions);
        var updatedMetadata = await verifyContext.SongMetadata.FindAsync(metadata.Id);
        Assert.That(updatedMetadata?.NumberOfStreams, Is.EqualTo(3));
    }

    [Test]
    public async Task NotifyStreamCountUpdated_TriggersEvent()
    {
        // Arrange
        var eventFired = false;
        var receivedSongId = 0;
        var receivedCount = 0;

        _service.OnStreamCountUpdated += (songId, count) =>
        {
            eventFired = true;
            receivedSongId = songId;
            receivedCount = count;
        };

        // Act
        _service.NotifyStreamCountUpdated(123, 456);

        // Assert
        Assert.That(eventFired, Is.True);
        Assert.That(receivedSongId, Is.EqualTo(123));
        Assert.That(receivedCount, Is.EqualTo(456));
    }

    [Test]
    public async Task IncrementStreamCountAsync_NotifiesSubscribers()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 10);
        var eventFired = false;
        var receivedSongId = 0;
        var receivedCount = 0;

        _service.OnStreamCountUpdated += (songId, count) =>
        {
            eventFired = true;
            receivedSongId = songId;
            receivedCount = count;
        };

        // Act
        await _service.IncrementStreamCountAsync(metadata.Id);

        // Assert
        Assert.That(eventFired, Is.True);
        Assert.That(receivedSongId, Is.EqualTo(metadata.Id));
        Assert.That(receivedCount, Is.EqualTo(11));
    }

    [Test]
    public async Task IncrementStreamCountAsync_RegularUser_CreatesStreamRecordAndIncrementsCount()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 5);
        var streamerUserId = 999;

        // Act
        var result = await _service.IncrementStreamCountAsync(metadata.Id, streamerUserId);

        // Assert
        Assert.That(result, Is.EqualTo(6));

        // Verify SongStream record was created
        using var verifyContext = new AppDbContext(_contextOptions);
        var streamRecord = await verifyContext.SongStreams
            .FirstOrDefaultAsync(s => s.SongMetadataId == metadata.Id);
        Assert.That(streamRecord, Is.Not.Null);
        Assert.That(streamRecord.StreamerUserId, Is.EqualTo(streamerUserId));
        Assert.That(streamRecord.SongMetadataId, Is.EqualTo(metadata.Id));
    }

    [Test]
    public async Task IncrementStreamCountAsync_CreatorStreamsOwnSong_DoesNotIncrementCount()
    {
        // Arrange
        var (metadata, creator, user) = await CreateTestSongWithCreator(numberOfStreams: 5);

        // Act - Creator streams their own song
        var result = await _service.IncrementStreamCountAsync(metadata.Id, user.Id);

        // Assert - Count should NOT be incremented
        Assert.That(result, Is.EqualTo(5));

        // Verify the database was NOT updated
        using var verifyContext = new AppDbContext(_contextOptions);
        var updatedMetadata = await verifyContext.SongMetadata.FindAsync(metadata.Id);
        Assert.That(updatedMetadata?.NumberOfStreams, Is.EqualTo(5));
    }

    [Test]
    public async Task IncrementStreamCountAsync_CreatorStreamsOwnSong_DoesNotCreateStreamRecord()
    {
        // Arrange
        var (metadata, creator, user) = await CreateTestSongWithCreator(numberOfStreams: 5);

        // Act
        var result = await _service.IncrementStreamCountAsync(metadata.Id, user.Id);

        // Assert - SongStream record should NOT be created for creator's own song
        using var verifyContext = new AppDbContext(_contextOptions);
        var streamRecord = await verifyContext.SongStreams
            .FirstOrDefaultAsync(s => s.SongMetadataId == metadata.Id);
        Assert.That(streamRecord, Is.Null);
    }

    [Test]
    public async Task IncrementStreamCountAsync_AdminStreams_DoesNotIncrementCount()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 10);
        var adminUserId = 1;

        // Act - Admin streams a song
        var result = await _service.IncrementStreamCountAsync(metadata.Id, adminUserId, isAdmin: true);

        // Assert - Count should NOT be incremented
        Assert.That(result, Is.EqualTo(10));

        // Verify the database was NOT updated
        using var verifyContext = new AppDbContext(_contextOptions);
        var updatedMetadata = await verifyContext.SongMetadata.FindAsync(metadata.Id);
        Assert.That(updatedMetadata?.NumberOfStreams, Is.EqualTo(10));
    }

    [Test]
    public async Task IncrementStreamCountAsync_AdminStreams_DoesNotCreateStreamRecord()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 10);
        var adminUserId = 1;

        // Act
        var result = await _service.IncrementStreamCountAsync(metadata.Id, adminUserId, isAdmin: true);

        // Assert - SongStream record should NOT be created for admin
        using var verifyContext = new AppDbContext(_contextOptions);
        var streamRecord = await verifyContext.SongStreams
            .FirstOrDefaultAsync(s => s.SongMetadataId == metadata.Id);
        Assert.That(streamRecord, Is.Null);
    }

    [Test]
    public async Task IncrementStreamCountAsync_UnauthenticatedUser_IncrementsCountAndCreatesRecord()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 3);

        // Act - No streamerUserId (unauthenticated)
        var result = await _service.IncrementStreamCountAsync(metadata.Id);

        // Assert - Count should be incremented
        Assert.That(result, Is.EqualTo(4));

        // Verify SongStream record was created with null StreamerUserId
        using var verifyContext = new AppDbContext(_contextOptions);
        var streamRecord = await verifyContext.SongStreams
            .FirstOrDefaultAsync(s => s.SongMetadataId == metadata.Id);
        Assert.That(streamRecord, Is.Not.Null);
        Assert.That(streamRecord.StreamerUserId, Is.Null);
    }

    [Test]
    public async Task IncrementStreamCountAsync_CreatorStreamsOtherCreatorSong_IncrementsCount()
    {
        // Arrange
        var (metadata, creator, songOwner) = await CreateTestSongWithCreator(numberOfStreams: 5);

        // Create another user who is NOT the creator of this song
        using var setupContext = new AppDbContext(_contextOptions);
        var otherUser = new ApplicationUser
        {
            UserName = "other@test.com",
            Email = "other@test.com",
            NormalizedEmail = "OTHER@TEST.COM",
            NormalizedUserName = "OTHER@TEST.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        setupContext.Users.Add(otherUser);
        await setupContext.SaveChangesAsync();

        // Act - Another user streams this song
        var result = await _service.IncrementStreamCountAsync(metadata.Id, otherUser.Id);

        // Assert - Count should be incremented
        Assert.That(result, Is.EqualTo(6));
    }
}
