namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Names shared between the web app, which writes them, and the Azure Function, which reads them.
///
/// <para>
/// These cross a process boundary and a deployment boundary — a queue name that drifts between the
/// two produces no compiler error and no exception, just messages piling up in a queue nobody
/// polls. Both sides reference these constants.
/// </para>
/// </summary>
public static class MediaProcessingQueues
{
    /// <summary>Base name of the queue carrying <c>AudioTranscodeRequest</c> messages.</summary>
    public const string Transcode = "audio-transcode";

    /// <summary>Base name of the queue carrying <c>AudioProbeRequest</c> messages.</summary>
    public const string Probe = "audio-probe";

    /// <summary>
    /// Base name of the queue carrying <c>CoverArtMatchRequest</c> messages — the pre-pairing phase
    /// that decides which staged image belongs to which upload, before any song job exists.
    /// </summary>
    public const string CoverArtMatch = "cover-art-match";

    /// <summary>
    /// Base name of the queue carrying <c>AudioPackageRequest</c> messages — the encrypted-HLS pass.
    ///
    /// <para>
    /// A separate queue from <see cref="Transcode"/> rather than a second step inside it, because
    /// <c>host.json</c> pins <c>batchSize: 1</c> so one instance handles one message: folding
    /// packaging into the transcode would put two full FFmpeg passes under a single 10-minute
    /// Consumption ceiling. It also lets the one-time backfill reuse the same worker for songs that
    /// were transcoded long ago.
    /// </para>
    /// </summary>
    public const string Package = "audio-package";

    /// <summary>
    /// Azure Storage queue names must be lowercase alphanumeric with single hyphens, so the
    /// environment suffix follows the same convention the blob containers already use
    /// (<c>musiccontainer-dev</c>), keeping both readable in the portal side by side.
    /// </summary>
    public const string TestSuffix = "-dev";

    /// <summary>Local-development suffix, matching <c>musiccontainer-local</c>.</summary>
    public const string LocalSuffix = "-local";
}

/// <summary>Staging container names, one per environment, mirroring the media container naming.</summary>
public static class MediaProcessingContainers
{
    public const string StagingProduction = "musicuploads";
    public const string StagingTest = "musicuploads-dev";
    public const string StagingLocal = "musicuploads-local";

    /// <summary>
    /// Where the encrypted HLS segments and their manifests live, one container per environment.
    ///
    /// <para>
    /// <b>Private</b>, like every other container in the product. Its contents are AES-128
    /// ciphertext and useless without a key the API gates; segment URLs carry a container read SAS
    /// the manifest builder stamps on per request. Serving segments straight from storage is what
    /// keeps audio bandwidth off the SmarterASP shared host, and that part is unchanged.
    /// </para>
    /// <para>
    /// It was going to be the one public container in the product, so segment URLs would need no
    /// credential at all. Both storage accounts set <c>allowBlobPublicAccess: false</c>, and that
    /// guardrail is worth more than credential-free URLs — this account holds every song master and
    /// the Data Protection key rings, for Production as well as Test, and the two share it.
    /// </para>
    /// <para>
    /// It sits on the same premium account as <c>musiccontainer{-env}</c>. Never put anything here
    /// that is not encrypted.
    /// </para>
    /// </summary>
    public const string StreamingProduction = "musicstreaming";

    public const string StreamingTest = "musicstreaming-dev";

    public const string StreamingLocal = "musicstreaming-local";
}

/// <summary>
/// Blob names inside a staging job folder. The folder itself is the job's media GUID, so these are
/// fixed leaf names rather than anything derived from the creator's filename.
/// </summary>
public static class MediaProcessingStagingPaths
{
    private const string SourceBaseName = "source";
    private const string CoverBaseName = "cover";
    private const string PlaybackName = "playback.mp3";
    private const string MatchBatchPrefix = "batch";
    private const string LyricsPrefix = "lyrics";
    private const string LyricsTimingsName = "timings.json";
    private const string LyricsLrcName = "lyrics.lrc";

    /// <summary>The raw audio exactly as the creator supplied it.</summary>
    public static string Source(Guid jobId, string extension)
        => $"{Folder(jobId)}/{SourceBaseName}{NormalizeExtension(extension)}";

    /// <summary>The raw cover art. FFmpeg never touches it; it is staged so assembly is one step.</summary>
    public static string Cover(Guid jobId, string extension)
        => $"{Folder(jobId)}/{CoverBaseName}{NormalizeExtension(extension)}";

    /// <summary>The MP3 the Function produces.</summary>
    public static string Playback(Guid jobId)
        => $"{Folder(jobId)}/{PlaybackName}";

