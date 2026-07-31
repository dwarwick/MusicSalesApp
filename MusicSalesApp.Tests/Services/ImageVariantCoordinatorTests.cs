using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class ImageVariantCoordinatorTests
{
    private const string CoverArtPath = "3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b/3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b-coverart.jpg";
    private const string PersonaPath = "creator-12/persona-7.png";

    private Mock<IImageVariantService> _variantService = null!;
    private Mock<ILogger<ImageVariantCoordinator>> _logger = null!;
    private DbContextOptions<AppDbContext> _options = null!;
    private AppDbContext _context = null!;
    private ImageVariantCoordinator _coordinator = null!;

    [SetUp]
    public void SetUp()
    {
        _variantService = new Mock<IImageVariantService>();
        _logger = new Mock<ILogger<ImageVariantCoordinator>>();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ImageVariantCoordinatorTests_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_options);

        _coordinator = new ImageVariantCoordinator(
            new TestDbContextFactory(_options),
            _variantService.Object,
            _logger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private static ImageVariantResult Success(params int[] widths)
        => new(true, widths, 1500, 1500, 90_000, null);

    private async Task<SongMetadata> AddSongAsync(
        string imageBlobPath = CoverArtPath,
        string widths = null,
        int version = 0)
    {
        var song = new SongMetadata
        {
            Id = 1,
            SongTitle = "Night Drive",
            Mp3BlobPath = "3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b/3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b-music.mp3",
            ImageBlobPath = imageBlobPath,
            CoverArtVariantWidths = widths,
            CoverArtVariantVersion = version,
            IsActive = true,
            IsEnabled = true
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();
        return song;
    }

    private async Task<CreatorPersona> AddPersonaAsync(string imageBlobPath = PersonaPath, string widths = null)
    {
        var persona = new CreatorPersona
        {
            Id = 7,
            CreatorId = 12,
            Name = "Nova",
            ImageBlobPath = imageBlobPath,
            ImageVariantWidths = widths,
            IsEnabled = true
        };
        _context.CreatorPersonas.Add(persona);
        await _context.SaveChangesAsync();
        return persona;
    }

    [Test]
    public async Task RefreshCoverArt_RecordsTheGeneratedWidthsAndBumpsTheVersion()
    {
        await AddSongAsync();
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(CoverArtPath, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(128, 320, 640));

        var refreshed = await _coordinator.RefreshCoverArtVariantsAsync(1);

        using var verifyContext = new AppDbContext(_options);
        var song = await verifyContext.SongMetadata.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refreshed, Is.True);
            Assert.That(song.CoverArtVariantWidths, Is.EqualTo("128,320,640"));
            Assert.That(song.CoverArtVariantVersion, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RefreshCoverArt_BumpsTheVersionOnEveryRegeneration()
    {
        // GUID-scheme art keeps one fixed path that a re-crop overwrites in place, and the media
        // endpoint serves it with a year-long immutable cache header. Without a version bump the
        // recropped image would stay stale in browsers.
        await AddSongAsync(widths: "128,320,640", version: 4);
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(CoverArtPath, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(128, 320, 640));

        await _coordinator.RefreshCoverArtVariantsAsync(1);

        using var verifyContext = new AppDbContext(_options);
        Assert.That((await verifyContext.SongMetadata.SingleAsync()).CoverArtVariantVersion, Is.EqualTo(5));
    }

    [Test]
    public async Task RefreshCoverArt_WhenGenerationFails_LeavesTheRecordedWidthsUntouched()
    {
        // The song keeps serving its full-size master, exactly as it did before renditions existed.
        await AddSongAsync(widths: "128,320", version: 2);
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(CoverArtPath, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageVariantResult.Failed("decode_failed"));

        var refreshed = await _coordinator.RefreshCoverArtVariantsAsync(1);

        using var verifyContext = new AppDbContext(_options);
        var song = await verifyContext.SongMetadata.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refreshed, Is.False);
            Assert.That(song.CoverArtVariantWidths, Is.EqualTo("128,320"));
            Assert.That(song.CoverArtVariantVersion, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task RefreshCoverArt_WhenTheVariantServiceThrows_DoesNotPropagate()
    {
        // Renditions are derived data the backfill can rebuild. A SkiaSharp hiccup must never fail
        // an upload whose real blobs already committed.
        await AddSongAsync();
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("skia exploded"));

        Assert.That(await _coordinator.RefreshCoverArtVariantsAsync(1), Is.False);
    }

    [Test]
    public async Task RefreshCoverArt_ForAMissingSong_ReturnsFalseWithoutGenerating()
    {
        var refreshed = await _coordinator.RefreshCoverArtVariantsAsync(999);

        Assert.That(refreshed, Is.False);
        _variantService.Verify(
            s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task RefreshCoverArt_WhenArtWasRemoved_ClearsTheWidthsInsteadOfGenerating()
    {
        await AddSongAsync(imageBlobPath: null, widths: "128,320,640");

        var refreshed = await _coordinator.RefreshCoverArtVariantsAsync(1);

        using var verifyContext = new AppDbContext(_options);
        var song = await verifyContext.SongMetadata.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refreshed, Is.True);
            Assert.That(song.CoverArtVariantWidths, Is.Empty);
        });
        _variantService.Verify(
            s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task RefreshCoverArt_WhenLegacyArtMoved_DeletesTheRenditionsLeftAtTheOldPath()
    {
        const string previousPath = "Night Drive/Night Drive.png";
        await AddSongAsync(imageBlobPath: "Night Drive/Night Drive Remaster.png", widths: "128,320");
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(128, 320, 640));

        await _coordinator.RefreshCoverArtVariantsAsync(1, previousCoverArtBlobPath: previousPath);

        // Exactly the widths that were recorded for the old master — not the whole ladder, which
        // would fire a storage round trip per rung that never existed.
        _variantService.Verify(
            s => s.DeleteCoverArtVariantsAsync(
                previousPath,
                It.Is<IEnumerable<int>>(w => w.SequenceEqual(new[] { 128, 320 })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task RefreshCoverArt_WhenThePathIsUnchanged_DeletesNothing()
    {
        // The GUID scheme's whole point: one fixed path, overwritten in place.
        await AddSongAsync(widths: "128,320,640");
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(CoverArtPath, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(128, 320, 640));

        await _coordinator.RefreshCoverArtVariantsAsync(1, previousCoverArtBlobPath: CoverArtPath);

        _variantService.Verify(
            s => s.DeleteCoverArtVariantsAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task RefreshCoverArt_ForASourceSmallerThanEveryRung_RecordsTheOddWidth()
    {
        // The never-upscale fallback: one rendition at the source's own width. The width set has to
        // be able to express that, which is why it is a CSV rather than a bitmask over the ladder.
        await AddSongAsync();
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(CoverArtPath, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageVariantResult(true, new[] { 96 }, 96, 96, 3_000, null));

        await _coordinator.RefreshCoverArtVariantsAsync(1);

        using var verifyContext = new AppDbContext(_options);
        Assert.That((await verifyContext.SongMetadata.SingleAsync()).CoverArtVariantWidths, Is.EqualTo("96"));
    }

    [Test]
    public async Task RefreshCoverArt_NeverRunsAsADryRun()
    {
        await AddSongAsync();
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(CoverArtPath, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(128, 320));

        await _coordinator.RefreshCoverArtVariantsAsync(1);

        _variantService.Verify(
            s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task RefreshPersona_RecordsTheGeneratedWidthsAndBumpsTheVersion()
    {
        await AddPersonaAsync();
        _variantService
            .Setup(s => s.GeneratePersonaVariantsAsync(PersonaPath, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(128, 320, 640));

        var refreshed = await _coordinator.RefreshPersonaVariantsAsync(7);

        using var verifyContext = new AppDbContext(_options);
        var persona = await verifyContext.CreatorPersonas.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refreshed, Is.True);
            Assert.That(persona.ImageVariantWidths, Is.EqualTo("128,320,640"));
            Assert.That(persona.ImageVariantVersion, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RefreshPersona_UsesThePersonaContainerNotTheMediaContainer()
    {
        await AddPersonaAsync();
        _variantService
            .Setup(s => s.GeneratePersonaVariantsAsync(PersonaPath, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(128, 320));

        await _coordinator.RefreshPersonaVariantsAsync(7);

        _variantService.Verify(
            s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task RefreshPersona_ForAMissingPersona_ReturnsFalse()
        => Assert.That(await _coordinator.RefreshPersonaVariantsAsync(999), Is.False);

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
