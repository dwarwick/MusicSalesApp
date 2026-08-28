using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using SkiaSharp;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The gate a creator's upload passes through before it is staged and queued.
///
/// <para>
/// This is where the trade made when FFmpeg moved to Azure Functions is visible: the checks here
/// are cheap header inspections that can run on a request thread, not decodes. A file that sniffs
/// correctly but does not decode gets past this and is rejected later by the Function - so what
/// these tests pin is that everything that *can* be caught cheaply still is, before any bytes reach
/// Azure and before a job row exists.
/// </para>
/// </summary>
[TestFixture]
public class SongUploadJobServiceTests
{
    private DbContextOptions<AppDbContext> _options = null!;
    private TestFactory _factory = null!;
    private Mock<IBlobContainerFactory> _containers = null!;
    private Mock<ISongMetadataService> _metadata = null!;
    private Mock<IMusicService> _music = null!;
    private Mock<IAppSettingsService> _appSettings = null!;
    private FakeStagedBlobReader _stagedBlobs = null!;
    private RecordingQueueClient _queue = null!;
    private SongUploadJobService _service = null!;

    /// <summary>
    /// Stands in for staging. BlobContainerClient cannot be usefully mocked, so without this seam
    /// the size cap and header sniff on a staged upload - the only two checks left between a creator
    /// and the catalogue once bytes stop passing through the server - would be untestable offline.
    /// </summary>
    private sealed class FakeStagedBlobReader : IStagedBlobReader
    {
        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
        public List<(string Source, string Destination)> Copies { get; } = [];

        /// <summary>Set to report a length without holding the bytes, for size-cap tests.</summary>
        public Dictionary<string, long> Lengths { get; } = new(StringComparer.Ordinal);

        public Task<long?> GetLengthAsync(string blobPath, CancellationToken cancellationToken = default)
        {
            if (Lengths.TryGetValue(blobPath, out var length)) return Task.FromResult<long?>(length);
            if (Blobs.TryGetValue(blobPath, out var bytes)) return Task.FromResult<long?>(bytes.Length);
            return Task.FromResult<long?>(null);
        }

        public Task<byte[]> ReadHeaderAsync(string blobPath, int byteCount, CancellationToken cancellationToken = default)
            => Task.FromResult(Blobs.TryGetValue(blobPath, out var bytes)
                ? bytes.Take(byteCount).ToArray()
                : null);

        public Task CopyWithinStagingAsync(string source, string destination, CancellationToken cancellationToken = default)
        {
            Copies.Add((source, destination));
            return Task.CompletedTask;
        }
    }

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"upload-jobs-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);

        // Null staging deliberately: the stream-based tests all assert a rejection that must happen
        // *before* storage is touched, so reaching for the container at all would fail them loudly.
        // The staged tests read their bytes through IStagedBlobReader instead, and reach for the
        // container only to clean up after a rejection - which a null container turns into a no-op,
        // leaving the call itself as the observable evidence that cleanup ran.
        _containers = new Mock<IBlobContainerFactory>();
        _containers.Setup(factory => factory.GetUploadStagingContainer()).Returns((Azure.Storage.Blobs.BlobContainerClient)null);

        _metadata = new Mock<ISongMetadataService>();
        _metadata.Setup(service => service.ValidateUploadTargetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((SongMetadata)null);

        _music = new Mock<IMusicService>();
        _music.Setup(service => service.IsValidAudioFileAsync(It.IsAny<Stream>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        // Generous by default so the existing tests still exercise the gate they were written for;
        // the size tests below lower these deliberately.
        _appSettings = new Mock<IAppSettingsService>();
        _appSettings.Setup(settings => settings.GetMaxAudioUploadSizeMBAsync()).ReturnsAsync(100);
        _appSettings.Setup(settings => settings.GetMaxImageUploadSizeMBAsync()).ReturnsAsync(20);

        _queue = new RecordingQueueClient();
        _stagedBlobs = new FakeStagedBlobReader();

        _service = new SongUploadJobService(
            _factory,
            _containers.Object,
            _queue,
            _metadata.Object,
            _music.Object,
            _appSettings.Object,
            _stagedBlobs,
            Mock.Of<ILogger<SongUploadJobService>>());
    }

    [Test]
    public void BlankTitle_IsRejectedBeforeAnythingIsStagedOrQueued()
    {
        using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);

        Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateAsync(new SongUploadJobRequest
        {
            AudioStream = audio,
            AudioFileName = "Song.mp3",
            SongTitle = "   ",
            CreatorId = 1
        }));

        AssertNothingHappened();
    }

