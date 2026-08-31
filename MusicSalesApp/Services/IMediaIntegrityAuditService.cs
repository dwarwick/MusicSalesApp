using Hangfire;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public interface IMediaIntegrityAuditService
{
    Task<MediaIntegrityAuditRun> StartAsync(
        MediaAuditMode mode,
        int? initiatedByUserId,
        string initiatedByEmail,
        int? sourceRunId = null);

    // Hangfire resolves filters from Job.Method, which for an interface-registered job is
    // this declaration. The same attribute on the implementation is silently ignored.
    [AutomaticRetry(Attempts = 2)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    Task RunAsync(int runId);
    Task<List<MediaIntegrityAuditRun>> GetRunsAsync();
    Task<MediaIntegrityAuditRun> GetRunAsync(int runId);

    /// <summary>
    /// Records one song's verdict, applies the run's repair/quarantine mode, and completes the run
    /// once every candidate has reported.
    ///
    /// <para>
    /// Decoding happens in the Azure Function now, so a run no longer finishes when its Hangfire
    /// job returns - it finishes on the <em>last callback</em>. This method is where that is
    /// detected.
    /// </para>
    /// </summary>
    Task RecordProbedItemAsync(
        MediaIntegrityAuditItem item,
        MediaAuditMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts a candidate that can no longer be judged - the song was deleted between dispatch and
    /// its result arriving. Without this the run would never reach its candidate count.
    /// </summary>
    Task RecordSkippedItemAsync(int runId, int songMetadataId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes out runs whose probes stopped arriving, so a lost queue message cannot leave a run
    /// Running forever and block the next one via the single-run lock.
    /// </summary>
    Task ReconcileStalledRunsAsync(CancellationToken cancellationToken = default);
}
