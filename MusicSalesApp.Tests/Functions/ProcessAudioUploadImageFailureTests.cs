using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Functions.Audio;
using MusicSalesApp.Functions.Functions;
using MusicSalesApp.Functions.Images;
using MusicSalesApp.Functions.Services;

namespace MusicSalesApp.Tests.Functions;

/// <summary>
/// The one image invariant that must never regress: <b>a song whose cover art cannot be rendered
/// still publishes.</b>
///
/// <para>
/// Renditions are derived data the admin backfill can rebuild at any time, and a song with no
/// recorded widths simply serves its full-size master — the behaviour that predates renditions
/// entirely. So the image block is deliberately <i>not</i> the three-way playable/unplayable/
/// inconclusive rule the audio path uses: there is no image equivalent of "throw and let the queue
/// retry", because retrying would re-run a transcode that already succeeded, costing minutes to
/// salvage a thumbnail.
/// </para>
///
/// <para>
/// This is the fixture most likely to be broken by a future well-meaning edit that "makes the image
/// path consistent with the audio path". It should not be made consistent.
/// </para>
/// </summary>
[TestFixture]
public class ProcessAudioUploadImageFailureTests
{
    private const double DurationSeconds = 123.5;
    private const string SourceBlobPath = "abc/source.mp3";

    private Mock<IFfmpegAudioProcessor> _ffmpeg = null!;
    private FakeMediaBlobStore _blobStore = null!;
    private Mock<ICoverArtRenditionGenerator> _renditions = null!;
    private RecordingCallbackClient _callbacks = null!;
    private ProcessAudioUploadFunction _function = null!;

    [SetUp]
    public void SetUp()
    {
        _ffmpeg = new Mock<IFfmpegAudioProcessor>();
        _ffmpeg
            .Setup(processor => processor.DecodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AudioDecodeResult.Playable(DurationSeconds));

        _blobStore = new FakeMediaBlobStore();

        // The audio half has to get all the way through, or the Function reports SourceBlobMissing
        // and returns before any image code runs. Content is irrelevant - IFfmpegAudioProcessor is
        // mocked, so nothing decodes it.
        _blobStore.StagedBlobs[SourceBlobPath] = "not really an mp3"u8.ToArray();

        _renditions = new Mock<ICoverArtRenditionGenerator>();
        _callbacks = new RecordingCallbackClient();

        _function = new ProcessAudioUploadFunction(
            _ffmpeg.Object,
            _blobStore,
            _renditions.Object,
            _callbacks,
            Mock.Of<ILogger<ProcessAudioUploadFunction>>());
    }

    /// <summary>An MP3 upload, so no transcode runs and the test is purely about the image half.</summary>
    private static AudioTranscodeRequest RequestWithCoverArt(string coverPath = "abc/cover.png")
        => new()
        {
            JobId = Guid.NewGuid(),
            SourceBlobPath = SourceBlobPath,
            SourceFileName = "song.mp3",
            SourceExtension = ".mp3",
            PlaybackBlobPath = "abc/playback.mp3",
            CoverArtBlobPath = coverPath,
            CoverArtExtension = coverPath is null ? null : ".png"
        };

