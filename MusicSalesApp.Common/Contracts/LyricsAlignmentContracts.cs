using System.Text.Json.Serialization;

namespace MusicSalesApp.Common.Contracts;

/// <summary>
/// How one alignment attempt ended, as reported by the Function.
///
/// <para>
/// Three-way for the same load-bearing reason as <see cref="AudioProcessingOutcome"/>:
/// <see cref="Unusable"/> means the <em>input</em> was bad — empty text, nothing matched — and the
/// creator must be told, while <see cref="Inconclusive"/> means the <em>pipeline</em> could not run
/// (model weights unreachable, out of memory, ffmpeg missing) and the attempt deserves another
/// chance rather than blaming the artist's lyrics. Collapsing the two either condemns good
/// submissions or burns a fresh 40-minute separation run on one that can never work.
/// </para>
///
/// <para>
/// Note what is <b>not</b> here: any notion of "good enough". Confidence is reported as a number and
/// judged server-side against an admin-tunable threshold, because the Function reports facts and the
/// web app decides — the same split the probe path already uses.
/// </para>
///
/// <para>
/// <b>Serialised by name, and the attribute is load-bearing rather than stylistic.</b> Every other
/// contract in this assembly is exchanged between two C# processes, where
/// <c>JsonSerializerDefaults.Web</c> writes an enum as a number and reads it back as one. This one
/// is sent by a <em>Python</em> app, which has no C# enum to number and sends the name - and
/// <c>JsonSerializerDefaults.Web</c> does <b>not</b> accept a name without this converter. Without
/// it every successful alignment fails model binding on arrival, is retried five times, and is
/// finally recorded as a failure: the pipeline works perfectly and reports that it did not.
/// </para>
///
/// <para>
/// Scoped to this type rather than registered globally, so it cannot change how any other API on
/// the site serialises. Note the deliberate asymmetry with <see cref="LyricsAlignmentStep"/>, which
/// stays numeric: that one's whole contract is that its ordinals are compared with <c>&gt;</c>.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LyricsAlignmentOutcome>))]
public enum LyricsAlignmentOutcome
{
    Aligned,
    Unusable,
    Inconclusive
}

/// <summary>
/// The orchestration input, POSTed to the Function app's HTTP starter and persisted verbatim as
/// <c>DurableFunctionTask.InputJson</c>.
///
/// <para>
/// <see cref="JobId"/> is the attempt's own GUID, minted when the <c>LyricsAlignmentJob</c> row is
/// created. It is <em>not</em> the song's media GUID: a song can be re-aligned any number of times,
/// so the correlation key has to be per attempt. It names the staging folder and rides every
/// callback.
/// </para>
/// </summary>
public sealed class LyricsAlignmentRequest
{
    public Guid JobId { get; set; }

    public int SongMetadataId { get; set; }

    /// <summary>Carried so progress can be broadcast to the right creator's SignalR group.</summary>
    public int CreatorId { get; set; }

    /// <summary>
    /// Path within the <b>media</b> container of the playback MP3 to align against.
    /// Container-relative, like every other path in this codebase — the container name comes from
    /// the Function app's own settings.
    /// </summary>
    public string Mp3BlobPath { get; set; }

    /// <summary>
    /// Path within the <b>media</b> container of the lyrics the creator pasted. Written by the web
    /// app before this request is sent, because it is creator content rather than a derived
    /// artifact, and the web app is the sole writer of primary blobs.
    /// </summary>
    public string LyricsBlobPath { get; set; }

    public DateTime RequestedUtc { get; set; }
}

/// <summary>
/// The terminal callback the Function POSTs once an attempt has finished, successfully or not.
///
/// <para>
/// Everything below the outcome is a <b>measurement</b>, not a verdict. The web app combines them
/// into a published/needs-review/failed decision in one place it can unit-test without a Function, a
/// Python runtime or a storage account, and can change that decision without redeploying the slowest
/// component in the system.
/// </para>
/// </summary>
public sealed class LyricsAlignmentResult
{
    public Guid JobId { get; set; }

    public LyricsAlignmentOutcome Outcome { get; set; }

    /// <summary>Path within the staging container of the timing JSON. Null unless aligned.</summary>
    public string TimingsBlobPath { get; set; }

    /// <summary>Path within the staging container of the Enhanced LRC. Null unless aligned.</summary>
    public string LrcBlobPath { get; set; }

    /// <summary>
    /// The Function's composite score, 0-1: mean aligner confidence tempered by how much of the
    /// artist's text had to be interpolated rather than matched. Reported, not acted on.
    /// </summary>
    public double? Confidence { get; set; }

    /// <summary>Mean of the per-token scores the aligner itself produced, 0-1.</summary>
    public double? MeanAlignerConfidence { get; set; }

    /// <summary>How many tokens the artist's lyrics contained after normalisation.</summary>
    public int LyricTokenCount { get; set; }

    /// <summary>How many of those were matched to a token the aligner recognised.</summary>
    public int MatchedTokenCount { get; set; }

    /// <summary>How many had to be given a time by interpolating between matched neighbours.</summary>
    public int InterpolatedTokenCount { get; set; }

    /// <summary>Aligner tokens with no counterpart in the artist's text — ad-libs, mostly.</summary>
    public int DroppedAlignerTokenCount { get; set; }

    /// <summary>
    /// Lyric words that had to be pushed out of a detected instrumental window. A handful is normal;
    /// a lot means the alignment has smeared across a solo, which is the classic drift failure.
    /// </summary>
    public int SilenceViolationCount { get; set; }

    public int LineCount { get; set; }

    /// <summary>Lines that ended up with a start and an end. Short of <see cref="LineCount"/> is bad.</summary>
    public int LinesWithTimingCount { get; set; }

    /// <summary>
    /// Whether every word's start is at or after the previous word's. False means the output is
    /// structurally broken rather than merely imprecise, and no amount of reviewing will fix it.
    /// </summary>
    public bool IsMonotonic { get; set; }

    /// <summary>End of the last timed word, in milliseconds.</summary>
    public long LastWordEndMs { get; set; }

    /// <summary>Track duration as ffprobe measured it, in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>A constant from <c>LyricsAlignmentFailureCodes</c>. Null on success.</summary>
    public string FailureCode { get; set; }

    public string Diagnostic { get; set; }
}

/// <summary>
/// A cosmetic progress ping. Posted best-effort from inside the activities — never from the
/// orchestrator, where each call would become a durable history entry and a storage transaction.
/// </summary>
public sealed class LyricsAlignmentProgress
{
    public Guid JobId { get; set; }

    public LyricsAlignmentStep Step { get; set; }

    /// <summary>0-100 within the step. Null for steps with no meaningful sub-progress.</summary>
    public double? StepPercent { get; set; }

    /// <summary>
    /// Computed at the sender via <see cref="LyricsAlignmentProgressCalculator"/> so every hop
    /// agrees on one number rather than each recomputing it from its own copy of the band table.
    /// </summary>
    public double OverallPercent { get; set; }

    public string Detail { get; set; }
}
