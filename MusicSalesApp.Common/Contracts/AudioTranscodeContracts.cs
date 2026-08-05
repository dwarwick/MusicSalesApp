namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// How an FFmpeg decode ended. The three-way split is load-bearing rather than a convenience:
/// <see cref="Unplayable"/> means the file is bad and the creator must be told, while
/// <see cref="Inconclusive"/> means the decoder itself could not run (out of disk, out of memory,
/// binary missing) and the message must be retried instead of blaming the upload.
/// </summary>
public enum AudioProcessingOutcome
{
    Playable,
    Unplayable,
    Inconclusive
}

/// <summary>
/// The queue message that asks the Function to turn one staged upload into a playback MP3.
///
/// <para>
/// <see cref="JobId"/> is the song's media GUID, minted by the web app when the job row is created.
/// It names the staging folder and later names the folder in the media container, so there is one
/// identifier end to end rather than a separate correlation key.
/// </para>
/// </summary>
public sealed class AudioTranscodeRequest
{
    public Guid JobId { get; set; }

    /// <summary>Path within the staging container, e.g. <c>{guid}/source.wav</c>.</summary>
    public string SourceBlobPath { get; set; }

    /// <summary>The creator's own filename. Diagnostics only — it never reaches storage.</summary>
    public string SourceFileName { get; set; }

    /// <summary>Lowercase, dot-prefixed, e.g. <c>.wav</c>.</summary>
    public string SourceExtension { get; set; }

    /// <summary>Where the Function writes <c>{guid}/playback.mp3</c>.</summary>
    public string PlaybackBlobPath { get; set; }
}

/// <summary>
/// The terminal callback the Function POSTs once transcoding has finished, successfully or not.
/// </summary>
public sealed class AudioTranscodeResult
{
    public Guid JobId { get; set; }

    /// <summary>Path within the staging container of the MP3 the Function produced.</summary>
    public string PlaybackBlobPath { get; set; }

    public double? DurationSeconds { get; set; }

    public AudioProcessingOutcome Outcome { get; set; }

    /// <summary>
    /// True when the creator uploaded an MP3, so no transcode ran and the source is also the
    /// playback copy. The API uses this to store one blob instead of two.
    /// </summary>
    public bool SourceWasAlreadyMp3 { get; set; }

    public string FailureCode { get; set; }

    public string Diagnostic { get; set; }
}

/// <summary>Which maintenance job a probe belongs to, so its result can be routed back.</summary>
public enum AudioProbeKind
{
    MediaIntegrityAudit,
    TrackLengthRepair
}

/// <summary>
/// The queue message that asks the Function to decode an already-stored playback blob without
/// producing anything — used by the media-integrity audit and the track-length repair job.
/// </summary>
public sealed class AudioProbeRequest
{
    public Guid ProbeId { get; set; }

    public AudioProbeKind Kind { get; set; }

    /// <summary>Set for <see cref="AudioProbeKind.MediaIntegrityAudit"/>.</summary>
    public int? AuditRunId { get; set; }

    public int SongMetadataId { get; set; }

    /// <summary>Path within the <em>media</em> container, not staging.</summary>
    public string BlobPath { get; set; }

    /// <summary>
    /// 1 for the first look, 2 for a confirmation pass.
    ///
    /// <para>
    /// The audit never condemns a song on a single failed decode: a first "unplayable" verdict is
    /// re-probed against a fresh Azure stream, and only a second failure is recorded. A transient
    /// read fault would otherwise quarantine a perfectly good song.
    /// </para>
    /// </summary>
    public int Attempt { get; set; } = 1;
}

/// <summary>
/// The result of a probe.
///
/// <para>
/// Deliberately raw: blob facts and decode facts only. Turning these into a
/// <c>MediaAuditOutcome</c> (Healthy / MetadataRepairable / ConfirmedUnplayable / …) is a business
/// decision that stays in the web app, so the audit's rules can change without redeploying the
/// Function.
/// </para>
/// </summary>
public sealed class AudioProbeResult
{
    public Guid ProbeId { get; set; }

    public AudioProbeKind Kind { get; set; }

    public int? AuditRunId { get; set; }

    public int SongMetadataId { get; set; }

    public string BlobPath { get; set; }

    /// <summary>Echoed back from the request so the confirmation pass can be recognised.</summary>
    public int Attempt { get; set; } = 1;

    public bool BlobExists { get; set; }

    public long? BlobLength { get; set; }

    public string ETag { get; set; }

    public string ContentType { get; set; }

    public DateTimeOffset? BlobLastModified { get; set; }

    public string DetectedFormat { get; set; }

    public double? DurationSeconds { get; set; }

    public AudioProcessingOutcome Outcome { get; set; }

    public string FailureCode { get; set; }

    public string Diagnostic { get; set; }
}

/// <summary>
/// A fire-and-forget progress ping from the Function, or from the API's own assembly stages.
///
/// <para>
/// Never treated as important: the sender swallows failures, and the receiver drops anything for an
/// unknown or already-finished job. Losing one of these must cost nothing more than a slightly
/// jumpier progress bar.
/// </para>
/// </summary>
public sealed class AudioProcessingProgress
{
    public Guid JobId { get; set; }

    public AudioProcessingStep Step { get; set; }

    /// <summary>Progress within <see cref="Step"/>, 0-100. Null for steps that report no detail.</summary>
    public double? StepPercent { get; set; }

    /// <summary>
    /// Overall 0-100, computed by <see cref="AudioProcessingProgressCalculator"/> at the sender so
    /// every hop agrees on one number.
    /// </summary>
    public double OverallPercent { get; set; }

    /// <summary>Optional short human-readable note shown beside the bar.</summary>
    public string Detail { get; set; }
}