    private void SetUpRenditions(CoverArtRenditionResult result)
        => _renditions
            .Setup(generator => generator.GenerateAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IProgress<double>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Test]
    public async Task ADecodeFailure_StillPublishesTheSongWithEmptyWidths()
    {
        SetUpRenditions(CoverArtRenditionResult.Failed(ImageVariantFailureCodes.DecodeFailed));

        await _function.ProcessAudioUpload(RequestWithCoverArt(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_callbacks.Results, Has.Count.EqualTo(1));
            Assert.That(_callbacks.Results[0].Outcome, Is.EqualTo(AudioProcessingOutcome.Playable));
            Assert.That(_callbacks.Results[0].DurationSeconds, Is.EqualTo(DurationSeconds));
            Assert.That(_callbacks.Results[0].CoverArtVariantWidths, Is.Empty);
            Assert.That(
                _callbacks.Results[0].CoverArtDiagnosticCode,
                Is.EqualTo(ImageVariantFailureCodes.DecodeFailed));
        });
    }

    [Test]
    public async Task AnUploadFailure_StillPublishesTheSongAndReportsThePartialLadder()
    {
        SetUpRenditions(new CoverArtRenditionResult(
            new[] { 128, 320 }, 2000, 2000, ImageVariantFailureCodes.UploadFailed));

        await _function.ProcessAudioUpload(RequestWithCoverArt(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_callbacks.Results[0].Outcome, Is.EqualTo(AudioProcessingOutcome.Playable));
            Assert.That(_callbacks.Results[0].CoverArtVariantWidths, Is.EqualTo(new[] { 128, 320 }));
        });
    }

    [Test]
    public void AnUnexpectedThrowFromTheGenerator_IsNotSwallowedIntoASilentSuccess()
    {
        // The generator's contract is "never throws except on cancellation", so if one ever escapes
        // it is a bug in the generator rather than a bad upload. Letting it propagate means the
        // queue retries and the fault is visible, instead of a song quietly publishing with no art
        // and no diagnostic. Pinned so the behaviour is a decision rather than an accident.
        _renditions
            .Setup(generator => generator.GenerateAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IProgress<double>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _function.ProcessAudioUpload(RequestWithCoverArt(), CancellationToken.None));

        Assert.That(_callbacks.Results, Is.Empty);
    }

    [Test]
    public async Task AFullLadder_IsReportedAlongsideTheSourceDimensions()
    {
        SetUpRenditions(new CoverArtRenditionResult(ImageVariantSizes.CoverArt, 2000, 1800, null));

        await _function.ProcessAudioUpload(RequestWithCoverArt(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_callbacks.Results[0].CoverArtVariantWidths, Is.EqualTo(ImageVariantSizes.CoverArt));
            Assert.That(_callbacks.Results[0].CoverArtWidth, Is.EqualTo(2000));
            Assert.That(_callbacks.Results[0].CoverArtHeight, Is.EqualTo(1800));
            Assert.That(_callbacks.Results[0].CoverArtDiagnosticCode, Is.Null);
        });
    }

    [Test]
    public async Task AnAudioOnlyUpload_SkipsTheImageWorkAndReportsNullWidths()
    {
        // Null rather than empty. "There was never any art" and "the art could not be rendered" are
        // different answers, and the API branches on the difference when it records the width set.
        await _function.ProcessAudioUpload(RequestWithCoverArt(coverPath: null), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_callbacks.Results[0].Outcome, Is.EqualTo(AudioProcessingOutcome.Playable));
            Assert.That(_callbacks.Results[0].CoverArtVariantWidths, Is.Null);
        });

        _renditions.Verify(
            generator => generator.GenerateAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IProgress<double>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task AnAudioOnlyUpload_NeverReportsTheRenderingArtworkStep()
    {
        await _function.ProcessAudioUpload(RequestWithCoverArt(coverPath: null), CancellationToken.None);

        Assert.That(
            _callbacks.Progress.Select(progress => progress.Step),
            Does.Not.Contain(AudioProcessingStep.RenderingArtwork));
    }

    [Test]
    public async Task AnUploadWithCoverArt_AnnouncesTheRenderingArtworkStep()
    {
        SetUpRenditions(new CoverArtRenditionResult(ImageVariantSizes.CoverArt, 2000, 2000, null));

        await _function.ProcessAudioUpload(RequestWithCoverArt(), CancellationToken.None);

        Assert.That(
            _callbacks.Progress.Select(progress => progress.Step),
            Does.Contain(AudioProcessingStep.RenderingArtwork));
    }

    /// <summary>Captures what the Function posted, so the tests can assert on the wire contract.</summary>
    private sealed class RecordingCallbackClient : IMediaProcessingCallbackClient
    {
        public List<AudioTranscodeResult> Results { get; } = [];
        public List<AudioProcessingProgress> Progress { get; } = [];

        public Task PostTranscodeResultAsync(AudioTranscodeResult result, CancellationToken cancellationToken = default)
        {
            Results.Add(result);
            return Task.CompletedTask;
        }

        public Task PostProbeResultAsync(AudioProbeResult result, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PostPackageResultAsync(AudioPackageResult result, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PostMatchResultAsync(CoverArtMatchResult result, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PostMatchProgressAsync(CoverArtMatchProgress progress, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PostProgressAsync(AudioProcessingProgress progress, CancellationToken cancellationToken = default)
        {
            lock (Progress) Progress.Add(progress);
            return Task.CompletedTask;
        }
    }
}
