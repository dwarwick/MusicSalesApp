using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Functions.Images;

namespace MusicSalesApp.Tests.Functions;

/// <summary>
/// The rendition pass the Azure Function runs on a staged cover art.
///
/// <para>
/// Two properties are worth pinning here. The ladder must never upscale but must always yield at
/// least one rendition, because the mobile client treats "has cover art" and "has a rendition" as
/// the same thing. And every failure has to come back as a result rather than an exception — the
/// song publishes either way, and a throw here would retry a transcode that already succeeded.
/// </para>
/// </summary>
[TestFixture]
public class CoverArtRenditionGeneratorTests
{
    private const string StagedCoverPath = "abc/cover.png";

    private FakeMediaBlobStore _blobStore = null!;
    private CoverArtRenditionGenerator _generator = null!;
    private Guid _jobId;
    private string _scratchPath = null!;

    [SetUp]
    public void SetUp()
    {
        _blobStore = new FakeMediaBlobStore();
        _generator = new CoverArtRenditionGenerator(
            _blobStore,
            Mock.Of<ILogger<CoverArtRenditionGenerator>>());
        _jobId = Guid.NewGuid();
        _scratchPath = Path.Combine(Path.GetTempPath(), $"rendition-test-{Guid.NewGuid():N}.png");
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_scratchPath))
            File.Delete(_scratchPath);
    }

    private Task<CoverArtRenditionResult> GenerateAsync(IProgress<double> progress = null)
        => _generator.GenerateAsync(_jobId, StagedCoverPath, ".png", _scratchPath, progress);

    [Test]
    public async Task ASourceLargerThanEveryRung_WritesTheWholeLadder()
    {
        _blobStore.StagedBlobs[StagedCoverPath] = FakeMediaBlobStore.CreatePng(2000, 2000);

        var result = await GenerateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Widths, Is.EqualTo(ImageVariantSizes.CoverArt));
            Assert.That(result.SourceWidth, Is.EqualTo(2000));
            Assert.That(result.SourceHeight, Is.EqualTo(2000));
            Assert.That(result.DiagnosticCode, Is.Null);
            Assert.That(_blobStore.MediaWrites, Has.Count.EqualTo(ImageVariantSizes.CoverArt.Count));
        });
    }

    [Test]
    public async Task RenditionsAreWrittenUnderTheMastersEventualMediaPath()
    {
        // The master does not exist in the media container yet - assembly copies it in afterwards -
        // so the Function has to derive this path from the job GUID alone. If it derives it
        // differently from SongMediaPaths, the public media whitelist can never resolve a rendition
        // back to its master and every rendition 404s.
        _blobStore.StagedBlobs[StagedCoverPath] = FakeMediaBlobStore.CreatePng(400, 400);

        await GenerateAsync();

        var expectedMaster = SongMediaPaths.CoverArt(_jobId, ".png");
        Assert.That(
            _blobStore.MediaWrites.Keys,
            Does.Contain(ImageVariantPaths.Variant(expectedMaster, 320)));
    }

    [Test]
    public async Task ASourceNarrowerThanTheSmallestRung_StillYieldsOneRendition()
    {
        // The invariant the mobile client depends on: any image with cover art has at least one
        // rendition. A 100px source fills no rung, so it is re-encoded at its own width.
        _blobStore.StagedBlobs[StagedCoverPath] = FakeMediaBlobStore.CreatePng(100, 100);

        var result = await GenerateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Widths, Is.EqualTo(new[] { 100 }));
            Assert.That(_blobStore.MediaWrites, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task NeverUpscales()
    {
        _blobStore.StagedBlobs[StagedCoverPath] = FakeMediaBlobStore.CreatePng(500, 500);

        var result = await GenerateAsync();

        Assert.That(result.Widths, Is.EqualTo(new[] { 128, 320 }));
    }

    [Test]
    public async Task AMissingStagedCover_ReportsSourceMissingRatherThanThrowing()
    {
        // Assembly is about to try to copy this same blob and already knows how to unwind and leave
        // the job retryable. Failing the song here would pre-empt that decision over a thumbnail.
        var result = await GenerateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Widths, Is.Empty);
            Assert.That(result.DiagnosticCode, Is.EqualTo(ImageVariantFailureCodes.SourceMissing));
        });
    }

    [Test]
    public async Task AnUndecodableCover_ReportsDecodeFailedRatherThanThrowing()
    {
        _blobStore.StagedBlobs[StagedCoverPath] = "this is not an image"u8.ToArray();

        var result = await GenerateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Widths, Is.Empty);
            Assert.That(result.DiagnosticCode, Is.EqualTo(ImageVariantFailureCodes.DecodeFailed));
            Assert.That(_blobStore.MediaWrites, Is.Empty);
        });
    }

    [Test]
    public async Task AFailedUploadKeepsTheRungsAlreadyWritten()
    {
        // A partial ladder still serves every surface whose rung exists; the rest fall back to the
        // master exactly as they would with no renditions at all. Discarding the run would throw
        // away work for nothing.
        _blobStore.StagedBlobs[StagedCoverPath] = FakeMediaBlobStore.CreatePng(2000, 2000);
        _blobStore.FailMediaWriteFromIndex = 3;

        var result = await GenerateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Widths, Is.EqualTo(new[] { 128, 320 }));
            Assert.That(result.DiagnosticCode, Is.EqualTo(ImageVariantFailureCodes.UploadFailed));
            Assert.That(result.SourceWidth, Is.EqualTo(2000));
        });
    }

    [Test]
    public async Task ABlankCoverPath_IsANoOp()
    {
        var result = await _generator.GenerateAsync(_jobId, string.Empty, ".png", _scratchPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Widths, Is.Empty);
            Assert.That(result.DiagnosticCode, Is.EqualTo(ImageVariantFailureCodes.NoSourcePath));
            Assert.That(_blobStore.MediaWrites, Is.Empty);
        });
    }

    [Test]
    public async Task ProgressIsReportedOncePerRungAndEndsAtOneHundred()
    {
        _blobStore.StagedBlobs[StagedCoverPath] = FakeMediaBlobStore.CreatePng(2000, 2000);
        var reported = new List<double>();

        await GenerateAsync(new Progress<double>(percent =>
        {
            lock (reported) reported.Add(percent);
        }));

        // Progress<T> posts to the thread pool, so the last callback can still be in flight.
        await Task.Delay(50);

        lock (reported)
        {
            Assert.Multiple(() =>
            {
                Assert.That(reported, Has.Count.EqualTo(ImageVariantSizes.CoverArt.Count));
                Assert.That(reported[^1], Is.EqualTo(100d).Within(0.0001));
            });
        }
    }

    [Test]
    public async Task RerunningWritesTheSamePaths_SoAQueueRedeliveryIsIdempotent()
    {
        _blobStore.StagedBlobs[StagedCoverPath] = FakeMediaBlobStore.CreatePng(2000, 2000);

        var first = await GenerateAsync();
        var firstPaths = _blobStore.MediaWrites.Keys.OrderBy(path => path, StringComparer.Ordinal).ToList();

        var second = await GenerateAsync();
        var secondPaths = _blobStore.MediaWrites.Keys.OrderBy(path => path, StringComparer.Ordinal).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(second.Widths, Is.EqualTo(first.Widths));
            Assert.That(secondPaths, Is.EqualTo(firstPaths));
            Assert.That(_blobStore.MediaWrites, Has.Count.EqualTo(ImageVariantSizes.CoverArt.Count));
        });
    }
}
