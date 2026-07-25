using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
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

    private async Task<SongMetadata> CreateTestSongMetadata(int numberOfStreams = 0, bool displayOnHomePage = false)
    {
        using var context = new AppDbContext(_contextOptions);
        var metadata = new SongMetadata
        {
            BlobPath = "test/song.mp3",
            Mp3BlobPath = "test/song.mp3",
            AlbumName = "Test Album",
            NumberOfStreams = numberOfStreams,
            DisplayOnHomePage = displayOnHomePage,
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
    public async Task IncrementStreamCountAsync_AdminStreams_IncrementsCount()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 10);
        var adminUserId = 1;

        // Act - Admin streams a song
        var result = await _service.IncrementStreamCountAsync(metadata.Id, adminUserId, isAdmin: true);

        // Assert - Admin streams count like any other listener's
        Assert.That(result, Is.EqualTo(11));

        using var verifyContext = new AppDbContext(_contextOptions);
        var updatedMetadata = await verifyContext.SongMetadata.FindAsync(metadata.Id);
        Assert.That(updatedMetadata?.NumberOfStreams, Is.EqualTo(11));
    }

    [Test]
    public async Task IncrementStreamCountAsync_AdminStreams_CreatesStreamRecord()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 10);
        var adminUserId = 1;

        // Act
        await _service.IncrementStreamCountAsync(metadata.Id, adminUserId, isAdmin: true);

        // Assert
        using var verifyContext = new AppDbContext(_contextOptions);
        var streamRecord = await verifyContext.SongStreams
            .FirstOrDefaultAsync(s => s.SongMetadataId == metadata.Id);
        Assert.That(streamRecord, Is.Not.Null);
        Assert.That(streamRecord.StreamerUserId, Is.EqualTo(adminUserId));
    }

    [Test]
    public async Task IncrementStreamCountAsync_FeaturedAdminWithExistingStream_IncrementsCountAndCreatesRecord()
    {
        // Arrange - admins play everything in full without a subscription, so the featured-song
        // "one free stream per non-subscriber" cap must not apply to them.
        var metadata = await CreateTestSongMetadata(numberOfStreams: 7, displayOnHomePage: true);
        var adminUserId = 1;
        using (var setupContext = new AppDbContext(_contextOptions))
        {
            setupContext.SongStreams.Add(new SongStream
            {
                SongMetadataId = metadata.Id,
                StreamerUserId = adminUserId,
                CreatedDate = DateTime.UtcNow.AddMinutes(-5)
            });
            await setupContext.SaveChangesAsync();
        }

        // Act
        var result = await _service.IncrementStreamCountAsync(metadata.Id, adminUserId, isAdmin: true);

        // Assert
        Assert.That(result, Is.EqualTo(8));

        using var verifyContext = new AppDbContext(_contextOptions);
        var streamRecords = await verifyContext.SongStreams
            .Where(s => s.SongMetadataId == metadata.Id && s.StreamerUserId == adminUserId)
            .ToListAsync();
        Assert.That(streamRecords, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task IncrementStreamCountAsync_AdminStreamsOwnSong_DoesNotIncrementCount()
    {
        // Arrange - the creator's-own-song rule still wins over the admin role.
        var (metadata, creator, user) = await CreateTestSongWithCreator(numberOfStreams: 5);

        // Act
        var result = await _service.IncrementStreamCountAsync(metadata.Id, user.Id, isAdmin: true);

        // Assert
        Assert.That(result, Is.EqualTo(5));

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
    public async Task IncrementStreamCountAsync_FeaturedNonSubscriberFirstStream_IncrementsCountAndCreatesRecord()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 7, displayOnHomePage: true);
        var streamerUserId = 999;

        // Act
        var result = await _service.IncrementStreamCountAsync(metadata.Id, streamerUserId);

        // Assert
        Assert.That(result, Is.EqualTo(8));

        using var verifyContext = new AppDbContext(_contextOptions);
        var streamRecords = await verifyContext.SongStreams
            .Where(s => s.SongMetadataId == metadata.Id && s.StreamerUserId == streamerUserId)
            .ToListAsync();
        Assert.That(streamRecords, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task IncrementStreamCountAsync_FeaturedNonSubscriberWithExistingStream_DoesNotIncrementOrCreateRecord()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 7, displayOnHomePage: true);
        var streamerUserId = 999;
        using (var setupContext = new AppDbContext(_contextOptions))
        {
            setupContext.SongStreams.Add(new SongStream
            {
                SongMetadataId = metadata.Id,
                StreamerUserId = streamerUserId,
                CreatedDate = DateTime.UtcNow.AddMinutes(-5)
            });
            await setupContext.SaveChangesAsync();
        }

        // Act
        var result = await _service.IncrementStreamCountAsync(metadata.Id, streamerUserId);

        // Assert
        Assert.That(result, Is.EqualTo(7));

        using var verifyContext = new AppDbContext(_contextOptions);
        var updatedMetadata = await verifyContext.SongMetadata.FindAsync(metadata.Id);
        var streamRecords = await verifyContext.SongStreams
            .Where(s => s.SongMetadataId == metadata.Id && s.StreamerUserId == streamerUserId)
            .ToListAsync();

        Assert.That(updatedMetadata?.NumberOfStreams, Is.EqualTo(7));
        Assert.That(streamRecords, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task IncrementStreamCountAsync_FeaturedAnonymousWithExistingStream_IncrementsWhenClientReportsStream()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 7, displayOnHomePage: true);
        using (var setupContext = new AppDbContext(_contextOptions))
        {
            setupContext.SongStreams.Add(new SongStream
            {
                SongMetadataId = metadata.Id,
                StreamerUserId = null,
                CreatedDate = DateTime.UtcNow.AddMinutes(-5)
            });
            await setupContext.SaveChangesAsync();
        }

        // Act
        var result = await _service.IncrementStreamCountAsync(metadata.Id);

        // Assert
        Assert.That(result, Is.EqualTo(8));

        using var verifyContext = new AppDbContext(_contextOptions);
        var streamRecords = await verifyContext.SongStreams
            .Where(s => s.SongMetadataId == metadata.Id && s.StreamerUserId == null)
            .ToListAsync();
        Assert.That(streamRecords, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task IncrementStreamCountAsync_FeaturedSubscriberWithExistingStream_IncrementsCountAndCreatesRecord()
    {
        // Arrange
        var metadata = await CreateTestSongMetadata(numberOfStreams: 7, displayOnHomePage: true);
        var streamerUserId = 999;
        using (var setupContext = new AppDbContext(_contextOptions))
        {
            setupContext.Subscriptions.Add(new Subscription
            {
                UserId = streamerUserId,
                Status = SubscriptionStatuses.Active,
                StartDate = DateTime.UtcNow.AddDays(-1),
                EndDate = DateTime.UtcNow.AddDays(1),
                MonthlyPrice = 3.99m
            });
            setupContext.SongStreams.Add(new SongStream
            {
                SongMetadataId = metadata.Id,
                StreamerUserId = streamerUserId,
                CreatedDate = DateTime.UtcNow.AddMinutes(-5)
            });
            await setupContext.SaveChangesAsync();
        }

        // Act
        var result = await _service.IncrementStreamCountAsync(metadata.Id, streamerUserId);

        // Assert
        Assert.That(result, Is.EqualTo(8));

        using var verifyContext = new AppDbContext(_contextOptions);
        var streamRecords = await verifyContext.SongStreams
            .Where(s => s.SongMetadataId == metadata.Id && s.StreamerUserId == streamerUserId)
            .ToListAsync();
        Assert.That(streamRecords, Has.Count.EqualTo(2));
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

    [Test]
    public async Task IncrementStreamCountAsync_NullCreatorId_ActiveCreatorStreams_DoesNotIncrementAndAutoFixes()
    {
        // Arrange - Create a song WITHOUT CreatorId, and a separate active creator
        using var setupContext = new AppDbContext(_contextOptions);
        var user = new ApplicationUser
        {
            UserName = "creator_noid@test.com",
            Email = "creator_noid@test.com",
            NormalizedEmail = "CREATOR_NOID@TEST.COM",
            NormalizedUserName = "CREATOR_NOID@TEST.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        setupContext.Users.Add(user);
        await setupContext.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            IsActive = true,
            OnboardingStatus = CreatorOnboardingStatus.Completed
        };
        setupContext.Creators.Add(creator);
        await setupContext.SaveChangesAsync();

        // Song with NULL CreatorId (simulates the upload bug)
        var metadata = new SongMetadata
        {
            BlobPath = "nullcreator/song.mp3",
            Mp3BlobPath = "nullcreator/song.mp3",
            AlbumName = "Test",
            NumberOfStreams = 5,
            CreatorId = null, // BUG: should have been set during upload
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        setupContext.SongMetadata.Add(metadata);
        await setupContext.SaveChangesAsync();

        // Act - Active creator streams the song
        var result = await _service.IncrementStreamCountAsync(metadata.Id, user.Id);

        // Assert - Count should NOT be incremented (treated as creator's own song)
        Assert.That(result, Is.EqualTo(5));

        // Verify CreatorId was auto-fixed
        using var verifyContext = new AppDbContext(_contextOptions);
        var updatedSong = await verifyContext.SongMetadata.FindAsync(metadata.Id);
        Assert.That(updatedSong?.CreatorId, Is.EqualTo(creator.Id));

        // Verify no SongStream record was created
        var streamRecord = await verifyContext.SongStreams
            .FirstOrDefaultAsync(s => s.SongMetadataId == metadata.Id);
        Assert.That(streamRecord, Is.Null);
    }

}
