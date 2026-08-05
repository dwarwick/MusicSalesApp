using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Contracts;
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
    private RecordingQueueClient _queue = null!;
    private SongUploadJobService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"upload-jobs-{Guid.NewGuid():N}")
            .Options;
        _factory = new TestFactory(_options);

        // Null staging deliberately: every test here asserts a rejection that must happen *before*
        // storage is touched, so reaching for the container at all would fail the test loudly.
        _containers = new Mock<IBlobContainerFactory>();
        _containers.Setup(factory => factory.GetUploadStagingContainer()).Returns((Azure.Storage.Blobs.BlobContainerClient)null);

        _metadata = new Mock<ISongMetadataService>();
        _metadata.Setup(service => service.ValidateUploadTargetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((SongMetadata)null);

        _music = new Mock<IMusicService>();
        _music.Setup(service => service.IsValidAudioFileAsync(It.IsAny<Stream>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _queue = new RecordingQueueClient();

        _service = new SongUploadJobService(
            _factory,
            _containers.Object,
            _queue,
            _metadata.Object,
            _music.Object,
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

        public bool IsConfigured => true;

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
