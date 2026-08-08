#nullable enable
namespace MusicSalesApp.Services;

/// <summary>
/// Deletes temp files this application abandoned when a previous process died.
/// </summary>
public interface ITempFileCleanupService
{
    /// <summary>Sweeps the temp directory. Never throws - it is a scheduled job, not a request.</summary>
    Task CleanupOrphanedTempFilesAsync();
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Every upload path deletes its own temp files - on completion, on cancel, on navigation, and from
/// <c>DisposeAsync</c> three minutes after a tab closes. All of those need the process to still be
/// alive. An app-pool recycle, a crash or a deploy while a batch is buffered for review skips them
/// all, and nothing else ever looks: contrary to the note on <c>TempFileHelper.TryDelete</c>, Windows
/// has no automatic sweeper for a service account's temp directory, so on shared hosting an orphan
/// stays until somebody deletes it by hand.
/// </para>
///
/// <para>
/// Scheduled daily rather than run at startup, which is the tempting choice because process start is
/// the very event that orphans them. It does not work: a file orphaned by a recycle is minutes old at
/// the next start, so <see cref="MinimumAge"/> - which is what makes sweeping a shared directory safe
/// at all - correctly refuses to touch it. The sweep has to come back later, and "later" is a
/// schedule, not a lifetime event.
/// </para>
/// </remarks>
public sealed class TempFileCleanupService : ITempFileCleanupService
{
    /// <summary>
    /// How stale a file must be before it is assumed abandoned.
    ///
    /// <para>
    /// Generous on purpose. Every producer in this application deletes within one request or one
    /// circuit lifetime, so a day is far beyond any legitimate age - and the margin is what makes it
    /// safe to run against a directory this process may not be the only writer to.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan MinimumAge = TimeSpan.FromDays(1);

    /// <summary>Exactly what <see cref="Path.GetTempFileName"/> produces: "tmp" + 4 hex + ".tmp".</summary>
    private const int TempFileNameLength = 11;

    private readonly ILogger<TempFileCleanupService> _logger;

    public TempFileCleanupService(ILogger<TempFileCleanupService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task CleanupOrphanedTempFilesAsync()
    {
        var result = Sweep(Path.GetTempPath(), DateTime.UtcNow, _logger);

        if (result.Deleted > 0)
        {
            _logger.LogInformation(
                "Swept {Deleted} orphaned temp file(s), reclaiming {Megabytes:F1} MB. "
                + "These were left by a process that ended before its uploads finished.",
                result.Deleted,
                result.BytesReclaimed / (double)(1024 * 1024));
        }

        return Task.CompletedTask;
    }

    /// <param name="utcNow">Passed in rather than read, so the age rule is testable without waiting a day.</param>
    internal static SweepResult Sweep(
        string directory,
        DateTime utcNow,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        var skipped = 0;
        var bytes = 0L;

        try
        {
            // Enumerated rather than listed, and the whole loop is guarded: a temp directory is by
            // definition being written to by other things, so an entry can vanish between being
            // listed and being stat-ed.
            foreach (var path in Directory.EnumerateFiles(directory, "tmp*.tmp"))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    if (!IsTempFileName(Path.GetFileName(path)))
                    {
                        skipped++;
                        continue;
                    }

                    var info = new FileInfo(path);
                    if (!info.Exists || utcNow - info.LastWriteTimeUtc < MinimumAge)
                    {
                        skipped++;
                        continue;
                    }

                    var length = info.Length;
                    info.Delete();

                    deleted++;
                    bytes += length;
                }
                catch (Exception ex)
                {
                    // Almost always a sharing violation - something still holds the file open, which
                    // is itself proof it is not abandoned. Left alone deliberately.
                    skipped++;
                    logger.LogDebug(ex, "Left temp file '{TempPath}' in place.", path);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not sweep orphaned temp files in '{Directory}'.", directory);
        }

        return new SweepResult(deleted, skipped, bytes);
    }

    /// <summary>
    /// True only for the exact shape <see cref="Path.GetTempFileName"/> generates.
    ///
    /// <para>
    /// The glob alone is not enough. A temp directory can be shared with other applications, and
    /// "tmp*.tmp" would also match a deliberately named <c>tmp-nightly-export.tmp</c> belonging to
    /// something else entirely. Nothing is deleted unless its name could only have been generated,
    /// not chosen.
    /// </para>
    /// </summary>
    internal static bool IsTempFileName(string fileName)
    {
        if (fileName.Length != TempFileNameLength
            || !fileName.StartsWith("tmp", StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 3; i < 7; i++)
        {
            if (!Uri.IsHexDigit(fileName[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal readonly record struct SweepResult(int Deleted, int Skipped, long BytesReclaimed);
}
