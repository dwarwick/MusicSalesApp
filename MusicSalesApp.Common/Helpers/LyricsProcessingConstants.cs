namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Where the lyrics-alignment orchestration is started.
///
/// <para>
/// A constant because the web app builds this URL from configuration at runtime and the Python app
/// declares it as a trigger route, so a typo would surface as a 404 in a log rather than as a build
/// error. The two sides cannot share a compiled constant — see the drift note on
/// <see cref="LyricsAlignmentFailureCodes"/>.
/// </para>
/// </summary>
public static class LyricsStarterRoutes
{
    /// <summary>
    /// The HTTP starter's route, relative to the Function app's base URL. Azure prefixes
    /// <c>api/</c> by default, so the Python trigger declares <c>route="lyrics/align"</c> and this
    /// is what the web app POSTs to.
    /// </summary>
    public const string Start = "api/lyrics/align";

    /// <summary>
    /// Header carrying the Function app's key. Azure's own name, not ours — the platform reads it
    /// before any of our code runs.
    /// </summary>
    public const string FunctionKeyHeaderName = "x-functions-key";
}

/// <summary>
/// The time budget for one lyrics terminal callback, split across the two processes that share it.
///
/// <para>
/// Same shape and the same hazard as <see cref="MediaProcessingTimeouts"/>: the Function posts its
/// result and waits while the site assembles synchronously, so if the Function stops waiting first
/// its request is abandoned mid-assembly and the retry runs assembly a second time on top of one
/// still in flight. The numbers are much smaller than the audio pair's because the work is much
/// smaller — two blobs of a few tens of kilobytes and one row, against a 150 MB MP3.
/// </para>
/// </summary>
public static class LyricsProcessingTimeouts
{
    /// <summary>
    /// What the site allows itself to copy the timing artifacts across accounts and write the row.
    /// Must stay comfortably below <see cref="TerminalCallback"/>.
    /// </summary>
    public static readonly TimeSpan Assembly = TimeSpan.FromSeconds(90);

    /// <summary>
    /// What the Function waits for that assembly, with margin for request transit and shared-hosting
    /// cold start.
    /// </summary>
    public static readonly TimeSpan TerminalCallback = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long the web app waits when asking Azure for an orchestration's runtime status. Short:
    /// the reconciler sweeps a batch, and one unreachable instance must not stall the rest. Failing
    /// to get an answer is not a verdict — the job is simply left for the next sweep.
    /// </summary>
    public static readonly TimeSpan StatusQuery = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Progress pings reuse <see cref="MediaProcessingTimeouts.ProgressCallback"/> rather than
    /// declaring their own. There is no reason for the two pipelines to disagree about how long a
    /// cosmetic update is worth waiting for, and a second copy would only be a second thing to
    /// change.
    /// </summary>
    public static TimeSpan ProgressCallback => MediaProcessingTimeouts.ProgressCallback;
}

/// <summary>
/// What the creator may paste. Enforced server-side in <c>SongLyricsService</c> and shown as a
/// counter in the editor dialog.
/// </summary>
public static class LyricsTextLimits
{
    /// <summary>
    /// Generous enough for any real song — a wordy eight-minute track runs to about 4,000 characters
    /// — while still bounding what gets written to a blob and shipped through an orchestration input.
    /// </summary>
    public const int MaxCharacters = 20_000;

    /// <summary>
    /// Bounds the line-mapping work and catches a pasted document rather than a lyric sheet.
    /// </summary>
    public const int MaxLines = 500;
}

/// <summary>
/// Failure codes recorded on a <c>LyricsAlignmentJob</c> and shown to the creator. Constants because
/// the Python app writes them and the web app matches on them.
///
/// <para>
/// <b>Drift warning, and it is a new one for this repository.</b> Every other cross-process constant
/// here is shared by <em>compilation</em> — the C# Function app and the web app reference this
/// assembly, so a mismatch cannot happen. The Python app cannot, so it carries a hand-maintained
/// copy. A pytest fixture asserts the two agree; if you add a code here, add it there.
/// </para>
/// </summary>
public static class LyricsAlignmentFailureCodes
{
    /// <summary>Nothing was pasted, or it normalised away to nothing.</summary>
    public const string LyricsTextEmpty = "LyricsTextEmpty";

    /// <summary>Past <see cref="LyricsTextLimits"/>. Rejected before any orchestration starts.</summary>
    public const string LyricsTextTooLong = "LyricsTextTooLong";

    /// <summary>The song's playback blob could not be read. The song may have been deleted mid-run.</summary>
    public const string AudioBlobMissing = "AudioBlobMissing";

    /// <summary>The lyrics blob the request pointed at could not be read.</summary>
    public const string LyricsBlobMissing = "LyricsBlobMissing";

    /// <summary>FFmpeg could not produce the analysis-ready WAV.</summary>
    public const string PreparationFailed = "PreparationFailed";

    /// <summary>Demucs failed. Usually memory, occasionally a track it cannot make sense of.</summary>
    public const string SeparationFailed = "SeparationFailed";

    /// <summary>The aligner ran but produced nothing usable.</summary>
    public const string AlignmentFailed = "AlignmentFailed";

    /// <summary>
    /// The aligner produced tokens, but none of them matched the artist's text. Almost always the
    /// wrong lyrics pasted against the wrong song, which is worth saying plainly.
    /// </summary>
    public const string NoTokensMatched = "NoTokensMatched";

    /// <summary>A word started before the one before it. Structurally broken, not merely imprecise.</summary>
    public const string TimingsNotMonotonic = "TimingsNotMonotonic";

    /// <summary>Words were placed past the end of the track.</summary>
    public const string TimingsExceedDuration = "TimingsExceedDuration";

    /// <summary>
    /// The orchestration threw and reported it through its own except path. Authoritative and
    /// immediate — deliberately distinct from <see cref="Abandoned"/>, which is inferred.
    /// </summary>
    public const string OrchestrationFailed = "OrchestrationFailed";

    /// <summary>Someone cancelled the run. Not a fault; the creator asked for it.</summary>
    public const string OrchestrationTerminated = "OrchestrationTerminated";

    /// <summary>
    /// The HTTP starter could not be reached even after Hangfire exhausted its retries, so no
    /// orchestration was ever created. Distinct from every code above, all of which imply one ran.
    /// </summary>
    public const string StarterUnreachable = "StarterUnreachable";

    /// <summary>
    /// The reconciler gave up on a job it could get no answer about — the orchestration is not
    /// reachable and the timestamp is long stale. A guess, and the only one in this list.
    /// </summary>
    public const string Abandoned = "Abandoned";
}
