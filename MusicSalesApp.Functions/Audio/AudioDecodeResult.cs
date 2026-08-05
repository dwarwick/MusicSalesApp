using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Functions.Audio;

/// <summary>
/// The verdict on one FFmpeg decode.
///
/// <para>
/// The three-way split is the whole point of this type and is load-bearing throughout the pipeline.
/// <see cref="AudioDecodeStatus.Unplayable"/> means the <em>file</em> is bad, so the creator is told
/// and the queue message is finished. <see cref="AudioDecodeStatus.Inconclusive"/> means the
/// <em>decoder</em> could not run — out of disk, out of memory, binary missing — so the message must
/// be retried rather than the upload blamed. Collapsing the two would either quarantine good songs
/// during an infrastructure blip, or retry a genuinely corrupt file until it poisons the queue.
/// </para>
/// </summary>
public enum AudioDecodeStatus
{
    Playable,
    Unplayable,
    Inconclusive
}

/// <inheritdoc cref="AudioDecodeStatus"/>
public sealed class AudioDecodeResult
{
    public AudioDecodeStatus Status { get; private init; }
    public double? Duration { get; private init; }
    public string FailureCode { get; private init; }
    public string Diagnostic { get; private init; }

    public static AudioDecodeResult Playable(double duration) => new()
    {
        Status = AudioDecodeStatus.Playable,
        Duration = duration
    };

    public static AudioDecodeResult Unplayable(string failureCode, string diagnostic) => new()
    {
        Status = AudioDecodeStatus.Unplayable,
        FailureCode = failureCode,
        Diagnostic = diagnostic
    };

    public static AudioDecodeResult Inconclusive(string failureCode, string diagnostic) => new()
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
