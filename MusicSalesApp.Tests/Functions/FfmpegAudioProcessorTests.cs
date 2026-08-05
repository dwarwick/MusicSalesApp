using MusicSalesApp.Functions.Audio;

namespace MusicSalesApp.Tests.Functions;

/// <summary>
/// Covers the parts of the FFmpeg wrapper that can be exercised without actually running FFmpeg:
/// duration parsing, and the classification that decides whether a failure means "this file is
/// bad" or "this worker is bad".
/// </summary>
[TestFixture]
public class FfmpegAudioProcessorTests
{
    [Test]
    public void TryParseDurationFromFfmpegOutput_WithDurationLine_ReturnsTotalSeconds()
    {
        var output = "Input #0, mp3, from 'song.mp3':\r\n  Duration: 00:03:21.50, start: 0.025057, bitrate: 320 kb/s";

        var result = FfmpegAudioProcessor.TryParseDurationFromFfmpegOutput(output);

        Assert.That(result, Is.EqualTo(201.5).Within(0.001));
    }

    [Test]
    public void TryParseDurationFromFfmpegOutput_WithHourDurationLine_ReturnsTotalSeconds()
    {
        var output = "Duration: 01:02:03.25, start: 0.000000, bitrate: 192 kb/s";

        var result = FfmpegAudioProcessor.TryParseDurationFromFfmpegOutput(output);

        Assert.That(result, Is.EqualTo(3723.25).Within(0.001));
    }

