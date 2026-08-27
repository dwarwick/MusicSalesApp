using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class StorageBackupServiceTests
{
    private const string MusicContainer = "musiccontainer";
    private const string PersonaContainer = "persona-images";
    private const string KeysContainer = "dataprotection-keys";
    private const string StreamingContainer = "musicstreaming";

    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private Mock<IStorageBackupBlobGateway> _gateway = null!;
    private Mock<IBackgroundJobClient> _jobs = null!;
    private AzureStorageOptions _storageOptions = null!;
    private ManualTimeProvider _clock = null!;
    private StorageBackupService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"storage-backup-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);

        _gateway = new Mock<IStorageBackupBlobGateway>();
        _gateway.SetupGet(gateway => gateway.IsConfigured).Returns(true);
        _gateway.Setup(gateway => gateway.ContainerExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _gateway.Setup(gateway => gateway.EnsureContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _gateway.Setup(gateway => gateway.CreateContainerReadSasQuery(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns("sig=test");
        _gateway.Setup(gateway => gateway.CopyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(), It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default every container to empty; individual tests override specific containers, and a
        // later Moq setup wins over this one.
        _gateway.Setup(gateway => gateway.ListAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(() => ToAsyncEnumerable(Array.Empty<StorageBlobSnapshot>()));

        _jobs = new Mock<IBackgroundJobClient>();
        _jobs.Setup(client => client.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");

        _storageOptions = new AzureStorageOptions
        {
            StorageAccountConnectionString = "UseDevelopmentStorage=true",
            ContainerName = MusicContainer,
            PersonaImageContainerName = PersonaContainer,
            StreamingContainerName = StreamingContainer
        };

        _clock = new ManualTimeProvider();
        _service = BuildService();
    }

    private StorageBackupService BuildService()
        => new(
            _factory,
            _gateway.Object,
            Options.Create(_storageOptions),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build(),
            _jobs.Object,
            Mock.Of<ILogger<StorageBackupService>>(),
            _clock);

    // ---------------- Queueing ----------------

    [Test]
    public async Task StartBackup_CreatesRunWithOneRowPerConfiguredContainer()
    {
        var run = await _service.StartBackupAsync(7, "admin@example.com", forceFullCopy: false);

        await using var context = new AppDbContext(_options);
        var stored = await context.StorageBackupRuns.Include(item => item.Containers).SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(run.Id, Is.EqualTo(stored.Id));
            Assert.That(stored.Direction, Is.EqualTo(StorageBackupDirection.Backup));
            Assert.That(stored.Status, Is.EqualTo(StorageBackupRunStatus.Queued));
            Assert.That(stored.ActiveLockKey, Is.EqualTo(1));
            Assert.That(stored.HangfireJobId, Is.EqualTo("job-1"));
            Assert.That(stored.TriggerSource, Is.EqualTo(StorageBackupTriggerSources.Admin));
            Assert.That(stored.Containers, Has.Count.EqualTo(3));
            Assert.That(
                stored.Containers.Select(container => container.DestinationContainerName),
                Is.EquivalentTo(new[] { "backup-musiccontainer", "backup-persona-images", "backup-musicstreaming" }));
        });
    }

    [Test]
    public async Task StartBackup_WhenAnotherRunIsActive_IsRejected()
    {
        await _service.StartBackupAsync(1, "admin@example.com", false);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StartBackupAsync(1, "admin@example.com", false));
    }

    [Test]
    public void StartBackup_WhenConfiguredContainerIsItselfABackupContainer_IsRejected()
    {
        _storageOptions.ContainerName = "backup-musiccontainer";
        _service = BuildService();

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StartBackupAsync(1, "admin@example.com", false));
    }

    [Test]
    public void StartBackup_WhenStorageIsNotConfigured_IsRejected()
    {
        _gateway.SetupGet(gateway => gateway.IsConfigured).Returns(false);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StartBackupAsync(1, "admin@example.com", false));
    }

    [Test]
    public async Task StartBackup_WhenHangfireEnqueueFails_MarksRunFailedAndReleasesLock()
    {
        _jobs.Setup(client => client.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Throws(new InvalidOperationException("hangfire is down"));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StartBackupAsync(1, "admin@example.com", false));

        await using var context = new AppDbContext(_options);
        var run = await context.StorageBackupRuns.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo(StorageBackupRunStatus.Failed));
            Assert.That(run.ActiveLockKey, Is.Null);
            Assert.That(run.FailureMessage, Is.Not.Null.And.Length.LessThanOrEqualTo(2000));
        });
    }

    [Test]
    public void StartRestore_WithNoContainersSelected_IsRejected()
        => Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StartRestoreAsync(
                1, "admin@example.com", Array.Empty<string>(),
                StorageRestoreScope.MissingAndDiffering, true));

    [Test]
    public void StartRestore_WithAnUnconfiguredContainer_IsRejected()
        => Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StartRestoreAsync(
                1, "admin@example.com", new[] { "some-other-container" },
                StorageRestoreScope.MissingAndDiffering, true));

    [Test]
    public void StartRestore_WhenBackupContainerIsEmpty_IsRejected()
    {
        SetupContainer("backup-musiccontainer");

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StartRestoreAsync(
                1, "admin@example.com", new[] { MusicContainer },
                StorageRestoreScope.MissingAndDiffering, true));
    }

    [Test]
    public void StartRestore_WhenBackupContainerDoesNotExist_IsRejected()
    {
        _gateway.Setup(gateway => gateway.ContainerExistsAsync("backup-musiccontainer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StartRestoreAsync(
                1, "admin@example.com", new[] { MusicContainer },
                StorageRestoreScope.MissingAndDiffering, true));
    }

    [Test]
    public async Task StartRestore_QueuesOnlyTheSelectedContainers()
    {
        SetupContainer("backup-musiccontainer", Blob("song/song.mp3"));

        await _service.StartRestoreAsync(
            1, "admin@example.com", new[] { MusicContainer },
            StorageRestoreScope.MissingOnly, overwriteNewerLive: false);

        await using var context = new AppDbContext(_options);
        var run = await context.StorageBackupRuns.Include(item => item.Containers).SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(run.Direction, Is.EqualTo(StorageBackupDirection.Restore));
            Assert.That(run.RestoreScope, Is.EqualTo(StorageRestoreScope.MissingOnly));
            Assert.That(run.OverwriteNewerLive, Is.False);
            Assert.That(run.Containers.Single().SourceContainerName, Is.EqualTo(MusicContainer));
        });
    }

    [Test]
    public async Task RunRecurringBackup_WhenARunIsAlreadyActive_DoesNothing()
    {
        await _service.StartBackupAsync(1, "admin@example.com", false);

        await _service.RunRecurringBackupAsync();

        await using var context = new AppDbContext(_options);
        Assert.That(await context.StorageBackupRuns.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task RunRecurringBackup_CreatesARunTaggedAsRecurring()
    {
        await _service.RunRecurringBackupAsync();

        await using var context = new AppDbContext(_options);
        var run = await context.StorageBackupRuns.SingleAsync();
        Assert.That(run.TriggerSource, Is.EqualTo(StorageBackupTriggerSources.Recurring));
    }

    // ---------------- Execution ----------------

    [Test]
    public async Task Run_CopiesEveryBlobOnAFirstBackup()
    {
        SetupContainer(MusicContainer, Blob("a.mp3", length: 100), Blob("b.mp3", length: 200));
        var run = await _service.StartBackupAsync(1, "admin@example.com", false);

        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var stored = await context.StorageBackupRuns.Include(item => item.Containers)
            .SingleAsync(item => item.Id == run.Id);
        var music = stored.Containers.Single(container => container.SourceContainerName == MusicContainer);

        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(StorageBackupRunStatus.Completed));
            Assert.That(stored.ActiveLockKey, Is.Null);
            Assert.That(stored.CompletedAt, Is.Not.Null);
            Assert.That(music.Status, Is.EqualTo(StorageBackupContainerStatus.Completed));
            Assert.That(music.TotalBlobCount, Is.EqualTo(2));
            Assert.That(music.CopiedCount, Is.EqualTo(2));
            Assert.That(music.SkippedCount, Is.Zero);
            Assert.That(music.CopiedBytes, Is.EqualTo(300));
        });

        _gateway.Verify(gateway => gateway.CopyAsync(
            MusicContainer, "backup-musiccontainer", It.IsAny<string>(), "sig=test",
            It.IsAny<IDictionary<string, string>>(), It.IsAny<IDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task Run_SkipsBlobsAlreadyPresentInTheBackupWithAMatchingETag()
    {
        var source = Blob("a.mp3", etag: "0xAAA", length: 100);
        SetupContainer(MusicContainer, source);
        SetupContainer("backup-musiccontainer", BackupOf(source));

        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var music = await context.StorageBackupContainerProgresses
            .SingleAsync(container => container.RunId == run.Id && container.SourceContainerName == MusicContainer);

        Assert.Multiple(() =>
        {
            Assert.That(music.SkippedCount, Is.EqualTo(1));
            Assert.That(music.CopiedCount, Is.Zero);
        });

        _gateway.Verify(gateway => gateway.CopyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(), It.IsAny<IDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Run_RecopiesABlobWhoseSourceETagChanged()
    {
        var original = Blob("cover.jpg", etag: "0xOLD", length: 100);
        var cropped = Blob("cover.jpg", etag: "0xNEW", length: 90);
        SetupContainer(MusicContainer, cropped);
        SetupContainer("backup-musiccontainer", BackupOf(original));

        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var music = await context.StorageBackupContainerProgresses
            .SingleAsync(container => container.RunId == run.Id && container.SourceContainerName == MusicContainer);

        Assert.That(music.CopiedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Run_ForceFullCopy_RecopiesEvenUnchangedBlobs()
    {
        var source = Blob("a.mp3", etag: "0xAAA");
        SetupContainer(MusicContainer, source);
        SetupContainer("backup-musiccontainer", BackupOf(source));

        var run = await _service.StartBackupAsync(1, "admin@example.com", forceFullCopy: true);
        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var music = await context.StorageBackupContainerProgresses
            .SingleAsync(container => container.RunId == run.Id && container.SourceContainerName == MusicContainer);

        Assert.That(music.CopiedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Run_CarriesBlobIndexTagsAcrossToTheBackup()
    {
        var tags = new Dictionary<string, string> { ["AlbumName"] = "Greatest Hits" };
        SetupContainer(MusicContainer, Blob("cover.jpg", tags: tags));

        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RunAsync(run.Id);

        _gateway.Verify(gateway => gateway.CopyAsync(
            MusicContainer, "backup-musiccontainer", "cover.jpg", It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(),
            It.Is<IDictionary<string, string>>(actual => actual["AlbumName"] == "Greatest Hits"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Run_StampsSourceProvenanceIntoTheBackupBlobMetadata()
    {
        SetupContainer(MusicContainer, Blob("a.mp3", etag: "0xAAA", length: 512));

        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RunAsync(run.Id);

        _gateway.Verify(gateway => gateway.CopyAsync(
            MusicContainer, "backup-musiccontainer", "a.mp3", It.IsAny<string>(),
            It.Is<IDictionary<string, string>>(metadata =>
                metadata[StorageBackupMetadataKeys.SourceETag] == "0xAAA"
                && metadata[StorageBackupMetadataKeys.SourceLength] == "512"
                && metadata[StorageBackupMetadataKeys.SourceContainer] == MusicContainer),
            It.IsAny<IDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Run_WhenACopyFails_RecordsTheFailureAndKeepsGoing()
    {
        SetupContainer(MusicContainer, Blob("good.mp3"), Blob("bad.mp3"));
        _gateway.Setup(gateway => gateway.CopyAsync(
                It.IsAny<string>(), It.IsAny<string>(), "bad.mp3", It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(), It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("blob is leased"));

        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var stored = await context.StorageBackupRuns.Include(item => item.Containers)
            .SingleAsync(item => item.Id == run.Id);
        var failures = await context.StorageBackupItemFailures.Where(item => item.RunId == run.Id).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(StorageBackupRunStatus.CompletedWithErrors));
            Assert.That(stored.ActiveLockKey, Is.Null);
            Assert.That(stored.CopiedCount, Is.EqualTo(1));
            Assert.That(stored.FailedCount, Is.EqualTo(1));
            Assert.That(failures.Single().BlobName, Is.EqualTo("bad.mp3"));
            Assert.That(failures.Single().FailureCode, Is.EqualTo(StorageBackupFailureCodes.CopyFailed));
        });
    }

    [Test]
    public async Task Run_WhenTheSourceContainerDoesNotExist_SkipsItWithoutFailingTheRun()
    {
        _gateway.Setup(gateway => gateway.ContainerExistsAsync(MusicContainer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var stored = await context.StorageBackupRuns.Include(item => item.Containers)
            .SingleAsync(item => item.Id == run.Id);
        var music = stored.Containers.Single(container => container.SourceContainerName == MusicContainer);

        Assert.Multiple(() =>
        {
            Assert.That(music.Status, Is.EqualTo(StorageBackupContainerStatus.Skipped));
            Assert.That(music.FailureMessage, Does.Contain("does not exist"));
            Assert.That(stored.Status, Is.EqualTo(StorageBackupRunStatus.Completed));
        });
    }

    [Test]
    public async Task Run_WhenListingFails_MarksTheContainerFailedAndCompletesWithErrors()
    {
        _gateway.Setup(gateway => gateway.ListAsync(
                "backup-musiccontainer", It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("storage is unreachable"));

        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var stored = await context.StorageBackupRuns.Include(item => item.Containers)
            .SingleAsync(item => item.Id == run.Id);
        var music = stored.Containers.Single(container => container.SourceContainerName == MusicContainer);
        var failure = await context.StorageBackupItemFailures.SingleAsync(item => item.RunId == run.Id);

        Assert.Multiple(() =>
        {
            Assert.That(music.Status, Is.EqualTo(StorageBackupContainerStatus.Failed));
            Assert.That(failure.FailureCode, Is.EqualTo(StorageBackupFailureCodes.ListFailed));
            Assert.That(stored.Status, Is.EqualTo(StorageBackupRunStatus.CompletedWithErrors));
            Assert.That(stored.ActiveLockKey, Is.Null);
        });
    }

    [Test]
    public async Task Run_WhenCancellationWasRequested_StopsAndReleasesTheLock()
    {
        SetupContainer(MusicContainer, Blob("a.mp3"));
        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RequestCancellationAsync(run.Id);

        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var stored = await context.StorageBackupRuns.SingleAsync(item => item.Id == run.Id);

        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(StorageBackupRunStatus.Cancelled));
            Assert.That(stored.ActiveLockKey, Is.Null);
        });

        _gateway.Verify(gateway => gateway.CopyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(), It.IsAny<IDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Simulates an IIS app-pool recycle: Hangfire re-invokes RunAsync against a row already
    /// marked Running. The run must resume in place and skip what was already copied.
    /// </summary>
    [Test]
    public async Task Run_ReEnteredAfterAProcessRecycle_ResumesWithoutDuplicatingRowsOrCopies()
    {
        var source = Blob("a.mp3", etag: "0xAAA");
        SetupContainer(MusicContainer, source);
        var run = await _service.StartBackupAsync(1, "admin@example.com", false);

        await using (var seed = new AppDbContext(_options))
        {
            var stored = await seed.StorageBackupRuns.Include(item => item.Containers)
                .SingleAsync(item => item.Id == run.Id);
            stored.Status = StorageBackupRunStatus.Running;
            stored.StartedAt = DateTime.UtcNow.AddMinutes(-10);
            foreach (var container in stored.Containers)
            {
                container.Status = StorageBackupContainerStatus.Copying;
            }
            await seed.SaveChangesAsync();
        }

        // The first pass already mirrored this blob, so the re-run must see it as unchanged.
        SetupContainer("backup-musiccontainer", BackupOf(source));

        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var finished = await context.StorageBackupRuns.Include(item => item.Containers)
            .SingleAsync(item => item.Id == run.Id);

        Assert.Multiple(() =>
        {
            Assert.That(context.StorageBackupRuns.Count(), Is.EqualTo(1));
            Assert.That(finished.Containers, Has.Count.EqualTo(3));
            Assert.That(finished.Status, Is.EqualTo(StorageBackupRunStatus.Completed));
            Assert.That(finished.CopiedCount, Is.Zero);
            Assert.That(finished.SkippedCount, Is.EqualTo(1));
        });

        _gateway.Verify(gateway => gateway.CopyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(), It.IsAny<IDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Run_AnAlreadyCompletedRunIsNotReprocessed()
    {
        SetupContainer(MusicContainer, Blob("a.mp3"));
        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RunAsync(run.Id);
        _gateway.Invocations.Clear();

        await _service.RunAsync(run.Id);

        _gateway.Verify(gateway => gateway.CopyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(), It.IsAny<IDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Restore_CopiesBackTheBlobsMissingFromTheLiveContainer()
    {
        var backedUp = BackupOf(Blob("a.mp3", etag: "0xAAA"));
        SetupContainer("backup-musiccontainer", backedUp);
        SetupContainer(MusicContainer);

        var run = await _service.StartRestoreAsync(
            1, "admin@example.com", new[] { MusicContainer },
            StorageRestoreScope.MissingOnly, overwriteNewerLive: false);
        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var stored = await context.StorageBackupRuns.Include(item => item.Containers)
            .SingleAsync(item => item.Id == run.Id);

        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(StorageBackupRunStatus.Completed));
            Assert.That(stored.CopiedCount, Is.EqualTo(1));
        });

        _gateway.Verify(gateway => gateway.CopyAsync(
            "backup-musiccontainer", MusicContainer, "a.mp3", It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(), It.IsAny<IDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Restore_MissingOnly_LeavesAnExistingButDifferentLiveBlobAlone()
    {
        SetupContainer("backup-musiccontainer", BackupOf(Blob("a.mp3", etag: "0xAAA")));
        SetupContainer(MusicContainer, Blob("a.mp3", etag: "0xCHANGED"));

        var run = await _service.StartRestoreAsync(
            1, "admin@example.com", new[] { MusicContainer },
            StorageRestoreScope.MissingOnly, overwriteNewerLive: false);
        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var stored = await context.StorageBackupRuns.SingleAsync(item => item.Id == run.Id);

        Assert.Multiple(() =>
        {
            Assert.That(stored.CopiedCount, Is.Zero);
            Assert.That(stored.SkippedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Restore_StripsBackupProvenanceFromTheMetadataWrittenToTheLiveBlob()
    {
        SetupContainer("backup-musiccontainer", BackupOf(Blob("a.mp3", etag: "0xAAA")));
        SetupContainer(MusicContainer);

        var run = await _service.StartRestoreAsync(
            1, "admin@example.com", new[] { MusicContainer },
            StorageRestoreScope.MissingOnly, overwriteNewerLive: false);
        await _service.RunAsync(run.Id);

        _gateway.Verify(gateway => gateway.CopyAsync(
            "backup-musiccontainer", MusicContainer, "a.mp3", It.IsAny<string>(),
            It.Is<IDictionary<string, string>>(metadata =>
                !metadata.ContainsKey(StorageBackupMetadataKeys.SourceETag)
                && metadata.ContainsKey(StorageBackupMetadataKeys.RestoredFromETag)),
            It.IsAny<IDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------------- Queries ----------------

    [Test]
    public async Task GetActiveRun_ReturnsTheQueuedRunWithItsContainers()
    {
        var run = await _service.StartBackupAsync(1, "admin@example.com", false);

        var active = await _service.GetActiveRunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(active, Is.Not.Null);
            Assert.That(active!.Id, Is.EqualTo(run.Id));
            Assert.That(active.Containers, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public async Task GetActiveRun_ReturnsNullOnceTheRunFinishes()
    {
        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RunAsync(run.Id);

        Assert.That(await _service.GetActiveRunAsync(), Is.Null);
    }

    [Test]
    public void GetConfiguredContainerNames_ReturnsTheMediaPersonaAndStreamingContainers()
        => Assert.That(
            _service.GetConfiguredContainerNames(),
            Is.EqualTo(new[] { MusicContainer, PersonaContainer, StreamingContainer }));

    /// <summary>
    /// The Data Protection key ring is excluded on purpose: it regenerates automatically if lost,
    /// and nothing in this app is persisted encrypted with it. See GetConfiguredContainerNames.
    /// </summary>
    [Test]
    public async Task Backup_NeverTouchesTheDataProtectionKeyRingContainer()
    {
        SetupContainer(KeysContainer, Blob("keys.xml"));

        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RunAsync(run.Id);

        await using var context = new AppDbContext(_options);
        var containers = await context.StorageBackupContainerProgresses
            .Where(container => container.RunId == run.Id)
            .Select(container => container.SourceContainerName)
            .ToListAsync();

        Assert.That(containers, Does.Not.Contain(KeysContainer));

        _gateway.Verify(gateway => gateway.CopyAsync(
            KeysContainer, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(), It.IsAny<IDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void Restore_RejectsTheDataProtectionKeyRingContainer()
        => Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.StartRestoreAsync(
                1, "admin@example.com", new[] { KeysContainer },
                StorageRestoreScope.MissingAndDiffering, true));

    // ---------------- Progress reporting ----------------

    [Test]
    public async Task Run_ReportsProgressWhileACopyIsStillGoing()
    {
        // The admin page reads these counters, so a container holding fewer blobs than the flush
        // interval - or one whose copies are slow - would otherwise sit at zero until it finished.
        SetupContainer(MusicContainer, Blob("a.mp3"), Blob("b.mp3"), Blob("c.mp3"));

        var observed = new List<int>();
        _gateway.Setup(gateway => gateway.CopyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(), It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await using var reader = new AppDbContext(_options);
                observed.Add(reader.StorageBackupContainerProgresses
                    .Single(container => container.SourceContainerName == MusicContainer)
                    .ProcessedCount);

                // Stand in for the real duration of a server-side copy.
                _clock.Advance(TimeSpan.FromSeconds(2));
            });

        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RunAsync(run.Id);

        Assert.That(observed, Is.EqualTo(new[] { 0, 1, 2 }),
            "each copy should see the previous one's progress already recorded");
    }

    // ---------------- Helpers ----------------

    private void SetupContainer(string containerName, params StorageBlobSnapshot[] blobs)
        => _gateway.Setup(gateway => gateway.ListAsync(
                containerName, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(() => ToAsyncEnumerable(blobs));

    private static StorageBlobSnapshot Blob(
        string name,
        string etag = "0xETAG",
        long length = 128,
        IReadOnlyDictionary<string, string> tags = null)
        => new(
            name,
            etag,
            length,
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            null,
            StorageBlobCopyStatuses.Success,
            new Dictionary<string, string>(),
            tags);

    /// <summary>Builds the backup-side snapshot that a prior run would have produced for a source blob.</summary>
    private static StorageBlobSnapshot BackupOf(StorageBlobSnapshot source)
        => source with
        {
            ETag = "0xBACKUP",
            Metadata = StorageBackupDiff.BuildBackupMetadata(source, MusicContainer, 1, DateTime.UtcNow)
        };

#pragma warning disable CS1998 // Test data is already materialised; no awaits are needed.
    private static async IAsyncEnumerable<StorageBlobSnapshot> ToAsyncEnumerable(
        IReadOnlyList<StorageBlobSnapshot> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
    }
#pragma warning restore CS1998

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }


    /// <summary>
    /// The encrypted-HLS container is backed up like any other, and that is the whole point.
    ///
    /// <para>
    /// Every byte in it is derived from the playback master, so skipping it looks like an easy
    /// saving. It is not: the database is not restored alongside these containers, so after a
    /// blob-only restore every <c>SongMetadata</c> row still carries its <c>HlsStreamId</c> and every
    /// one of them points at a folder that would not exist. The catalogue would be silently
    /// unplayable with a database that looked perfectly healthy.
    /// </para>
    /// </summary>
    [Test]
    public async Task Backup_CopiesTheStreamingContainerAlongsideTheMedia()
    {
        SetupContainer(MusicContainer, Blob("song.mp3"));
        SetupContainer(StreamingContainer, Blob("abc/seg-000.ts"));

        var run = await _service.StartBackupAsync(1, "admin@example.com", false);
        await _service.RunAsync(run.Id);

        _gateway.Verify(
            gateway => gateway.CopyAsync(
                StreamingContainer,
                $"backup-{StreamingContainer}",
                "abc/seg-000.ts",
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Restore_PutsTheStreamingContainerBack()
    {
        SetupContainer($"backup-{StreamingContainer}", Blob("abc/seg-000.ts"));

        var run = await _service.StartRestoreAsync(
            1,
            "admin@example.com",
            new[] { StreamingContainer },
            StorageRestoreScope.MissingAndDiffering,
            overwriteNewerLive: true);

        await _service.RunAsync(run.Id);

        _gateway.Verify(
            gateway => gateway.CopyAsync(
                $"backup-{StreamingContainer}",
                StreamingContainer,
                "abc/seg-000.ts",
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
