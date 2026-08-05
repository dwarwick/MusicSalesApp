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
    public const string Abandoned = "Abandoned";
}