    [Test]
    public void TitleOverMaxLength_IsRejected()
    {
        using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);

        Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateAsync(new SongUploadJobRequest
        {
            AudioStream = audio,
            AudioFileName = "Song.mp3",
            SongTitle = new string('x', 201),
            CreatorId = 1
        }));

        AssertNothingHappened();
    }

    [Test]
    public void AudioOverTheAdminCap_IsRejectedBeforeAnythingIsStagedOrQueued()
    {
        // Until this gate existed the caps lived in exactly one place - the upload page's
        // IBrowserFile.OpenReadStream(maxAllowedSize) - and this method checked nothing. There is no
        // Kestrel or IIS body limit behind it either, so anything reaching here directly was
        // unbounded.
        _appSettings.Setup(settings => settings.GetMaxAudioUploadSizeMBAsync()).ReturnsAsync(1);
        using var audio = new MemoryStream(new byte[2 * 1024 * 1024]);

        var ex = Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateAsync(new SongUploadJobRequest
        {
            AudioStream = audio,
            AudioFileName = "Song.mp3",
            SongTitle = "Song",
            CreatorId = 1
        }));

        Assert.That(ex!.Message, Does.Contain("1 MB"), "The creator should be told what the limit is.");
        AssertNothingHappened();
    }

    [Test]
    public void CoverArtOverTheAdminCap_IsRejected()
    {
        _appSettings.Setup(settings => settings.GetMaxImageUploadSizeMBAsync()).ReturnsAsync(1);
        using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);
        using var cover = new MemoryStream(new byte[2 * 1024 * 1024]);

        Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateAsync(new SongUploadJobRequest
        {
            AudioStream = audio,
            AudioFileName = "Song.mp3",
            SongTitle = "Song",
            CreatorId = 1,
            CoverArtStream = cover,
            CoverArtFileName = "Song.png"
        }));

        AssertNothingHappened();
    }

    [Test]
    public void AFileWithinTheCap_ClearsTheSizeGate()
    {
        // Guards against a gate that rejects everything: this must fail later, on the null staging
        // container, not on size. AssertNothingHappened still holds - nothing was queued.
        _appSettings.Setup(settings => settings.GetMaxAudioUploadSizeMBAsync()).ReturnsAsync(100);
        using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);

        Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(new SongUploadJobRequest
        {
            AudioStream = audio,
            AudioFileName = "Song.mp3",
            SongTitle = "Song",
            CreatorId = 1
        }));

        AssertNothingHappened();
    }

    #region Staged uploads - the browser already put the bytes in Azure

    private static readonly Guid StagedGuid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static readonly byte[] Mp3Header = [(byte)'I', (byte)'D', (byte)'3', 0, 0, 0, 0, 0];

    private StagedSongUploadRequest StagedRequest(string coverStagedPath = null, string coverFileName = null)
        => new()
        {
            MediaGuid = StagedGuid,
            AudioFileName = "Song.mp3",
            SongTitle = "Song",
            CreatorId = 1,
            CoverArtStagedPath = coverStagedPath,
            CoverArtFileName = coverFileName
        };

    private void GivenStagedAudio(byte[] content = null)
        => _stagedBlobs.Blobs[MediaProcessingStagingPaths.Source(StagedGuid, ".mp3")] = content ?? Mp3Header;

    [Test]
    public async Task AStagedUpload_IsRecordedAndQueuedWithoutTouchingAStream()
    {
        GivenStagedAudio();

        var job = await _service.CreateFromStagedAsync(StagedRequest());

        Assert.Multiple(() =>
        {
            Assert.That(job.MediaGuid, Is.EqualTo(StagedGuid), "The GUID is minted before the upload, not here.");
            Assert.That(job.SourceBlobPath, Is.EqualTo(MediaProcessingStagingPaths.Source(StagedGuid, ".mp3")));
            Assert.That(job.SourceFileSize, Is.EqualTo(Mp3Header.Length), "Size comes from the blob, not the caller.");
            Assert.That(_queue.Transcodes, Has.Count.EqualTo(1));
            Assert.That(_queue.Transcodes[0].JobId, Is.EqualTo(StagedGuid));
        });
    }

    [Test]
    public void AStagedUploadThatNeverFinished_IsRejected()
    {
        // No blob at all: the browser started and stopped. Queueing this would hand the Function a
        // job whose source 404s, which it can only report as a failure minutes later.
        Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateFromStagedAsync(StagedRequest()));

        Assert.That(_queue.Transcodes, Is.Empty);
    }

    [Test]
    public void AStagedUploadOverTheCap_IsRejectedAndTheBlobIsDeleted()
    {
        // The browser enforced the cap too, but that is the creator's own machine reporting the size.
        // This is the first measurement anything we control has made - and the only one left, now
        // that OpenReadStream is out of the path.
        _appSettings.Setup(settings => settings.GetMaxAudioUploadSizeMBAsync()).ReturnsAsync(1);
        GivenStagedAudio();
        _stagedBlobs.Lengths[MediaProcessingStagingPaths.Source(StagedGuid, ".mp3")] = 5 * 1024 * 1024;

        var ex = Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateFromStagedAsync(StagedRequest()));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("1 MB"));
            Assert.That(_queue.Transcodes, Is.Empty);

            // Rejecting without this would leave 5 MB in staging under a GUID no row references, so
            // nothing would ever come back for it - the lifecycle rule would be the only sweeper.
            _containers.Verify(factory => factory.GetUploadStagingContainer(), Times.Once);
        });
    }

    [Test]
    public void AStagedFileThatIsNotReallyAudio_IsRejected()
    {
        // Proven from 64 bytes rather than a download - the whole point of the ranged read.
        GivenStagedAudio("this is not an mp3"u8.ToArray());

        Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateFromStagedAsync(StagedRequest()));

        Assert.Multiple(() =>
        {
            Assert.That(_queue.Transcodes, Is.Empty);
            _containers.Verify(factory => factory.GetUploadStagingContainer(), Times.Once);
        });
    }

    /// <summary>Stages a batch image and returns its path, as the image phase would have left it.</summary>
    private string GivenStagedCover(int sizeBytes = 4096)
    {
        var path = MediaProcessingStagingPaths.MatchBatchImage(Guid.NewGuid(), 2, ".png");
        _stagedBlobs.Blobs[path] = new byte[Math.Min(sizeBytes, 1024)];
        _stagedBlobs.Lengths[path] = sizeBytes;
        return path;
    }

    [Test]
    public async Task AMatchedCoverIsCopiedOutOfTheBatchFolderIntoTheSongsOwn()
    {
        // It could not have been uploaded there in the first place: which song an image belongs to
        // is unknown until after matching, which is after the images are uploaded.
        GivenStagedAudio();
        var batchPath = GivenStagedCover();

        var job = await _service.CreateFromStagedAsync(StagedRequest(batchPath, "Cover.png"));

        Assert.Multiple(() =>
        {
            Assert.That(_stagedBlobs.Copies, Has.Count.EqualTo(1));
            Assert.That(_stagedBlobs.Copies[0].Source, Is.EqualTo(batchPath));
            Assert.That(
                _stagedBlobs.Copies[0].Destination,
                Is.EqualTo(MediaProcessingStagingPaths.Cover(StagedGuid, ".png")));
            Assert.That(job.CoverArtBlobPath, Is.EqualTo(MediaProcessingStagingPaths.Cover(StagedGuid, ".png")));
            Assert.That(_queue.Transcodes[0].CoverArtExtension, Is.EqualTo(".png"));
        });
    }

    [Test]
    public async Task AnOversizedCoverCostsTheSongItsArtwork_NotTheUpload()
    {
        // The streamed path enforces this cap on the cover stream. The staged path had no image
        // limit at all: the write token minted for a batch image is Create|Write with no size bound,
        // so anything the browser was willing to PUT was copied in and handed to the Function.
        //
        // The song still publishes. Its audio is already staged and valid, and failing the expensive
        // half of the transfer over the cheap half's mistake would be a poor trade.
        _appSettings.Setup(settings => settings.GetMaxImageUploadSizeMBAsync()).ReturnsAsync(5);
        GivenStagedAudio();
        var batchPath = GivenStagedCover(sizeBytes: 40 * 1024 * 1024);

        var job = await _service.CreateFromStagedAsync(StagedRequest(batchPath, "Huge.png"));

        Assert.Multiple(() =>
        {
            Assert.That(_stagedBlobs.Copies, Is.Empty, "An over-cap image must not be copied in.");
            Assert.That(job.CoverArtBlobPath, Is.Null);
            Assert.That(_queue.Transcodes, Has.Count.EqualTo(1), "The song is still queued.");
            Assert.That(_queue.Transcodes[0].CoverArtBlobPath, Is.Null);
        });
    }

    [Test]
    public async Task ACoverExactlyOnTheCap_IsAccepted()
    {
        _appSettings.Setup(settings => settings.GetMaxImageUploadSizeMBAsync()).ReturnsAsync(5);
        GivenStagedAudio();
        var batchPath = GivenStagedCover(sizeBytes: 5 * 1024 * 1024);

        var job = await _service.CreateFromStagedAsync(StagedRequest(batchPath, "Exact.png"));

        Assert.That(job.CoverArtBlobPath, Is.Not.Null, "The limit is inclusive, as it is for audio.");
    }

    [Test]
    public void ACoverThatNeverFinishedUploading_IsRejected()
    {
        // Distinct from over-cap: nothing to copy at all. Queueing this would tell the Function to
        // fetch a blob that 404s.
        GivenStagedAudio();
        var missing = MediaProcessingStagingPaths.MatchBatchImage(Guid.NewGuid(), 0, ".png");

        Assert.ThrowsAsync<InvalidDataException>(
            () => _service.CreateFromStagedAsync(StagedRequest(missing, "Gone.png")));
    }

    [Test]
    public void AZeroLengthStagedUpload_IsRejectedAsIncomplete()
    {
        // What an interrupted single PUT leaves behind. The blob exists, so the "never uploaded"
        // check passes on existence alone - and a ranged read of the first 64 bytes of an empty blob
        // is answered with 416, not an empty body, so the header sniff faulted and the creator got a
        // raw Azure message instead of being told the file was incomplete.
        _stagedBlobs.Blobs[MediaProcessingStagingPaths.Source(StagedGuid, ".mp3")] = [];
        _stagedBlobs.Lengths[MediaProcessingStagingPaths.Source(StagedGuid, ".mp3")] = 0;

        var ex = Assert.ThrowsAsync<InvalidDataException>(
            () => _service.CreateFromStagedAsync(StagedRequest()));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("never fully uploaded"));
            Assert.That(_queue.Transcodes, Is.Empty);
        });
    }

    [Test]
    public async Task AnAudioOnlyStagedUpload_CopiesNothingAndTellsTheFunctionSo()
    {
        GivenStagedAudio();

        var job = await _service.CreateFromStagedAsync(StagedRequest());

        Assert.Multiple(() =>
        {
            Assert.That(_stagedBlobs.Copies, Is.Empty);
            Assert.That(job.CoverArtBlobPath, Is.Null);
            Assert.That(_queue.Transcodes[0].CoverArtBlobPath, Is.Null,
                "Null is how the Function knows to skip the image work entirely.");
        });
    }

    [Test]
    public void AnEmptyGuid_IsRejected()
    {
        // The GUID names the staging folder, so an empty one would look at batch-sibling paths.
        var request = new StagedSongUploadRequest
        {
            MediaGuid = Guid.Empty,
            AudioFileName = "Song.mp3",
            SongTitle = "Song",
            CreatorId = 1
        };

        Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateFromStagedAsync(request));
    }

    [Test]
    public void AStagedUpload_StillRunsTheOwnershipCheck()
    {
        // It also ran when the write token was minted, but minutes pass in between and an admin
        // art-replace or a second tab could claim these paths - dropping it here would turn an
        // ownership check into a time-of-check/time-of-use gap.
        GivenStagedAudio();
        _metadata
            .Setup(service => service.ValidateUploadTargetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ThrowsAsync(new UnauthorizedAccessException("belongs to another creator"));

        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.CreateFromStagedAsync(StagedRequest()));

        Assert.That(_queue.Transcodes, Is.Empty);
    }

    [Test]
    public void AStagedUploadWithABlankTitle_IsRejected()
    {
        GivenStagedAudio();
        var request = new StagedSongUploadRequest
        {
            MediaGuid = StagedGuid,
            AudioFileName = "Song.mp3",
            SongTitle = "   ",
            CreatorId = 1
        };

        Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateFromStagedAsync(request));
    }

    #endregion

    [Test]
    public void UnsupportedAudioExtension_IsRejected()
    {
        using var audio = new MemoryStream([1, 2, 3]);

        Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateAsync(new SongUploadJobRequest
        {
            AudioStream = audio,
            AudioFileName = "Song.txt",
            SongTitle = "Song",
            CreatorId = 1
        }));

        AssertNothingHappened();
    }

    [Test]
    public void CoverArtStreamWithoutAFilename_IsRejected()
    {
        // The extension decides the stored blob's name and content type, so a stream with no
        // filename is a caller bug rather than a bad upload - and silently dropping the art would
        // publish a song with no cover.
        using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);
        using var cover = new MemoryStream([1, 2, 3]);

        Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateAsync(new SongUploadJobRequest
        {
            AudioStream = audio,
            AudioFileName = "Song.mp3",
            SongTitle = "Song",
            CreatorId = 1,
            CoverArtStream = cover,
            CoverArtFileName = null
        }));

        AssertNothingHappened();
    }

    [Test]
    public void ContentThatDoesNotMatchItsExtension_IsRejected()
    {
        using var audio = new MemoryStream([0, 1, 2, 3]);
        _music.Setup(service => service.IsValidAudioFileAsync(It.IsAny<Stream>(), "Song.mp3"))
            .ReturnsAsync(false);

        Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateAsync(new SongUploadJobRequest
        {
            AudioStream = audio,
            AudioFileName = "Song.mp3",
            SongTitle = "Song",
            CreatorId = 1
        }));

        AssertNothingHappened();
    }

    [Test]
    public void UndecodableCoverArt_IsRejected()
    {
        using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);
        using var cover = new MemoryStream([1, 2, 3]);

        Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateAsync(new SongUploadJobRequest
        {
            AudioStream = audio,
            AudioFileName = "Song.mp3",
            SongTitle = "Song",
            CreatorId = 1,
            CoverArtStream = cover,
            CoverArtFileName = "Cover.png"
        }));

        AssertNothingHappened();
    }

    [Test]
    public void ReplacementOwnedByAnotherCreator_IsRejectedBeforeStaging()
    {
        // The collision/ownership check runs synchronously on purpose, so the creator still learns
        // about it in the review step rather than minutes later through a failed job.
        using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);
        _metadata.Setup(service => service.ValidateUploadTargetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), 7))
            .ThrowsAsync(new UnauthorizedAccessException("belongs to another creator"));

        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.CreateAsync(new SongUploadJobRequest
        {
            AudioStream = audio,
            AudioFileName = "Song.mp3",
            SongTitle = "Song",
            CreatorId = 7
        }));

        AssertNothingHappened();
    }

    [Test]
    public void UnconfiguredStaging_IsReportedAsConfigurationNotAsABadFile()
    {
        // A valid upload that cannot be staged is an operator problem. It must not surface to the
        // creator as "your file is corrupt".
        using var audio = new MemoryStream([(byte)'I', (byte)'D', (byte)'3']);

        Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(new SongUploadJobRequest
        {
            AudioStream = audio,
            AudioFileName = "Song.mp3",
            SongTitle = "Song",
            CreatorId = 1
        }));
    }

    [Test]
    public async Task GetActiveJobs_ReturnsOnlyUnfinishedJobsForThatCreator()
    {
        await using (var context = new AppDbContext(_options))
        {
            context.SongUploadJobs.AddRange(
                NewJob(creatorId: 1, SongUploadJobStatus.Queued),
                NewJob(creatorId: 1, SongUploadJobStatus.Processing),
                NewJob(creatorId: 1, SongUploadJobStatus.Completed),
                NewJob(creatorId: 1, SongUploadJobStatus.Failed),
                NewJob(creatorId: 2, SongUploadJobStatus.Processing));
            await context.SaveChangesAsync();
        }

        var active = await _service.GetActiveJobsAsync(creatorId: 1);

        Assert.Multiple(() =>
        {
            Assert.That(active, Has.Count.EqualTo(2));
            Assert.That(active.Select(job => job.Status), Is.All.AnyOf(
                SongUploadJobStatus.Queued,
                SongUploadJobStatus.Processing));
        });
    }

    [Test]
    public async Task GetActiveJobs_OrdersOldestFirstSoTheBatchKeepsItsOrder()
    {
        await using (var context = new AppDbContext(_options))
        {
            var older = NewJob(1, SongUploadJobStatus.Queued);
            older.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
            older.SongTitle = "First";
            var newer = NewJob(1, SongUploadJobStatus.Queued);
            newer.SongTitle = "Second";
            context.SongUploadJobs.AddRange(newer, older);
            await context.SaveChangesAsync();
        }

        var active = await _service.GetActiveJobsAsync(creatorId: 1);

        Assert.That(active.Select(job => job.SongTitle), Is.EqualTo(new[] { "First", "Second" }));
    }

    private void AssertNothingHappened()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_queue.Transcodes, Is.Empty, "A rejected upload must never be queued.");

            using var context = new AppDbContext(_options);
            Assert.That(context.SongUploadJobs.Count(), Is.Zero, "A rejected upload must leave no job row.");
        });
    }

    private static SongUploadJob NewJob(int creatorId, SongUploadJobStatus status) => new()
    {
        MediaGuid = Guid.NewGuid(),
        CreatorId = creatorId,
        SongTitle = "Song",
        SourceBlobPath = "staging/source.mp3",
        SourceFileName = "Song.mp3",
        SourceExtension = ".mp3",
        Status = status,
        Step = status == SongUploadJobStatus.Completed
            ? AudioProcessingStep.Completed
            : AudioProcessingStep.Queued
    };

    private sealed class RecordingQueueClient : IMediaProcessingQueueClient
    {
        public List<AudioTranscodeRequest> Transcodes { get; } = [];
        public List<AudioProbeRequest> Probes { get; } = [];
        public List<CoverArtMatchRequest> Matches { get; } = [];

        public bool IsConfigured => true;
        public bool IsCoverArtMatchConfigured => true;

        public Task EnqueueCoverArtMatchAsync(CoverArtMatchRequest request, CancellationToken cancellationToken = default)
        {
            Matches.Add(request);
            return Task.CompletedTask;
        }

        public bool IsPackagingConfigured => true;

        public List<AudioPackageRequest> PackageRequests { get; } = new();

        public Task EnqueuePackageAsync(AudioPackageRequest request, CancellationToken cancellationToken = default)
        {
            PackageRequests.Add(request);
            return Task.CompletedTask;
        }

        public Task EnqueueTranscodeAsync(AudioTranscodeRequest request, CancellationToken cancellationToken = default)
        {
            Transcodes.Add(request);
            return Task.CompletedTask;
        }

        public Task EnqueueProbesAsync(IEnumerable<AudioProbeRequest> requests, CancellationToken cancellationToken = default)
        {
            Probes.AddRange(requests);
            return Task.CompletedTask;
        }
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
