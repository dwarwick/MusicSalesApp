using Hangfire;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Incremental backup of the configured Azure Blob containers into "backup-{container}" containers,
/// and restore back out of them. Both directions run as Hangfire jobs and share a single active-run
/// lock, so a backup and a restore can never overlap.
/// </summary>
public interface IStorageBackupService
{
    /// <summary>Queues a backup of every configured container.</summary>
    /// <param name="forceFullCopy">Re-copy every blob, including ones that appear unchanged.</param>
    Task<StorageBackupRun> StartBackupAsync(int? initiatedByUserId, string initiatedByEmail, bool forceFullCopy);

    /// <summary>Queues a restore of the selected containers out of their backup containers.</summary>
    Task<StorageBackupRun> StartRestoreAsync(
        int? initiatedByUserId,
        string initiatedByEmail,
        IReadOnlyCollection<string> sourceContainerNames,
        StorageRestoreScope scope,
        bool overwriteNewerLive);

    /// <summary>Executes a queued run. Invoked by Hangfire; safe to re-enter after a process recycle.</summary>
    // Hangfire resolves filters from Job.Method, which for an interface-registered job is
    // this declaration. The same attribute on the implementation is silently ignored.
    [AutomaticRetry(Attempts = 2)]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    Task RunAsync(int runId);

    /// <summary>Entry point for the nightly recurring backup.</summary>
    Task RunRecurringBackupAsync();

    Task<List<StorageBackupRun>> GetRunsAsync();

    Task<StorageBackupRun> GetRunAsync(int runId);

    /// <summary>The queued or running run, or null. Polled by the admin page to drive progress bars.</summary>
    Task<StorageBackupRun> GetActiveRunAsync();

    Task RequestCancellationAsync(int runId);

    /// <summary>The live containers in scope for backup, in configuration order.</summary>
    IReadOnlyList<string> GetConfiguredContainerNames();
}
