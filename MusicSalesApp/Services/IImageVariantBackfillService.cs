#nullable enable
using Hangfire;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// The one-off pass that generates pre-resized WebP renditions for images already in storage.
///
/// <para>
/// Scoped to the environment it runs in: the containers come from this process's
/// <see cref="AzureStorageOptions"/>, so running it locally touches only the local containers, and
/// production is a separate, deliberate run. It never writes to the <c>backup-*</c> containers -
/// <see cref="IBlobContainerFactory"/> gives it no way to address them. Propagate the results with
/// a storage backup afterwards.
/// </para>
/// </summary>
public interface IImageVariantBackfillService
{
    /// <summary>
    /// Queues a run, or throws <see cref="InvalidOperationException"/> if one is already active.
    /// </summary>
    Task<ImageVariantBackfillRun> StartAsync(
        ImageVariantBackfillScope scope,
        bool dryRun,
        bool regenerateExisting,
        bool removeLegacyPngSharingImages,
        int? initiatedByUserId,
        string? initiatedByEmail);

    /// <summary>Recent runs, newest first, for the admin history table.</summary>
    Task<List<ImageVariantBackfillRun>> GetRunsAsync();

    /// <summary>One run with its failure rows.</summary>
    Task<ImageVariantBackfillRun?> GetRunAsync(int runId);

    /// <summary>The queued or running run, if any.</summary>
    Task<ImageVariantBackfillRun?> GetActiveRunAsync();

    /// <summary>
    /// Asks the running job to stop. Cooperative - the job checks between items, so an encode
    /// already in flight finishes first.
    /// </summary>
    Task RequestCancellationAsync(int runId);

    /// <summary>
    /// The containers this environment will write to, so the admin page can show an operator which
    /// environment they are about to act on before they start a run.
    /// </summary>
    IReadOnlyList<string> GetTargetContainerNames();

    /// <summary>
    /// The Hangfire entry point. Idempotent: on re-execution after a process recycle it re-queries
    /// for work still outstanding and carries on, which is why there is no per-item resume state.
    /// </summary>
    [AutomaticRetry(Attempts = 2)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    Task RunAsync(int runId);
}
