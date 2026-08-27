using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Functions.Audio;

/// <summary>
/// The outcome of one encrypted-HLS packaging run.
///
/// <para>
/// Carries the same three-way status as <see cref="AudioDecodeResult"/> and for the same reason: a
/// source FFmpeg refuses is the file's fault and the creator should be told, while a worker that
/// ran out of disk is the machine's fault and the message must be retried on another instance.
/// Collapsing them either condemns good songs during an infrastructure blip or retries a corrupt
/// file until it poisons the queue.
/// </para>
/// </summary>
public sealed class HlsPackageResult
{
    public AudioDecodeStatus Status { get; private init; }

    /// <summary>Absolute path of the directory FFmpeg wrote the manifest and segments into.</summary>
    public string OutputDirectory { get; private init; }

    /// <summary>Segment file names in playback order, as listed by the manifest.</summary>
    public IReadOnlyList<string> SegmentFileNames { get; private init; } = Array.Empty<string>();

    /// <summary>The manifest's <c>#EXT-X-TARGETDURATION</c>, in seconds.</summary>
    public double TargetDurationSeconds { get; private init; }

    /// <summary>Total playable duration, summed from the manifest's <c>#EXTINF</c> lines.</summary>
    public double? DurationSeconds { get; private init; }

    public string FailureCode { get; private init; }

    public string Diagnostic { get; private init; }

    public static HlsPackageResult Packaged(
        string outputDirectory,
        IReadOnlyList<string> segmentFileNames,
        double targetDurationSeconds,
        double? durationSeconds) => new()
    {
        Status = AudioDecodeStatus.Playable,
        OutputDirectory = outputDirectory,
        SegmentFileNames = segmentFileNames,
        TargetDurationSeconds = targetDurationSeconds,
        DurationSeconds = durationSeconds
    };

    public static HlsPackageResult Unplayable(string failureCode, string diagnostic) => new()
    {
        Status = AudioDecodeStatus.Unplayable,
        FailureCode = failureCode,
        Diagnostic = diagnostic
    };

    public static HlsPackageResult Inconclusive(string failureCode, string diagnostic) => new()
    {
        Status = AudioDecodeStatus.Inconclusive,
        FailureCode = failureCode,
        Diagnostic = diagnostic
    };

    /// <summary>Maps onto the wire contract the web app receives.</summary>
    public AudioProcessingOutcome ToOutcome() => Status switch
    {
        AudioDecodeStatus.Playable => AudioProcessingOutcome.Playable,
        AudioDecodeStatus.Unplayable => AudioProcessingOutcome.Unplayable,
        _ => AudioProcessingOutcome.Inconclusive
    };
}
