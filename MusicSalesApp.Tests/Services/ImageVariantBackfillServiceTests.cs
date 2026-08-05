using Azure.Storage.Blobs;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class ImageVariantBackfillServiceTests
{
    private const string Guid32 = "3f2a9c1e5b7d4e6f8a901c2d3e4f5a6b";
    private static readonly string CoverArt = $"{Guid32}/{Guid32}-coverart.jpg";

    private Mock<IImageVariantService> _variantService = null!;
    private Mock<IBlobContainerFactory> _containerFactory = null!;
    private Mock<IAzureStorageService> _storage = null!;
    private Mock<IBackgroundJobClient> _jobs = null!;
    private DbContextOptions<AppDbContext> _options = null!;
    private AppDbContext _context = null!;
    private ManualTimeProvider _clock = null!;
    private ImageVariantBackfillService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _variantService = new Mock<IImageVariantService>();
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageVariantResult(true, new[] { 128, 320, 640 }, 800, 800, 90_000, null));
        _variantService
            .Setup(s => s.GeneratePersonaVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageVariantResult(true, new[] { 128, 320 }, 400, 400, 25_000, null));

        _containerFactory = new Mock<IBlobContainerFactory>();
        _containerFactory.Setup(f => f.GetConfiguredContainerNames())
            .Returns(new[] { "musiccontainer-local", "persona-images-local" });

        _storage = new Mock<IAzureStorageService>();
        _storage.Setup(s => s.DeleteAsync(It.IsAny<string>())).ReturnsAsync(true);

        _jobs = new Mock<IBackgroundJobClient>();
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ImageVariantBackfillTests_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(_options);
        _clock = new ManualTimeProvider();

        _service = BuildService();
    }

    /// <summary>
    /// Tests that assert on ordering pass <paramref name="maxParallelEncodes"/> = 1; with the default
    /// of 2 the items genuinely overlap and what any one of them observes is a race.
    /// </summary>
    private ImageVariantBackfillService BuildService(int? maxParallelEncodes = null)
    {
        var settings = new Dictionary<string, string>();
        if (maxParallelEncodes.HasValue)
        {
            settings["ImageVariants:MaxParallelEncodes"] = maxParallelEncodes.Value.ToString();
        }

        return new ImageVariantBackfillService(
            new TestDbContextFactory(_options),
            _variantService.Object,
            _containerFactory.Object,
            _storage.Object,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            _jobs.Object,
            Mock.Of<ILogger<ImageVariantBackfillService>>(),
            _clock);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private async Task<SongMetadata> AddSongAsync(int id, string widths = null, string imagePath = null)
    {
        var song = new SongMetadata
        {
            Id = id,
            SongTitle = $"Song {id}",
            Mp3BlobPath = $"{Guid32}/{Guid32}-music.mp3",
            ImageBlobPath = imagePath ?? CoverArt,
            CoverArtVariantWidths = widths,
            IsActive = true,
            IsEnabled = true
        };
        _context.SongMetadata.Add(song);
        await _context.SaveChangesAsync();
        return song;
    }

    private async Task AddPersonaAsync(int id, string widths = null)
    {
        _context.CreatorPersonas.Add(new CreatorPersona
        {
            Id = id,
            CreatorId = 1,
            Name = $"Persona {id}",
            ImageBlobPath = $"creator-1/persona-{id}.png",
            ImageVariantWidths = widths,
            IsEnabled = true
        });
        await _context.SaveChangesAsync();
    }

    private async Task<ImageVariantBackfillRun> RunAsync(
        ImageVariantBackfillScope scope = ImageVariantBackfillScope.All,
        bool dryRun = false,
        bool regenerateExisting = false,
        bool removeLegacyPng = false)
    {
        var run = await _service.StartAsync(scope, dryRun, regenerateExisting, removeLegacyPng, 1, "admin@test");
        await _service.RunAsync(run.Id);

        using var verify = new AppDbContext(_options);
        return await verify.ImageVariantBackfillRuns.FirstAsync(r => r.Id == run.Id);
    }

    [Test]
    public async Task ProcessesEverySongAndPersonaThatHasNoRenditions()
    {
        await AddSongAsync(1);
        await AddSongAsync(2);
        await AddPersonaAsync(7);

        var run = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(run.TotalItemCount, Is.EqualTo(3));
            Assert.That(run.GeneratedCount, Is.EqualTo(3));
            Assert.That(run.Status, Is.EqualTo(ImageVariantBackfillStatus.Completed));
        });
    }

    [Test]
    public async Task SkipsImagesThatAlreadyHaveRenditions()
    {
        // This filter is what makes the job idempotent and resumable: after a process recycle
        // Hangfire re-invokes it, and it picks up only what is still outstanding.
        await AddSongAsync(1, widths: "128,320,640");
        await AddSongAsync(2);

        var run = await RunAsync();

        Assert.That(run.TotalItemCount, Is.EqualTo(1));
        _variantService.Verify(
            s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task RegenerateExisting_ReprocessesImagesThatAlreadyHaveRenditions()
    {
        await AddSongAsync(1, widths: "128,320,640");
        await AddSongAsync(2, widths: "128");

        var run = await RunAsync(regenerateExisting: true);

        Assert.That(run.TotalItemCount, Is.EqualTo(2));
    }

    [Test]
    public async Task RecordsTheGeneratedWidthsAndBumpsTheVersion()
    {
        await AddSongAsync(1);

        await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        using var verify = new AppDbContext(_options);
        var song = await verify.SongMetadata.FirstAsync(s => s.Id == 1);

        Assert.Multiple(() =>
        {
            Assert.That(song.CoverArtVariantWidths, Is.EqualTo("128,320,640"));
            Assert.That(song.CoverArtVariantVersion, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DryRun_WritesNothingToTheDatabase()
    {
        await AddSongAsync(1);

        var run = await RunAsync(dryRun: true);

        using var verify = new AppDbContext(_options);
        var song = await verify.SongMetadata.FirstAsync(s => s.Id == 1);

        Assert.Multiple(() =>
        {
            Assert.That(song.CoverArtVariantWidths, Is.Null.Or.Empty);
            Assert.That(song.CoverArtVariantVersion, Is.Zero);
            // But it still reports what a real run would produce, which is the point of a rehearsal.
            Assert.That(run.VariantBlobCount, Is.GreaterThan(0));
            Assert.That(run.BytesWritten, Is.GreaterThan(0));
        });

        _variantService.Verify(
            s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Scope_LimitsWhatIsProcessed()
    {
        await AddSongAsync(1);
        await AddPersonaAsync(7);

        var coverArtOnly = await RunAsync(ImageVariantBackfillScope.CoverArtOnly);
        Assert.That(coverArtOnly.TotalItemCount, Is.EqualTo(1));

        _variantService.Verify(
            s => s.GeneratePersonaVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task IncludesDisabledAndInactiveSongs()
    {
        // Re-enabling a song should not require a second backfill, and disabled songs are still
        // shown in the admin grids.
        var song = await AddSongAsync(1);
        song.IsActive = false;
        song.IsEnabled = false;
        await _context.SaveChangesAsync();

        var run = await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        Assert.That(run.TotalItemCount, Is.EqualTo(1));
    }

    [Test]
    public async Task IgnoresSongsWithNoCoverArt()
    {
        await AddSongAsync(1, imagePath: "");

        var run = await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        Assert.That(run.TotalItemCount, Is.Zero);
    }

    [Test]
    public async Task LegacyNamedSongsNeedNoSpecialHandling()
    {
        // The rendition path derives from the stored blob path, and ImageVariantPaths has no
        // naming-scheme branch, so pre-GUID songs flow through unchanged.
        await AddSongAsync(1, imagePath: "Night Drive/Night Drive.png");

        var run = await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        Assert.That(run.GeneratedCount, Is.EqualTo(1));
        _variantService.Verify(
            s => s.GenerateCoverArtVariantsAsync("Night Drive/Night Drive.png", false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task RecordsAFailureRowAndCompletesWithErrors()
    {
        await AddSongAsync(1);
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageVariantResult.Failed(ImageVariantFailureCodes.DecodeFailed));

        var run = await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        using var verify = new AppDbContext(_options);
        var failures = await verify.ImageVariantBackfillItemFailures.ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo(ImageVariantBackfillStatus.CompletedWithErrors));
            Assert.That(run.FailedCount, Is.EqualTo(1));
            Assert.That(failures, Has.Count.EqualTo(1));
            Assert.That(failures[0].FailureCode, Is.EqualTo(ImageVariantFailureCodes.DecodeFailed));
        });
    }

    [Test]
    public async Task CountsUndersizedSourcesSeparately()
    {
        await AddSongAsync(1);
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageVariantResult(true, new[] { 96 }, 96, 96, 3_000, null));

        var run = await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        Assert.That(run.UndersizedSourceCount, Is.EqualTo(1));
    }

    [Test]
    public async Task RemoveLegacyPngSharingImages_DeletesTheSupersededShareImage()
    {
        await AddSongAsync(1);

        var run = await RunAsync(ImageVariantBackfillScope.CoverArtOnly, removeLegacyPng: true);

        _storage.Verify(s => s.DeleteAsync($"{Guid32}/{Guid32}-fb.png"), Times.Once);
        // Never the current JPEG - that one is still in use.
        _storage.Verify(s => s.DeleteAsync($"{Guid32}/{Guid32}-fb.jpg"), Times.Never);
        Assert.That(run.LegacySharingImagesRemoved, Is.EqualTo(1));
    }

    [Test]
    public async Task WithoutTheOption_LeavesTheLegacyShareImageAlone()
    {
        await AddSongAsync(1);

        await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        _storage.Verify(s => s.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ReleasesTheSingletonLockWhenTheRunFinishes()
    {
        await AddSongAsync(1);

        var run = await RunAsync();

        Assert.That(run.ActiveLockKey, Is.Null,
            "a run that does not release the lock blocks the feature until someone edits the table by hand");
    }

    [Test]
    public async Task RefusesToStartASecondRunWhileOneIsActive()
    {
        await AddSongAsync(1);
        await _service.StartAsync(ImageVariantBackfillScope.All, false, false, false, 1, "admin@test");

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.StartAsync(ImageVariantBackfillScope.All, false, false, false, 1, "admin@test"));
    }

    [Test]
    public async Task WhenEnqueueFails_TheLockIsReleasedRatherThanBlockingTheFeatureForever()
    {
        await AddSongAsync(1);
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Throws(new InvalidOperationException("hangfire storage unavailable"));

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.StartAsync(ImageVariantBackfillScope.All, false, false, false, 1, "admin@test"));

        using var verify = new AppDbContext(_options);
        var run = await verify.ImageVariantBackfillRuns.FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo(ImageVariantBackfillStatus.Failed));
            Assert.That(run.ActiveLockKey, Is.Null);
        });
    }

    [Test]
    public async Task CancellationStopsTheRun()
    {
        await AddSongAsync(1);
        await AddSongAsync(2);

        var run = await _service.StartAsync(ImageVariantBackfillScope.All, false, false, false, 1, "admin@test");
        await _service.RequestCancellationAsync(run.Id);
        await _service.RunAsync(run.Id);

        using var verify = new AppDbContext(_options);
        var finished = await verify.ImageVariantBackfillRuns.FirstAsync(r => r.Id == run.Id);

        Assert.Multiple(() =>
        {
            Assert.That(finished.Status, Is.EqualTo(ImageVariantBackfillStatus.Cancelled));
            Assert.That(finished.ProcessedCount, Is.Zero);
            Assert.That(finished.ActiveLockKey, Is.Null);
        });
    }

    [Test]
    public async Task ProgressIsVisibleWhileTheRunIsStillGoing()
    {
        // The admin page polls this row, so progress that is only written every 25 items leaves the
        // bar at zero for the whole of any run shorter than that - which is every run on a small
        // catalogue, and the first 25 images of a large one.
        _service = BuildService(maxParallelEncodes: 1);
        await AddSongAsync(1);
        await AddSongAsync(2);
        await AddSongAsync(3);

        var observed = new List<int>();
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                using var reader = new AppDbContext(_options);
                observed.Add(reader.ImageVariantBackfillRuns.Single().ProcessedCount);

                // Stand in for the real cost of decoding and re-encoding one image.
                _clock.Advance(TimeSpan.FromSeconds(2));
            })
            .ReturnsAsync(new ImageVariantResult(true, new[] { 128, 320, 640 }, 800, 800, 90_000, null));

        await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        Assert.That(observed, Is.EqualTo(new[] { 0, 1, 2 }),
            "each image should see the previous one's progress already recorded");
    }

    [Test]
    public async Task AFastRunStillReportsProgressWithoutWaitingOnTheClock()
    {
        // The time trigger must not become the only rule: with the clock frozen the count trigger is
        // what keeps a long, fast run reporting.
        _service = BuildService(maxParallelEncodes: 1);
        for (var id = 1; id <= 26; id++)
        {
            await AddSongAsync(id, imagePath: $"{Guid32}/{Guid32}-coverart-{id}.jpg");
        }

        var seenBeforeTheEnd = new List<int>();
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                using var reader = new AppDbContext(_options);
                seenBeforeTheEnd.Add(reader.ImageVariantBackfillRuns.Single().ProcessedCount);
            })
            .ReturnsAsync(new ImageVariantResult(true, new[] { 128 }, 200, 200, 5_000, null));

        await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        Assert.That(seenBeforeTheEnd.Max(), Is.EqualTo(25),
            "the 26th image should have seen the flush that the 25th triggered");
    }

    [Test]
    public async Task EncodesRunConcurrentlyUpToTheConfiguredLimit()
    {
        // MaxParallelEncodes is documented on the admin page as the knob that trades run time against
        // CPU contention with request serving, so it has to actually do something.
        _service = BuildService(maxParallelEncodes: 3);
        for (var id = 1; id <= 6; id++)
        {
            await AddSongAsync(id, imagePath: $"{Guid32}/{Guid32}-coverart-{id}.jpg");
        }

        var active = 0;
        var peak = 0;
        var release = new TaskCompletionSource();

        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedMax(ref peak, now);

                // Hold every worker until enough of them have arrived to prove they overlap.
                if (now >= 3) release.TrySetResult();
                await release.Task;

                Interlocked.Decrement(ref active);
                return new ImageVariantResult(true, new[] { 128 }, 200, 200, 5_000, null);
            });

        await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        Assert.That(peak, Is.EqualTo(3), "three encodes should have been in flight at once");
    }

    [Test]
    public async Task NeverExceedsTheConfiguredEncodeLimit()
    {
        // The ceiling is what keeps peak memory bounded: each worker holds a fully decoded bitmap.
        _service = BuildService(maxParallelEncodes: 2);
        for (var id = 1; id <= 8; id++)
        {
            await AddSongAsync(id, imagePath: $"{Guid32}/{Guid32}-coverart-{id}.jpg");
        }

        var active = 0;
        var peak = 0;

        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                InterlockedMax(ref peak, Interlocked.Increment(ref active));
                await Task.Yield();
                Interlocked.Decrement(ref active);
                return new ImageVariantResult(true, new[] { 128 }, 200, 200, 5_000, null);
            });

        await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        Assert.That(peak, Is.LessThanOrEqualTo(2));
    }

    [Test]
    public async Task LegacyPngSweepStillRunsForSongsThatAlreadyHaveRenditions()
    {
        // The sweep used to be nested inside the per-item work, which the "generate missing" filter
        // excludes as soon as renditions exist - so ticking the box on any later run silently
        // reclaimed nothing, and the only way to sweep stragglers was a full REGENERATE.
        await AddSongAsync(1, widths: "128,320,640");

        var run = await RunAsync(ImageVariantBackfillScope.CoverArtOnly, removeLegacyPng: true);

        Assert.Multiple(() =>
        {
            Assert.That(run.TotalItemCount, Is.Zero, "no renditions were missing");
            Assert.That(run.LegacySharingImagesRemoved, Is.EqualTo(1));
        });
        _storage.Verify(s => s.DeleteAsync($"{Guid32}/{Guid32}-fb.png"), Times.Once);
    }

    [Test]
    public async Task ADryRunSweepsNothing()
    {
        await AddSongAsync(1);

        var run = await RunAsync(ImageVariantBackfillScope.CoverArtOnly, dryRun: true, removeLegacyPng: true);

        Assert.That(run.LegacySharingImagesRemoved, Is.Zero);
        _storage.Verify(s => s.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task PersonaOnlyScopeSweepsNoSharingImages()
    {
        await AddSongAsync(1);
        await AddPersonaAsync(7);

        var run = await RunAsync(ImageVariantBackfillScope.PersonaOnly, removeLegacyPng: true);

        Assert.That(run.LegacySharingImagesRemoved, Is.Zero);
        _storage.Verify(s => s.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task AFailedRunIsNotSilentlyRestartedByAHangfireRetry()
    {
        // RunAsync rethrows so the Hangfire dashboard shows a real failure, and [AutomaticRetry]
        // then calls it again - but the lock has been released and the admin has been shown a
        // failure message. Restarting here would resurrect a run they were told was dead.
        await AddSongAsync(1);
        var run = await _service.StartAsync(ImageVariantBackfillScope.CoverArtOnly, false, false, false, 1, "admin@test");

        await using (var seed = new AppDbContext(_options))
        {
            var row = await seed.ImageVariantBackfillRuns.FirstAsync(r => r.Id == run.Id);
            row.Status = ImageVariantBackfillStatus.Failed;
            row.ActiveLockKey = null;
            await seed.SaveChangesAsync();
        }

        await _service.RunAsync(run.Id);

        _variantService.Verify(
            s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task AnEntityDeletedMidRunIsSkippedRatherThanCountedAsAFailure()
    {
        await AddSongAsync(1);
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await using var deleter = new AppDbContext(_options);
                deleter.SongMetadata.Remove(await deleter.SongMetadata.FirstAsync(s => s.Id == 1));
                await deleter.SaveChangesAsync();
                return new ImageVariantResult(true, new[] { 128 }, 200, 200, 5_000, null);
            });

        var run = await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        Assert.Multiple(() =>
        {
            Assert.That(run.SkippedCount, Is.EqualTo(1));
            Assert.That(run.FailedCount, Is.Zero);
            Assert.That(run.Status, Is.EqualTo(ImageVariantBackfillStatus.Completed));
        });
    }

    [Test]
    public async Task AnUnexpectedThrowIsNotFiledAsAnUploadFailure()
    {
        await AddSongAsync(1);
        _variantService
            .Setup(s => s.GenerateCoverArtVariantsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("skia is unhappy"));

        await RunAsync(ImageVariantBackfillScope.CoverArtOnly);

        using var verify = new AppDbContext(_options);
        var failure = await verify.ImageVariantBackfillItemFailures.SingleAsync();

        Assert.That(failure.FailureCode, Is.EqualTo(ImageVariantFailureCodes.Unexpected),
            "the fault came from generation, not from writing a blob");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
        }
    }

    [Test]
    public void TargetContainersAreTheEnvironmentsOwn()
    {
        // Environment scoping is automatic: the containers come from this process's configuration,
        // which is why the job is run once per environment rather than once globally.
        Assert.That(
            _service.GetTargetContainerNames(),
            Is.EqualTo(new[] { "musiccontainer-local", "persona-images-local" }));
    }

    [Test]
    public void TheContainerFactoryOffersNoWayToReachABackupContainer()
    {
        // Decision enforced by the type system rather than by discipline: every accessor on
        // IBlobContainerFactory names one specific container, with no lookup by name, so no amount
        // of edits to the backfill can point it at backup-*. If a by-name overload is ever added,
        // this test fails and the reviewer has to think about it.
        //
        // The count is a deliberate speed bump too. Adding an accessor is fine - GetUploadStagingContainer
        // was added when audio processing moved to Azure Functions - but it should be a decision,
        // not something that slides in.
        var methods = typeof(IBlobContainerFactory).GetMethods()
            .Where(m => m.ReturnType == typeof(BlobContainerClient)
                || m.ReturnType == typeof(BlobContainerClient).MakeByRefType()
                || Nullable.GetUnderlyingType(m.ReturnType) == typeof(BlobContainerClient)
                || m.ReturnType.FullName == typeof(BlobContainerClient).FullName)
            .ToList();

        Assert.That(
            methods.Select(m => m.Name),
            Is.EquivalentTo(new[]
            {
                nameof(IBlobContainerFactory.GetMediaContainer),
                nameof(IBlobContainerFactory.GetPersonaImageContainer),
                nameof(IBlobContainerFactory.GetUploadStagingContainer)
            }));
        Assert.That(
            methods.All(m => m.GetParameters().Length == 0),
            Is.True,
            "a container accessor that takes a name would let the backfill address backup-* containers");
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
