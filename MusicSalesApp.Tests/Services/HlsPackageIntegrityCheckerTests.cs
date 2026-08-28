using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using NUnit.Framework;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The sweep that notices the database and the streaming container have drifted apart.
///
/// <para>
/// The failure it exists to catch is a quiet one: after a restore that missed the streaming
/// container, every row still carries its <c>HlsStreamId</c>, so the catalogue looks healthy from
/// the database and plays nothing. Nobody finds that by looking at a table.
/// </para>
/// </summary>
[TestFixture]
public class HlsPackageIntegrityCheckerTests
{
    private DbContextOptions<AppDbContext> _options = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"hls-integrity-{Guid.NewGuid():N}")
            .Options;
    }

    private async Task<Guid> GivenPackagedSongAsync(
        int id,
        int? segmentCount = 10,
        bool isActive = true,
        bool isEnabled = true)
    {
        var streamId = Guid.NewGuid();

        await using var context = new AppDbContext(_options);
        context.SongMetadata.Add(new SongMetadata
        {
            Id = id,
            SongTitle = $"Song {id}",
            Mp3BlobPath = $"folder{id}/song.mp3",
            IsActive = isActive,
            IsEnabled = isEnabled,
            HlsStreamId = streamId,
            HlsSegmentCount = segmentCount
        });
        await context.SaveChangesAsync();

        return streamId;
    }

    /// <summary>
    /// Builds a checker over a container where only <paramref name="presentBlobs"/> exist.
    /// </summary>
    private HlsPackageIntegrityChecker CreateChecker(
        ISet<string> presentBlobs,
        ISet<string> throwingBlobs = null)
    {
        var container = new Mock<BlobContainerClient>();
        container.Setup(c => c.Name).Returns("musicstreaming-test");

        container
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Returns((string path) =>
            {
                var blob = new Mock<BlobClient>();

                if (throwingBlobs != null && throwingBlobs.Contains(path))
                {
                    blob.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
                        .ThrowsAsync(new RequestFailedException(503, "the service is unavailable"));
                }
                else
                {
                    blob.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
                        .ReturnsAsync(Response.FromValue(presentBlobs.Contains(path), Mock.Of<Response>()));
                }

                return blob.Object;
            });

        var factory = new Mock<IBlobContainerFactory>();
        factory.Setup(f => f.GetStreamingContainer()).Returns(container.Object);

        return new HlsPackageIntegrityChecker(
            new TestFactory(_options),
            factory.Object,
            Mock.Of<ILogger<HlsPackageIntegrityChecker>>());
    }

    [Test]
    public async Task AnIntactPackageIsNotReported()
    {
        var streamId = await GivenPackagedSongAsync(1, segmentCount: 10);

        var present = new HashSet<string>
        {
            HlsPackagePaths.Manifest(streamId),
            HlsPackagePaths.Segment(streamId, 9)
        };

        var report = await CreateChecker(present).CheckAsync();

        Assert.Multiple(() =>
        {
            Assert.That(report.CheckedCount, Is.EqualTo(1));
            Assert.That(report.ProblemCount, Is.Zero);
            Assert.That(report.WasInconclusive, Is.False);
        });
    }

    /// <summary>
    /// The headline case: the row says there is a package and the container is empty.
    /// </summary>
    [Test]
    public async Task AMissingManifestIsReportedAgainstItsSong()
    {
        var streamId = await GivenPackagedSongAsync(2);

        var report = await CreateChecker(new HashSet<string>()).CheckAsync();

        Assert.Multiple(() =>
        {
            Assert.That(report.ProblemCount, Is.EqualTo(1));
            Assert.That(report.Problems.Single().SongMetadataId, Is.EqualTo(2));
            Assert.That(report.Problems.Single().HlsStreamId, Is.EqualTo(streamId));
            Assert.That(report.Problems.Single().Problem, Does.Contain("no manifest"));
        });
    }

    /// <summary>
    /// A half-restored package: the manifest came back, the segments did not all follow.
    ///
    /// <para>
    /// The last segment is what is checked, because a partial restore leaves a prefix - any earlier
    /// segment would still be present and would report the package as healthy.
    /// </para>
    /// </summary>
    [Test]
    public async Task AManifestWithoutItsLastSegmentIsReported()
    {
        var streamId = await GivenPackagedSongAsync(3, segmentCount: 40);

        var present = new HashSet<string>
        {
            HlsPackagePaths.Manifest(streamId),
            HlsPackagePaths.Segment(streamId, 0),
            HlsPackagePaths.Segment(streamId, 1)
        };

        var report = await CreateChecker(present).CheckAsync();

        Assert.Multiple(() =>
        {
            Assert.That(report.ProblemCount, Is.EqualTo(1));
            Assert.That(report.Problems.Single().Problem, Does.Contain("segment 39 of 40"));
        });
    }

    /// <summary>
    /// Storage being unreachable must not read as a catalogue-wide outage.
    ///
    /// <para>
    /// "We could not ask" and "the packages are gone" are the same count and opposite meanings, and
    /// acting on the wrong one would repackage a catalogue that was never broken.
    /// </para>
    /// </summary>
    [Test]
    public async Task WhenStorageIsUnreachableTheSweepIsInconclusiveRatherThanAlarming()
    {
        var streamId = await GivenPackagedSongAsync(4);

        var report = await CreateChecker(
            presentBlobs: new HashSet<string>(),
            throwingBlobs: new HashSet<string> { HlsPackagePaths.Manifest(streamId) }).CheckAsync();

        Assert.Multiple(() =>
        {
            Assert.That(report.WasInconclusive, Is.True);
            Assert.That(report.ProblemCount, Is.Zero, "an unreachable account must not condemn songs");
        });
    }

    [Test]
    public async Task SongsWithNoPackageRecordedAreNotChecked()
    {
        // Nothing claims a package, so there is nothing to disagree with. During the rollout this is
        // most of the catalogue, and reporting it would bury the real signal.
        await using (var context = new AppDbContext(_options))
        {
            context.SongMetadata.Add(new SongMetadata
            {
                Id = 5,
                SongTitle = "Unpackaged",
                Mp3BlobPath = "folder5/song.mp3",
                IsActive = true,
                IsEnabled = true,
                HlsStreamId = null
            });
            await context.SaveChangesAsync();
        }

        var report = await CreateChecker(new HashSet<string>()).CheckAsync();

        Assert.That(report.CheckedCount, Is.Zero);
        Assert.That(report.ProblemCount, Is.Zero);
    }

    [Test]
    public async Task DisabledAndInactiveSongsAreNotChecked()
    {
        // They are already off the air, so a missing package is not a fault anyone can hear.
        await GivenPackagedSongAsync(6, isEnabled: false);
        await GivenPackagedSongAsync(7, isActive: false);

        var report = await CreateChecker(new HashSet<string>()).CheckAsync();

        Assert.That(report.CheckedCount, Is.Zero);
    }

    /// <summary>
    /// A package recorded before segment counts existed can only be checked as far as its manifest.
    /// </summary>
    [Test]
    public async Task APackageWithNoRecordedSegmentCountIsJudgedOnItsManifestAlone()
    {
        var streamId = await GivenPackagedSongAsync(8, segmentCount: null);

        var report = await CreateChecker(
            new HashSet<string> { HlsPackagePaths.Manifest(streamId) }).CheckAsync();

        Assert.That(report.ProblemCount, Is.Zero);
    }

    private sealed class TestFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestFactory(DbContextOptions<AppDbContext> options) => _options = options;

        public AppDbContext CreateDbContext() => new(_options);
    }
}