    [Test]
    public void TryParseDurationFromFfmpegOutput_WithoutDurationLine_ReturnsNull()
    {
        var output = "Invalid data found when processing input";

        var result = FfmpegAudioProcessor.TryParseDurationFromFfmpegOutput(output);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void TryParseProgressDuration_UsesTheLastOutTime()
    {
        // -progress emits a running out_time. Everything before the final one is a checkpoint
        // partway through the decode; only the last reflects the file's real length.
        var output = string.Join(
            "\n",
            "out_time=00:00:10.000000",
            "progress=continue",
            "out_time=00:01:05.500000",
            "progress=end");

        var result = FfmpegAudioProcessor.TryParseProgressDuration(output);

        Assert.That(result, Is.EqualTo(65.5).Within(0.001));
    }

    [Test]
    public void TryParseProgressDuration_WithNoProgressLines_ReturnsNull()
    {
        Assert.That(FfmpegAudioProcessor.TryParseProgressDuration("Duration: 00:01:00.00"), Is.Null);
    }

    [Test]
    public void TryParseProgressDuration_WithZeroOutTime_ReturnsNull()
    {
        // A file that decodes to nothing is not playable, and reporting 0 as a duration would let
        // it through as if it were.
        Assert.That(FfmpegAudioProcessor.TryParseProgressDuration("out_time=00:00:00.000000"), Is.Null);
    }

    /// <summary>
    /// The distinction that decides retry versus blame-the-file. Getting this wrong either
    /// quarantines good songs during an infrastructure blip, or retries a genuinely corrupt file
    /// until it poisons the queue.
    /// </summary>
    [TestCase("No space left on device")]
    [TestCase("Permission denied")]
    [TestCase("Cannot allocate memory")]
    [TestCase("Resource temporarily unavailable")]
    public void IsInfrastructureDiagnostic_ForMachineFailures_ReturnsTrue(string diagnostic)
    {
        Assert.That(FfmpegAudioProcessor.IsInfrastructureDiagnostic(diagnostic), Is.True);
    }

    [TestCase("Invalid data found when processing input")]
    [TestCase("moov atom not found")]
    [TestCase("")]
    [TestCase(null)]
    public void IsInfrastructureDiagnostic_ForFileFailures_ReturnsFalse(string diagnostic)
    {
        Assert.That(FfmpegAudioProcessor.IsInfrastructureDiagnostic(diagnostic), Is.False);
    }

    [Test]
    public void SanitizeDecoderDiagnostic_FlattensNewlinesSoItFitsOneColumn()
    {
        var result = FfmpegAudioProcessor.SanitizeDecoderDiagnostic("line one\r\nline two");

        Assert.That(result, Does.Not.Contain("\n"));
        Assert.That(result, Does.Not.Contain("\r"));
        Assert.That(result, Does.Contain("line one"));
        Assert.That(result, Does.Contain("line two"));
    }

    [Test]
    public void SanitizeDecoderDiagnostic_TruncatesToTheColumnWidth()
    {
        // FailureMessage/Diagnostic columns are nvarchar(2000); a longer string would throw on save.
        var result = FfmpegAudioProcessor.SanitizeDecoderDiagnostic(new string('x', 5000));

        Assert.That(result, Has.Length.EqualTo(2000));
    }

    [Test]
    public void SanitizeDecoderDiagnostic_WithNothingFromFfmpeg_StillExplainsItself()
    {
        Assert.That(FfmpegAudioProcessor.SanitizeDecoderDiagnostic("  "), Is.Not.Empty);
    }

    [Test]
    public void BuildTempPath_KeepsASupportedExtension()
    {
        // FFmpeg uses the extension as a demuxer hint, so it has to survive into the temp file.
        var path = FfmpegAudioProcessor.BuildTempPath(".wav", "upload-source");

        Assert.That(Path.GetExtension(path), Is.EqualTo(".wav"));
        Assert.That(Path.GetFileName(path), Does.StartWith("upload-source-"));
    }

    [TestCase(".exe")]
    [TestCase("")]
    [TestCase(null)]
    public void BuildTempPath_RewritesAnUnsupportedExtensionToBin(string extension)
    {
        // An attacker-supplied extension must never reach the filesystem as-is.
        var path = FfmpegAudioProcessor.BuildTempPath(extension, "upload-source");

        Assert.That(Path.GetExtension(path), Is.EqualTo(".bin"));
    }

    [Test]
    public async Task DecodeAsync_WithMissingFile_IsUnplayableRatherThanInconclusive()
    {
        var processor = new FfmpegAudioProcessor(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegAudioProcessor>.Instance);

        var result = await processor.DecodeAsync(
            Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.mp3"));

        // Only meaningful when ffmpeg.exe is actually present beside the test assembly; without it
        // the resolver legitimately reports Inconclusive first.
        if (FfmpegAudioProcessor.ResolveFfmpegExecutablePath() is null)
        {
            Assert.That(result.Status, Is.EqualTo(AudioDecodeStatus.Inconclusive));
            Assert.That(result.FailureCode, Is.EqualTo("FfmpegUnavailable"));
            return;
        }

        Assert.That(result.Status, Is.EqualTo(AudioDecodeStatus.Unplayable));
    }

    [Test]
    public async Task DecodeAsync_WithEmptyFile_IsUnplayable()
    {
        var processor = new FfmpegAudioProcessor(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegAudioProcessor>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(path, []);

        try
        {
            var result = await processor.DecodeAsync(path);

            if (FfmpegAudioProcessor.ResolveFfmpegExecutablePath() is null)
            {
                Assert.That(result.Status, Is.EqualTo(AudioDecodeStatus.Inconclusive));
                return;
            }

            Assert.That(result.Status, Is.EqualTo(AudioDecodeStatus.Unplayable));
            Assert.That(result.FailureCode, Is.EqualTo("EmptyFile"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ToOutcome_MapsAllThreeStatusesDistinctly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                AudioDecodeResult.Playable(1).ToOutcome(),
                Is.EqualTo(MusicSalesApp.Common.Contracts.AudioProcessingOutcome.Playable));
            Assert.That(
                AudioDecodeResult.Unplayable("x", "y").ToOutcome(),
                Is.EqualTo(MusicSalesApp.Common.Contracts.AudioProcessingOutcome.Unplayable));
            Assert.That(
                AudioDecodeResult.Inconclusive("x", "y").ToOutcome(),
                Is.EqualTo(MusicSalesApp.Common.Contracts.AudioProcessingOutcome.Inconclusive));
        });
    }
}