    /// <summary>
    /// The job's folder. Formatted "N" to match <see cref="SongMediaPaths"/>, so the staging folder
    /// and the eventual media folder are the same string.
    /// </summary>
    public static string Folder(Guid jobId)
        => jobId.ToString("N", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The folder holding one pre-pairing image batch.
    ///
    /// <para>
    /// Prefixed rather than sitting alongside the job folders, because it is not one: no song exists
    /// yet, and the GUID here is the batch's, not a media GUID. The prefix keeps the two visually
    /// distinct in the portal and stops <c>DeleteStagedBlobsAsync</c>, which deletes by
    /// <c>{guid}/</c>, from ever seeing one.
    /// </para>
    /// </summary>
    public static string MatchBatchFolder(Guid batchId)
        => $"{MatchBatchPrefix}/{batchId.ToString("N", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// One candidate image inside a match batch, named by its position rather than by the creator's
    /// filename — which is unconstrained and has no business in a blob path. The filename travels in
    /// the queue message instead, where it is data rather than a path.
    /// </summary>
    public static string MatchBatchImage(Guid batchId, int index, string extension)
        => $"{MatchBatchFolder(batchId)}/{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + NormalizeExtension(extension);

    /// <summary>
    /// The folder holding one lyrics-alignment attempt's derived output.
    ///
    /// <para>
    /// Prefixed for the same reason <see cref="MatchBatchFolder"/> is, and it matters more here. The
    /// GUID is the <em>attempt's</em>, not a media GUID — a song can be re-aligned any number of
    /// times — and <c>DeleteStagedBlobsAsync</c> deletes by <c>{guid}/</c>. Without the prefix, an
    /// upload job cleaning up after itself could collide with a lyrics folder, and the two GUID
    /// spaces would be indistinguishable in the portal.
    /// </para>
    /// </summary>
    public static string LyricsFolder(Guid jobId)
        => $"{LyricsPrefix}/{jobId.ToString("N", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The word-level timing JSON the Function produces. The primary output.</summary>
    public static string LyricsTimings(Guid jobId)
        => $"{LyricsFolder(jobId)}/{LyricsTimingsName}";

    /// <summary>
    /// The Enhanced LRC rendering of the same timings. Secondary: it costs almost nothing to
    /// produce and gives export and portability for free.
    /// </summary>
    public static string LyricsLrc(Guid jobId)
        => $"{LyricsFolder(jobId)}/{LyricsLrcName}";

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim().ToLowerInvariant();
        return trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }
}

/// <summary>
/// The callback surface the Function posts to. Routes are constants because the Function builds
/// these URLs from configuration at runtime, where a typo would only surface as a 404 in a log.
/// </summary>
public static class MediaProcessingRoutes
{
    public const string ControllerRoute = "api/media-processing";
    public const string Complete = ControllerRoute + "/complete";
    public const string Progress = ControllerRoute + "/progress";

    /// <summary>Where a finished cover-art pairing is posted. Terminal: throws on non-2xx.</summary>
    public const string MatchComplete = ControllerRoute + "/match-complete";

    /// <summary>Where cover-art matching progress is posted. Cosmetic: never throws.</summary>
    public const string MatchProgress = ControllerRoute + "/match-progress";

    /// <summary>
    /// Where a finished encrypted-HLS packaging run is posted. Terminal: throws on non-2xx.
    ///
    /// <para>
    /// This is the only callback that carries a secret. The Function generates the AES key because
    /// it is the process running FFmpeg, and it has no database to write it to — so the key travels
    /// home over HTTPS in the body, and the web app protects it at rest. Nothing about this route
    /// may ever be logged at a level that would render the body.
    /// </para>
    /// </summary>
    public const string PackageResult = ControllerRoute + "/package-result";

    /// <summary>
    /// Where a finished lyrics alignment is posted. Terminal: throws on non-2xx.
    ///
    /// <para>
    /// Shares this controller and this header with the audio pipeline even though the caller is a
    /// different Function app in a different language. Both are the same kind of caller — a trusted
    /// background worker authorised to write to the catalogue — so a second controller would only
    /// duplicate the auth wiring, and a second secret would only be a second thing to rotate.
    /// </para>
    /// </summary>
    public const string LyricsComplete = ControllerRoute + "/lyrics-complete";

    /// <summary>Where lyrics-alignment progress is posted. Cosmetic: never throws.</summary>
    public const string LyricsProgress = ControllerRoute + "/lyrics-progress";

