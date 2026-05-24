using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class TrackLengthRepairServiceTests
{
    private Mock<IAzureStorageService> _mockStorageService = null!;
    private Mock<IMusicService> _mockMusicService = null!;
    private Mock<ILogger<TrackLengthRepairService>> _mockLogger = null!;
    private DbContextOptions<AppDbContext> _options = null!;
    private TrackLengthRepairService _service = null!;
    private AppDbContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _mockStorageService = new Mock<IAzureStorageService>();
        _mockMusicService = new Mock<IMusicService>();
        _mockLogger = new Mock<ILogger<TrackLengthRepairService>>();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"TrackLengthRepairTests_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_options);

        _service = new TrackLengthRepairService(
            new TestDbContextFactory(_options),
            _mockStorageService.Object,
            _mockMusicService.Object,
            _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task RepairMissingTrackLengthsAsync_UpdatesActivePlayableSongsWithMissingLength()
    {
        var originalUpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _context.SongMetadata.Add(new SongMetadata
        {
            Id = 1,
            BlobPath = "songs/test-song.mp3",
            Mp3BlobPath = "songs/test-song.mp3",
            IsActive = true,
            IsEnabled = true,
            IsAlbumCover = false,
            TrackLength = null,
            UpdatedAt = originalUpdatedAt
        });
        await _context.SaveChangesAsync();

        _mockStorageService
            .Setup(service => service.OpenReadAsync("songs/test-song.mp3"))
            .ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));
        _mockMusicService
            .Setup(service => service.GetAudioDurationAsync(It.IsAny<Stream>(), "test-song.mp3"))
            .ReturnsAsync(245.67);

        var repaired = await _service.RepairMissingTrackLengthsAsync();

        Assert.That(repaired, Is.EqualTo(1));

        await using var verifyContext = new AppDbContext(_options);
        var saved = await verifyContext.SongMetadata.SingleAsync(song => song.Id == 1);
        Assert.That(saved.TrackLength, Is.EqualTo(245.67));
        Assert.That(saved.UpdatedAt, Is.GreaterThan(originalUpdatedAt));

        _mockStorageService.Verify(service => service.OpenReadAsync("songs/test-song.mp3"), Times.Once);
        _mockMusicService.Verify(service => service.GetAudioDurationAsync(It.IsAny<Stream>(), "test-song.mp3"), Times.Once);
    }

    [Test]
    public async Task RepairMissingTrackLengthsAsync_RepairsLegacyBlobPathOnlyMp3AndBackfillsMp3Path()
    {
        var originalUpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _context.SongMetadata.Add(new SongMetadata
        {
            Id = 1,
            BlobPath = "legacy/test-song.mp3",
            Mp3BlobPath = string.Empty,
            IsActive = true,
            IsEnabled = true,
            IsAlbumCover = false,
            TrackLength = null,
            UpdatedAt = originalUpdatedAt
        });
        await _context.SaveChangesAsync();

        _mockStorageService
            .Setup(service => service.OpenReadAsync("legacy/test-song.mp3"))
            .ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));
        _mockMusicService
            .Setup(service => service.GetAudioDurationAsync(It.IsAny<Stream>(), "test-song.mp3"))
            .ReturnsAsync(201.5);

        var repaired = await _service.RepairMissingTrackLengthsAsync();

        Assert.That(repaired, Is.EqualTo(1));

        await using var verifyContext = new AppDbContext(_options);
        var saved = await verifyContext.SongMetadata.SingleAsync(song => song.Id == 1);
        Assert.That(saved.TrackLength, Is.EqualTo(201.5));
        Assert.That(saved.Mp3BlobPath, Is.EqualTo("legacy/test-song.mp3"));
        Assert.That(saved.UpdatedAt, Is.GreaterThan(originalUpdatedAt));

        _mockStorageService.Verify(service => service.OpenReadAsync("legacy/test-song.mp3"), Times.Once);
        _mockMusicService.Verify(service => service.GetAudioDurationAsync(It.IsAny<Stream>(), "test-song.mp3"), Times.Once);
    }

    [Test]
    public async Task RepairMissingTrackLengthsAsync_IgnoresLegacyBlobPathWhenItIsNotMp3()
    {
        _context.SongMetadata.Add(new SongMetadata
        {
            Id = 1,
            BlobPath = "legacy/test-song.jpg",
            Mp3BlobPath = string.Empty,
            IsActive = true,
            IsEnabled = true,
            IsAlbumCover = false,
            TrackLength = null
        });
        await _context.SaveChangesAsync();

        var repaired = await _service.RepairMissingTrackLengthsAsync();

        Assert.That(repaired, Is.EqualTo(0));

        await using var verifyContext = new AppDbContext(_options);
        var saved = await verifyContext.SongMetadata.SingleAsync(song => song.Id == 1);
        Assert.That(saved.TrackLength, Is.Null);
        Assert.That(saved.Mp3BlobPath, Is.Empty);

        _mockStorageService.Verify(service => service.OpenReadAsync(It.IsAny<string>()), Times.Never);
        _mockMusicService.Verify(service => service.GetAudioDurationAsync(It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task RepairMissingTrackLengthsAsync_SkipsSongsWhenBlobStreamIsMissingOrEmpty()
    {
        _context.SongMetadata.Add(new SongMetadata
        {
            Id = 1,
            BlobPath = "songs/empty-song.mp3",
            Mp3BlobPath = "songs/empty-song.mp3",
            IsActive = true,
            IsEnabled = true,
            IsAlbumCover = false,
            TrackLength = null
        });
        await _context.SaveChangesAsync();

        _mockStorageService
            .Setup(service => service.OpenReadAsync("songs/empty-song.mp3"))
            .ReturnsAsync(Stream.Null);

        var repaired = await _service.RepairMissingTrackLengthsAsync();

        Assert.That(repaired, Is.EqualTo(0));

        await using var verifyContext = new AppDbContext(_options);
        var saved = await verifyContext.SongMetadata.SingleAsync(song => song.Id == 1);
        Assert.That(saved.TrackLength, Is.Null);

        _mockMusicService.Verify(service => service.GetAudioDurationAsync(It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task RepairMissingTrackLengthsAsync_SkipsSongsWhenDurationCannotBeRecovered()
    {
        _context.SongMetadata.Add(new SongMetadata
        {
            Id = 1,
            BlobPath = "songs/no-duration.mp3",
            Mp3BlobPath = "songs/no-duration.mp3",
            IsActive = true,
            IsEnabled = true,
            IsAlbumCover = false,
            TrackLength = null
        });
        await _context.SaveChangesAsync();

        _mockStorageService
            .Setup(service => service.OpenReadAsync("songs/no-duration.mp3"))
            .ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));
        _mockMusicService
            .Setup(service => service.GetAudioDurationAsync(It.IsAny<Stream>(), "no-duration.mp3"))
            .ReturnsAsync((double?)null);

        var repaired = await _service.RepairMissingTrackLengthsAsync();

        Assert.That(repaired, Is.EqualTo(0));

        await using var verifyContext = new AppDbContext(_options);
        var saved = await verifyContext.SongMetadata.SingleAsync(song => song.Id == 1);
        Assert.That(saved.TrackLength, Is.Null);
    }

    [Test]
    public async Task RepairMissingTrackLengthsAsync_RepairsDisabledActiveSongs()
    {
        _context.SongMetadata.Add(new SongMetadata
        {
            Id = 1,
            BlobPath = "songs/disabled-active.mp3",
            Mp3BlobPath = "songs/disabled-active.mp3",
            IsActive = true,
            IsEnabled = false,
            IsAlbumCover = false,
            TrackLength = null
        });
        await _context.SaveChangesAsync();

        _mockStorageService
            .Setup(service => service.OpenReadAsync("songs/disabled-active.mp3"))
            .ReturnsAsync(new MemoryStream(new byte[] { 5, 5, 5 }));
        _mockMusicService
            .Setup(service => service.GetAudioDurationAsync(It.IsAny<Stream>(), "disabled-active.mp3"))
            .ReturnsAsync(111.25);

        var repaired = await _service.RepairMissingTrackLengthsAsync();

        Assert.That(repaired, Is.EqualTo(1));

        await using var verifyContext = new AppDbContext(_options);
        var saved = await verifyContext.SongMetadata.SingleAsync(song => song.Id == 1);
        Assert.That(saved.TrackLength, Is.EqualTo(111.25));
    }

    [Test]
    public async Task RepairMissingTrackLengthsAsync_IgnoresInactiveImageOnlyAndAlreadySizedSongs()
    {
        _context.SongMetadata.AddRange(
            new SongMetadata
            {
                Id = 1,
                BlobPath = "songs/already-set.mp3",
                Mp3BlobPath = "songs/already-set.mp3",
                IsActive = true,
                IsEnabled = true,
                IsAlbumCover = false,
                TrackLength = 180
            },
            new SongMetadata
            {
                Id = 2,
                BlobPath = "songs/inactive.mp3",
                Mp3BlobPath = "songs/inactive.mp3",
                IsActive = false,
                IsEnabled = true,
                IsAlbumCover = false,
                TrackLength = null
            },
            new SongMetadata
            {
                Id = 3,
                BlobPath = "songs/cover.jpg",
                ImageBlobPath = "songs/cover.jpg",
                IsActive = true,
                IsEnabled = true,
                IsAlbumCover = true,
                TrackLength = null
            });
        await _context.SaveChangesAsync();

        var repaired = await _service.RepairMissingTrackLengthsAsync();

        Assert.That(repaired, Is.EqualTo(0));
        _mockStorageService.Verify(service => service.OpenReadAsync(It.IsAny<string>()), Times.Never);
        _mockMusicService.Verify(service => service.GetAudioDurationAsync(It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task RepairMissingTrackLengthsAsync_ContinuesWhenOneCandidateFails()
    {
        _context.SongMetadata.AddRange(
            new SongMetadata
            {
                Id = 1,
                BlobPath = "songs/failing-song.mp3",
                Mp3BlobPath = "songs/failing-song.mp3",
                IsActive = true,
                IsEnabled = true,
                IsAlbumCover = false,
                TrackLength = null
            },
            new SongMetadata
            {
                Id = 2,
                BlobPath = "songs/working-song.mp3",
                Mp3BlobPath = "songs/working-song.mp3",
                IsActive = true,
                IsEnabled = true,
                IsAlbumCover = false,
                TrackLength = null
            });
        await _context.SaveChangesAsync();

        _mockStorageService
            .Setup(service => service.OpenReadAsync("songs/failing-song.mp3"))
            .ThrowsAsync(new IOException("blob read failed"));
        _mockStorageService
            .Setup(service => service.OpenReadAsync("songs/working-song.mp3"))
            .ReturnsAsync(new MemoryStream(new byte[] { 9, 9, 9 }));
        _mockMusicService
            .Setup(service => service.GetAudioDurationAsync(It.IsAny<Stream>(), "working-song.mp3"))
            .ReturnsAsync(88.5);

        var repaired = await _service.RepairMissingTrackLengthsAsync();

        Assert.That(repaired, Is.EqualTo(1));

        await using var verifyContext = new AppDbContext(_options);
        var failedSong = await verifyContext.SongMetadata.SingleAsync(song => song.Id == 1);
        var repairedSong = await verifyContext.SongMetadata.SingleAsync(song => song.Id == 2);

        Assert.That(failedSong.TrackLength, Is.Null);
        Assert.That(repairedSong.TrackLength, Is.EqualTo(88.5));
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
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