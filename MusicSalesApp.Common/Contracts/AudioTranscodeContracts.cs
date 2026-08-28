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

    /// <summary>
    /// Path within the staging container of the raw cover art, e.g. <c>{guid}/cover.jpg</c>.
    /// Null when the creator supplied none, and null on messages queued before renditions moved
    /// into the Function - both mean "skip the image work", so no queue drain was needed.
    /// </summary>
    public string CoverArtBlobPath { get; set; }

    /// <summary>
    /// Lowercase, dot-prefixed, e.g. <c>.jpg</c>.
    ///
    /// <para>
    /// The Function needs this to derive where the renditions go, because their paths hang off the
    /// cover art's eventual path in the media container. It derives that through the same
    /// <c>SongMediaPaths.CoverArt(JobId, …)</c> helper the API uses when it copies the master in -
    /// deliberately not a second path computed by the web app and passed along, because the two
    /// would have to stay byte-identical for the public media whitelist to resolve a rendition back
    /// to its master.
    /// </para>
    /// </summary>
    public string CoverArtExtension { get; set; }
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

    /// <summary>
    /// Rendition widths the Function wrote into the media container, ascending.
    ///
    /// <para>
    /// The null/empty distinction is what the API branches on. <see langword="null"/> means there
    /// was no cover art at all, so the song's recorded width set stays null. <b>Empty</b> means art
    /// was present but no rendition could be produced - the song still publishes and serves its
    /// full-size master, which is the behaviour that predates renditions entirely.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> CoverArtVariantWidths { get; set; }

    /// <summary>Pixel width of the cover art as decoded. Null when absent or undecodable.</summary>
    public int? CoverArtWidth { get; set; }

    /// <summary>Pixel height of the cover art as decoded. Null when absent or undecodable.</summary>
    public int? CoverArtHeight { get; set; }

    /// <summary>
    /// Why no widths were produced - a constant from <c>ImageVariantFailureCodes</c>. Diagnostic
    /// only: never shown to the creator, and never a reason to fail the job. Renditions are derived
    /// data the admin backfill can rebuild at any time.
    /// </summary>
    public string CoverArtDiagnosticCode { get; set; }
}

/// <summary>
/// The queue message that asks the Function to package one already-published song as encrypted HLS.
///
/// <para>
/// Keyed on <see cref="SongMetadataId"/> rather than a media GUID, because this runs against songs
/// that already exist — including pre-July-2026 ones that have no media GUID at all. The row id is
/// the only identifier every song is guaranteed to have.
/// </para>
/// </summary>
public sealed class AudioPackageRequest
{
    public int SongMetadataId { get; set; }

    /// <summary>
    /// Path within the <em>media</em> container of the audio to package. The web app picks this:
    /// the retained original when one exists and differs from the playback MP3, so packaging costs
    /// one generation of loss rather than two, and the playback MP3 otherwise.
    /// </summary>
    public string SourceBlobPath { get; set; }

    /// <summary>
    /// The folder the Function writes into, inside the streaming container. Minted by the web app so
    /// a redelivered message writes to the same place twice instead of orphaning a folder per
    /// attempt, and re-minted per <em>run</em> so a repackage never overwrites a live package in
    /// place. See <c>SongMediaPaths.HlsManifest</c>.
    /// </summary>
    public Guid HlsStreamId { get; set; }

    /// <summary>
    /// Which backfill or repair run asked for this, echoed back so a run can tell when it is done.
    /// Null for the ordinary post-upload path, where nobody is counting.
    /// </summary>
    public int? BackfillRunId { get; set; }
}

/// <summary>
/// The terminal callback the Function POSTs once packaging has finished, successfully or not.
///
/// <para>
/// <b>This payload contains the content encryption key in the clear.</b> It is safe only because it
/// travels over HTTPS to a route gated by <c>X-Media-Processing-Key</c>. Do not log it, do not add
/// it to a diagnostic dump, and do not persist <see cref="KeyHex"/> unprotected.
/// </para>
/// </summary>
public sealed class AudioPackageResult
{
    public int SongMetadataId { get; set; }

    /// <summary>Echoed from the request, so a late redelivery cannot adopt a newer run's folder.</summary>
    public Guid HlsStreamId { get; set; }

    public int? BackfillRunId { get; set; }

    /// <summary>The 16-byte AES-128 content key, lowercase hex. Null on failure.</summary>
    public string KeyHex { get; set; }

    /// <summary>The 16-byte initialisation vector, lowercase hex. Null on failure.</summary>
    public string IvHex { get; set; }

    /// <summary>How many <c>.ts</c> segments the manifest lists. Zero is a failure, not a success.</summary>
    public int SegmentCount { get; set; }

    /// <summary>
    /// The manifest's <c>#EXT-X-TARGETDURATION</c>, in seconds. The API needs it to decide how many
    /// segments make up a free preview without re-parsing every <c>#EXTINF</c>.
    /// </summary>
    public double TargetDurationSeconds { get; set; }

    /// <summary>
    /// How long the Function spent on this song: download, FFmpeg, and the segment uploads.
    ///
    /// <para>
    /// The honest unit for estimating a large backfill. A run's wall-clock time is not, because
    /// songs are packaged concurrently - a ten-song run where all ten were dispatched at once takes
    /// about as long as one song, and extrapolating from that would badly underestimate five
    /// hundred.
    /// </para>
    /// </summary>
    public double ProcessingSeconds { get; set; }

    /// <summary>Total playable duration of the package, for cross-checking against TrackLength.</summary>
    public double? DurationSeconds { get; set; }

    public AudioProcessingOutcome Outcome { get; set; }

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
