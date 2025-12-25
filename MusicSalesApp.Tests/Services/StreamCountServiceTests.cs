using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class StreamCountServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<ILogger<StreamCountService>> _mockLogger;
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
        _service = new StreamCountService(_mockContextFactory.Object, _mockLogger.Object);
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
}