    /// <summary>
    /// Header carrying the shared secret. Deliberately not <c>X-Api-Key</c>: that header is the
    /// mobile app's, and its key ships inside a distributed binary. This one authorises writes to
    /// the song catalog and must be a separate secret with a separate name.
    /// </summary>
    public const string ApiKeyHeaderName = "X-Media-Processing-Key";
}

/// <summary>
/// The time budget for one terminal callback, split across the two processes that share it.
///
/// <para>
/// These have to agree. The Function posts <c>complete</c> and waits for the site to finish
/// assembling the song synchronously — copying blobs across storage accounts, writing the metadata
/// row and generating the artwork renditions. If the Function stops waiting before the site is
/// done, its request is abandoned mid-assembly, the queue redelivers, and assembly runs a second
/// time on top of a first attempt that is still in flight. Keeping the two numbers in one place is
/// what stops that ordering from drifting apart across two deployments.
/// </para>
/// </summary>
public static class MediaProcessingTimeouts
{
    /// <summary>
    /// What the site allows itself for the whole assembly. Server-side blob copies of a single MP3
    /// finish in seconds even across accounts, so this is generous headroom rather than an expected
    /// duration — and it has to stay comfortably below <see cref="TerminalCallback"/>.
    /// </summary>
    public static readonly TimeSpan Assembly = TimeSpan.FromMinutes(4);

    /// <summary>
    /// What the Function waits for that assembly, with a minute of margin over
    /// <see cref="Assembly"/> for request transit and shared-hosting cold start. Still well inside
    /// the 10-minute Consumption execution ceiling, so a hung site surfaces as a failed callback the
    /// queue retries rather than as a killed invocation.
    /// </summary>
    public static readonly TimeSpan TerminalCallback = TimeSpan.FromMinutes(5);

    /// <summary>
    /// What the Function waits for a progress ping. Short on purpose: progress is decoration, and a
    /// site that has stopped answering must not cost a transcode several minutes per step.
    /// </summary>
    public static readonly TimeSpan ProgressCallback = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the upload page waits for a cover-art pairing before giving up and matching on
    /// filenames itself.
    ///
    /// <para>
    /// Unlike the timeouts above this one is not split across two processes — it bounds a wait the
    /// creator is actively watching. Generous enough for a full batch of vision calls, and well
    /// inside the 10-minute Consumption ceiling so a Function that is merely slow still wins the
    /// race. Expiring is not an error: the page falls back to exact base-name matching, which is
    /// what it did before OpenAI was wired in at all.
    /// </para>
    /// </summary>
    public static readonly TimeSpan CoverArtMatch = TimeSpan.FromMinutes(3);
}

/// <summary>
/// Failure codes recorded on a job and shown to the creator. Constants because the Function writes
/// them and the web app matches on them to decide retry-versus-report.
/// </summary>
public static class MediaProcessingFailureCodes
{
    public const string SourceBlobMissing = "SourceBlobMissing";
    public const string NotDecodable = "NotDecodable";
    public const string TranscodeFailed = "TranscodeFailed";
    public const string InvalidMp3Container = "InvalidMp3Container";
    public const string ZeroDuration = "ZeroDuration";
    public const string DecoderUnavailable = "DecoderUnavailable";
    public const string DecoderTimeout = "DecoderTimeout";

    /// <summary>FFmpeg ran and failed while segmenting/encrypting. The source file is the suspect.</summary>
    public const string PackagingFailed = "PackagingFailed";

    /// <summary>
    /// FFmpeg reported success but produced no manifest, or a manifest listing no segments. Treated
    /// as a failure of the file rather than of the decoder: a zero-segment package would publish a
    /// song that silently plays nothing.
    /// </summary>
    public const string PackagingProducedNothing = "PackagingProducedNothing";

    /// <summary>
    /// The reconciler swept a job that stopped reporting progress, or its enqueue never landed.
    /// A guess, made from a timestamp - distinct from <see cref="PoisonedAfterRetries"/>, which is
    /// Azure telling us for certain.
    /// </summary>
    public const string Abandoned = "Abandoned";

    /// <summary>
    /// The queue message exhausted <c>maxDequeueCount</c> and landed in the poison queue, so no
    /// further attempt will ever be made.
    ///
    /// <para>
    /// Deliberately not folded into <see cref="Abandoned"/>. This one is authoritative and arrives
    /// as an event; that one is inferred from a stale timestamp and can be wrong. Keeping them apart
    /// is what makes it possible to tell, from a failed job alone, whether the pipeline really gave
    /// up or the reconciler merely lost sight of it.
    /// </para>
    /// </summary>
    public const string PoisonedAfterRetries = "PoisonedAfterRetries";
}
