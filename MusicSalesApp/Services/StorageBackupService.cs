using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Orchestrates incremental blob backup and restore.
/// <para>
/// Change detection keys off the source blob's ETag, stamped into the backup blob's metadata at
/// copy time, so a run is two container listings and an in-memory diff — no per-blob HEAD requests.
/// All copies are server-side, so no blob content passes through the web server.
/// </para>
/// <para>
/// The job is idempotent by construction: on re-execution after a process recycle it re-lists,
/// re-diffs, and skips everything already copied. That is why there is no per-blob resume state.
/// </para>
/// </summary>
public sealed class StorageBackupService : IStorageBackupService
{
    private const int ProgressFlushInterval = 25;

    /// <summary>
    /// The longest a container's progress row may go without an update while copying. The admin page
    /// polls every two seconds, and <see cref="ProgressFlushInterval"/> on its own ties the bar to
    /// throughput - a container holding fewer blobs than the interval, or one whose copies are slow,
    /// would show nothing at all until it completed.
    /// </summary>
    private static readonly TimeSpan ProgressFlushWindow = TimeSpan.FromSeconds(1);

    private const int ConsecutiveFailureAbortThreshold = 50;
    private const int DefaultMaxParallelCopies = 4;
    private const int MinParallelCopies = 1;
    private const int MaxParallelCopies = 16;
    private const int RunRetentionDays = 90;
    private static readonly TimeSpan SasLifetime = TimeSpan.FromHours(8);

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IStorageBackupBlobGateway _gateway;
    private readonly IOptions<AzureStorageOptions> _storageOptions;
    private readonly IConfiguration _configuration;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<StorageBackupService> _logger;
    private readonly TimeProvider _time;

    public StorageBackupService(
        IDbContextFactory<AppDbContext> contextFactory,
        IStorageBackupBlobGateway gateway,
        IOptions<AzureStorageOptions> storageOptions,
        IConfiguration configuration,
        IBackgroundJobClient jobs,
        ILogger<StorageBackupService> logger,
        TimeProvider time = null)
    {
        _time = time ?? TimeProvider.System;
        _contextFactory = contextFactory;
        _gateway = gateway;
        _storageOptions = storageOptions;
        _configuration = configuration;
        _jobs = jobs;
        _logger = logger;
    }

    public IReadOnlyList<string> GetConfiguredContainerNames()
    {
        var options = _storageOptions?.Value;
        if (options == null)
        {
            return Array.Empty<string>();
        }

        // The Data Protection key ring (keys.xml) is deliberately NOT backed up.
        //
        // Every consumer of Data Protection in this app protects something transient: Identity auth
        // cookies, antiforgery tokens, Identity password-reset/email-confirmation tokens, and the
        // 5-and-10-minute tokens issued by MobileExternalAuthTokenService and WebGoogleAuthTokenService.
        // Nothing is persisted encrypted at rest — Identity's ProtectPersonalData is not enabled — so
        // losing the ring signs everyone out once and is otherwise self-healing: Data Protection
        // regenerates a key automatically. Restoring a stale ring is strictly worse than regenerating,
        // which leaves the backup with no legitimate use while keeping a second copy of live key
        // material and a sharp edge in the restore UI.
        //
        // Revisit this if the app ever enables Identity's ProtectPersonalData, or starts protecting
        // anything long-lived and persisted. At that point losing the ring becomes permanent data
        // loss and the key-ring container must be added back here.
        //
        // (Encrypted-HLS content keys do NOT trip that condition: they are wrapped with a config-held
        // key, precisely so they do not depend on a ring designed to be disposable.)
        //
        // The streaming container IS backed up, even though every byte in it is derived from the
        // playback master sitting in ContainerName and could in principle be rebuilt by re-running
        // the packaging pass over the whole catalogue.
        //
        // The reason is that a restore has to return a *working product*, and the database is not
        // restored alongside these containers. After a blob-only restore every SongMetadata row
        // still carries its HlsStreamId, so every row points at a folder that would not exist -
        // the manifest endpoint fails for every song while the database looks perfectly healthy.
        // Rebuilding instead means the catalogue stays down for however long a full re-encode takes.
        // One extra copy of the audio corpus is cheap next to that outage.
        var names = new List<string>();
        foreach (var name in new[]
                 {
                     options.ContainerName,
                     options.PersonaImageContainerName,
                     options.StreamingContainerName
                 })
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var trimmed = name.Trim();
            if (!names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(trimmed);
            }
        }

        return names;
    }

    public Task<StorageBackupRun> StartBackupAsync(int? initiatedByUserId, string initiatedByEmail, bool forceFullCopy)
        => StartAsync(
            StorageBackupDirection.Backup,
            initiatedByUserId,
            initiatedByEmail,
            GetValidatedContainerNames(),
            StorageRestoreScope.MissingAndDiffering,
            overwriteNewerLive: true,
            forceFullCopy,
            StorageBackupTriggerSources.Admin);

    public async Task<StorageBackupRun> StartRestoreAsync(
        int? initiatedByUserId,
        string initiatedByEmail,
        IReadOnlyCollection<string> sourceContainerNames,
        StorageRestoreScope scope,
        bool overwriteNewerLive)
    {
        if (sourceContainerNames == null || sourceContainerNames.Count == 0)
        {
            throw new InvalidOperationException("Select at least one container to restore.");
        }

        var configured = GetValidatedContainerNames();
        var selected = new List<string>();
        foreach (var requested in sourceContainerNames)
        {
            var match = configured.FirstOrDefault(name =>
                string.Equals(name, requested?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new InvalidOperationException(
                    $"'{requested}' is not a configured storage container and cannot be restored.");
            }

            if (!selected.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                selected.Add(match);
            }
        }

        // Restoring from a backup container that does not exist, or that holds nothing, is a
        // no-op worth failing loudly on rather than reporting as a successful restore.
        foreach (var container in selected)
        {
            var backupContainer = StorageBackupNaming.ToBackupContainerName(container);
            if (!await _gateway.ContainerExistsAsync(backupContainer, CancellationToken.None))
            {
                throw new InvalidOperationException(
                    $"Backup container '{backupContainer}' does not exist. Run a backup before restoring.");
            }

            if (!await HasAnyBlobAsync(backupContainer, CancellationToken.None))
            {
                throw new InvalidOperationException(
                    $"Backup container '{backupContainer}' is empty. Run a backup before restoring.");
            }
        }

        return await StartAsync(
            StorageBackupDirection.Restore,
            initiatedByUserId,
            initiatedByEmail,
            selected,
            scope,
            overwriteNewerLive,
            forceFullCopy: false,
            StorageBackupTriggerSources.Admin);
    }

    public async Task RunRecurringBackupAsync()
    {
        await using (var context = await _contextFactory.CreateDbContextAsync())
        {
            if (await HasActiveRunAsync(context))
            {
                // Log and return rather than throw: a recurring job that throws would be retried
                // repeatedly against a run that is legitimately still going.
                _logger.LogInformation(
                    "Skipping the nightly storage backup because another backup or restore is already active.");
                return;
            }
        }

        try
        {
            await StartAsync(
                StorageBackupDirection.Backup,
                initiatedByUserId: null,
                initiatedByEmail: null,
                GetValidatedContainerNames(),
                StorageRestoreScope.MissingAndDiffering,
                overwriteNewerLive: true,
                forceFullCopy: false,
                StorageBackupTriggerSources.Recurring);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "The nightly storage backup could not be queued.");
        }
    }

    public async Task<List<StorageBackupRun>> GetRunsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.StorageBackupRuns.AsNoTracking()
            .Include(run => run.Containers)
            .OrderByDescending(run => run.CreatedAt)
            .ThenByDescending(run => run.Id)
            .Take(100)
            .ToListAsync();
    }

    public async Task<StorageBackupRun> GetRunAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.StorageBackupRuns.AsNoTracking()
            .Include(run => run.Containers)
            .Include(run => run.Failures)
            .FirstOrDefaultAsync(run => run.Id == runId);
    }

    public async Task<StorageBackupRun> GetActiveRunAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.StorageBackupRuns.AsNoTracking()
            .Include(run => run.Containers)
            .Where(run => run.Status == StorageBackupRunStatus.Queued
                || run.Status == StorageBackupRunStatus.Running)
            .OrderByDescending(run => run.Id)
            .FirstOrDefaultAsync();
    }

    public async Task RequestCancellationAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.StorageBackupRuns.FindAsync(runId);
        if (run == null
            || run.Status is not (StorageBackupRunStatus.Queued or StorageBackupRunStatus.Running)
            || run.CancellationRequestedAt.HasValue)
        {
            return;
        }

        run.CancellationRequestedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    [AutomaticRetry(Attempts = 2)]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RunAsync(int runId)
    {
        try
        {
            await ExecuteAsync(runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Storage backup run {RunId} aborted", runId);
            await MarkFailedAsync(runId, ex);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Queueing
    // ------------------------------------------------------------------

    private async Task<StorageBackupRun> StartAsync(
        StorageBackupDirection direction,
        int? initiatedByUserId,
        string initiatedByEmail,
        IReadOnlyList<string> containerNames,
        StorageRestoreScope scope,
        bool overwriteNewerLive,
        bool forceFullCopy,
        string triggerSource)
    {
        if (!_gateway.IsConfigured)
        {
            throw new InvalidOperationException(
                "Azure storage is not configured, so backup and restore are unavailable.");
        }

        await using var context = await _contextFactory.CreateDbContextAsync();
        if (await HasActiveRunAsync(context))
        {
            throw new InvalidOperationException("A storage backup or restore is already queued or running.");
        }

        var run = new StorageBackupRun
        {
            Direction = direction,
            RestoreScope = scope,
            OverwriteNewerLive = overwriteNewerLive,
            ForceFullCopy = forceFullCopy,
            Status = StorageBackupRunStatus.Queued,
            ActiveLockKey = 1,
            InitiatedByUserId = initiatedByUserId,
            InitiatedByEmail = initiatedByEmail?.Trim(),
            TriggerSource = triggerSource
        };

        foreach (var containerName in containerNames)
        {
            var backupContainerName = StorageBackupNaming.ToBackupContainerName(containerName);
            run.Containers.Add(new StorageBackupContainerProgress
            {
                SourceContainerName = containerName,
                DestinationContainerName = backupContainerName,
                Status = StorageBackupContainerStatus.Pending
            });
        }

        context.StorageBackupRuns.Add(run);
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            // The unique filtered index on ActiveLockKey is the real mutex.
            throw new InvalidOperationException(
                "A storage backup or restore is already queued or running.", ex);
        }

        try
        {
            run.HangfireJobId = _jobs.Enqueue<IStorageBackupService>(service => service.RunAsync(run.Id));
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            run.Status = StorageBackupRunStatus.Failed;
            run.ActiveLockKey = null;
            run.CompletedAt = DateTime.UtcNow;
            run.FailureMessage = SanitizeDiagnostic(ex);
            await context.SaveChangesAsync();
            throw new InvalidOperationException("The storage backup run could not be queued in Hangfire.", ex);
        }

        return run;
    }

    private static Task<bool> HasActiveRunAsync(AppDbContext context)
        => context.StorageBackupRuns.AnyAsync(run =>
            run.Status == StorageBackupRunStatus.Queued
            || run.Status == StorageBackupRunStatus.Running);

    private IReadOnlyList<string> GetValidatedContainerNames()
    {
        var names = GetConfiguredContainerNames();
        if (names.Count == 0)
        {
            throw new InvalidOperationException(
                "No Azure storage containers are configured, so there is nothing to back up.");
        }

        foreach (var name in names)
        {
            if (StorageBackupNaming.IsBackupContainerName(name))
            {
                throw new InvalidOperationException(
                    $"Configured container '{name}' is itself a backup container. Check the Azure configuration section.");
            }
        }

        return names;
    }

    private async Task<bool> HasAnyBlobAsync(string containerName, CancellationToken cancellationToken)
    {
        await foreach (var _ in _gateway.ListAsync(containerName, false, false, cancellationToken))
        {
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------

    private async Task ExecuteAsync(int runId)
    {
        StorageBackupDirection direction;
        StorageRestoreScope scope;
        bool overwriteNewerLive;
        bool forceFullCopy;
        List<int> containerIds;

        await using (var context = await _contextFactory.CreateDbContextAsync())
        {
            var run = await context.StorageBackupRuns
                .Include(item => item.Containers)
                .FirstOrDefaultAsync(item => item.Id == runId)
                ?? throw new InvalidOperationException($"Storage backup run {runId} was not found.");

            if (run.Status is StorageBackupRunStatus.Completed
                or StorageBackupRunStatus.CompletedWithErrors
                or StorageBackupRunStatus.Cancelled)
            {
                return;
            }

            // Re-entry after a process recycle resumes the same row rather than creating a new one.
            run.Status = StorageBackupRunStatus.Running;
            run.ActiveLockKey = 1;
            run.StartedAt ??= DateTime.UtcNow;
            direction = run.Direction;
            scope = run.RestoreScope;
            overwriteNewerLive = run.OverwriteNewerLive;
            forceFullCopy = run.ForceFullCopy;
            containerIds = run.Containers
                .Where(container => container.Status is not (StorageBackupContainerStatus.Completed
                    or StorageBackupContainerStatus.Skipped))
                .OrderBy(container => container.Id)
                .Select(container => container.Id)
                .ToList();
            await context.SaveChangesAsync();
        }

        var cancelled = false;
        foreach (var containerId in containerIds)
        {
            if (await IsCancellationRequestedAsync(runId))
            {
                cancelled = true;
                break;
            }

            cancelled |= await ProcessContainerAsync(
                runId, containerId, direction, scope, overwriteNewerLive, forceFullCopy);

            if (cancelled)
            {
                break;
            }
        }

        await FinalizeRunAsync(runId, cancelled);
        await PurgeExpiredRunsAsync();
    }

    /// <summary>
    /// Copies one container in one direction. Returns true when the run was cancelled mid-container.
    /// </summary>
    private async Task<bool> ProcessContainerAsync(
        int runId,
        int containerId,
        StorageBackupDirection direction,
        StorageRestoreScope scope,
        bool overwriteNewerLive,
        bool forceFullCopy)
    {
        string liveContainer;
        string backupContainer;
        await using (var context = await _contextFactory.CreateDbContextAsync())
        {
            var progress = await context.StorageBackupContainerProgresses.FindAsync(containerId);
            if (progress == null)
            {
                return false;
            }

            liveContainer = progress.SourceContainerName;
            backupContainer = progress.DestinationContainerName;
            progress.Status = StorageBackupContainerStatus.Listing;
            progress.StartedAt ??= DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // For a backup the live container is the source; for a restore the backup container is.
        var copyFrom = direction == StorageBackupDirection.Backup ? liveContainer : backupContainer;
        var copyTo = direction == StorageBackupDirection.Backup ? backupContainer : liveContainer;

        var tally = new ContainerTally();
        try
        {
            if (!await _gateway.ContainerExistsAsync(copyFrom, CancellationToken.None))
            {
                await CompleteContainerAsync(
                    containerId,
                    tally,
                    StorageBackupContainerStatus.Skipped,
                    $"Container '{copyFrom}' does not exist, so there was nothing to copy.");
                return false;
            }

            await _gateway.EnsureContainerAsync(copyTo, CancellationToken.None);

            // Only the destination index is materialised. The source is streamed, so a container
            // holding tens of thousands of blobs never lands in memory all at once.
            var destinationIndex = await BuildDestinationIndexAsync(copyTo);

            var total = await CountBlobsAsync(copyFrom);
            await SetContainerTotalAsync(containerId, total);

            var cancelled = await CopyContainerAsync(
                runId, containerId, copyFrom, copyTo, direction, scope,
                overwriteNewerLive, forceFullCopy, destinationIndex, tally);

            var status = cancelled
                ? StorageBackupContainerStatus.Copying
                : tally.Failed > 0
                    ? StorageBackupContainerStatus.Failed
                    : StorageBackupContainerStatus.Completed;

            await CompleteContainerAsync(containerId, tally, status, failureMessage: null, leaveOpen: cancelled);
            return cancelled;
        }
        catch (ContainerAbortedException ex)
        {
            await RecordFailureAsync(runId, copyFrom, blobName: null,
                StorageBackupFailureCodes.TooManyConsecutiveFailures, ex.Message);
            await CompleteContainerAsync(containerId, tally, StorageBackupContainerStatus.Failed, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Listing or preparing container {Container} failed for run {RunId}", copyFrom, runId);
            await RecordFailureAsync(runId, copyFrom, blobName: null,
                StorageBackupFailureCodes.ListFailed, SanitizeDiagnostic(ex));
            await CompleteContainerAsync(
                containerId, tally, StorageBackupContainerStatus.Failed, SanitizeDiagnostic(ex));
            return false;
        }
    }

    private async Task<bool> CopyContainerAsync(
        int runId,
        int containerId,
        string copyFrom,
        string copyTo,
        StorageBackupDirection direction,
        StorageRestoreScope scope,
        bool overwriteNewerLive,
        bool forceFullCopy,
        Dictionary<string, StorageBlobSnapshot> destinationIndex,
        ContainerTally tally)
    {
        var isBackup = direction == StorageBackupDirection.Backup;
        var sasQuery = _gateway.CreateContainerReadSasQuery(copyFrom, SasLifetime);
        var maxParallel = GetMaxParallelCopies();

        using var slots = new SemaphoreSlim(maxParallel, maxParallel);
        var inFlight = new List<Task>();
        var flushGate = new ProgressFlushGate(ProgressFlushInterval, ProgressFlushWindow, _time);
        var cancelled = false;

        await SetContainerStatusAsync(containerId, StorageBackupContainerStatus.Copying);

        await foreach (var source in _gateway.ListAsync(copyFrom, includeTags: true, includeMetadata: true, CancellationToken.None))
        {
            destinationIndex.TryGetValue(source.Name, out var destination);

            var decision = isBackup
                ? StorageBackupDiff.DecideBackup(source, destination, forceFullCopy)
                : StorageBackupDiff.DecideRestore(source, destination, scope, overwriteNewerLive);

            if (decision == StorageBackupDecision.Copy)
            {
                await slots.WaitAsync();
                inFlight.Add(CopyOneAsync(runId, copyFrom, copyTo, sasQuery, source, isBackup, tally, slots));
            }
            else
            {
                Interlocked.Increment(ref tally.Processed);
                if (decision == StorageBackupDecision.SkipNewerLive)
                {
                    Interlocked.Increment(ref tally.SkippedNewerLive);
                }
                Interlocked.Increment(ref tally.Skipped);
            }

            // Completed tasks never fault — CopyOneAsync records its own failures — so pruning
            // them here cannot swallow an error, and it keeps the list flat over a long run.
            if (inFlight.Count >= maxParallel * 4)
            {
                inFlight.RemoveAll(task => task.IsCompleted);
            }

            if (flushGate.ShouldFlush(Volatile.Read(ref tally.Processed)))
            {
                await FlushContainerProgressAsync(containerId, tally);
                if (await IsCancellationRequestedAsync(runId))
                {
                    cancelled = true;
                    break;
                }
            }

            if (tally.ConsecutiveFailures >= ConsecutiveFailureAbortThreshold)
            {
                await Task.WhenAll(inFlight);
                throw new ContainerAbortedException(
                    $"Aborted container '{copyFrom}' after {ConsecutiveFailureAbortThreshold} consecutive copy failures.");
            }
        }

        // Let in-flight server-side copies finish even on cancellation; aborting them would
        // leave half-written destination blobs.
        //
        // The listing loop is the only other thing that writes progress, so without flushing here the
        // last batch of copies - up to maxParallel of them, and the slowest ones at that, since large
        // blobs are what is still outstanding - would complete invisibly.
        var drain = Task.WhenAll(inFlight);
        while (await Task.WhenAny(drain, Task.Delay(ProgressFlushWindow)) != drain)
        {
            await FlushContainerProgressAsync(containerId, tally);
        }

        await drain;
        return cancelled;
    }

    private async Task CopyOneAsync(
        int runId,
        string copyFrom,
        string copyTo,
        string sasQuery,
        StorageBlobSnapshot source,
        bool isBackup,
        ContainerTally tally,
        SemaphoreSlim slots)
    {
        try
        {
            var stamp = isBackup
                ? StorageBackupDiff.BuildBackupMetadata(source, copyFrom, runId, DateTime.UtcNow)
                : StorageBackupDiff.BuildRestoreMetadata(source, DateTime.UtcNow);

            // Copy Blob drops index tags, so they are reapplied explicitly — album cover lookup
            // goes through FindBlobsByTags and would silently break otherwise.
            await _gateway.CopyAsync(
                copyFrom,
                copyTo,
                source.Name,
                sasQuery,
                BuildDestinationMetadata(source.Metadata, stamp),
                source.Tags?.ToDictionary(pair => pair.Key, pair => pair.Value),
                CancellationToken.None);

            Interlocked.Add(ref tally.CopiedBytes, source.Length);
            Interlocked.Increment(ref tally.Copied);
            Interlocked.Exchange(ref tally.ConsecutiveFailures, 0);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref tally.Failed);
            Interlocked.Increment(ref tally.ConsecutiveFailures);
            var code = ex is TimeoutException
                ? StorageBackupFailureCodes.CopyTimeout
                : StorageBackupFailureCodes.CopyFailed;
            await RecordFailureAsync(runId, copyTo, source.Name, code, SanitizeDiagnostic(ex));
            _logger.LogWarning(ex, "Copying blob {BlobName} into {Container} failed", source.Name, copyTo);
        }
        finally
        {
            Interlocked.Increment(ref tally.Processed);
            slots.Release();
        }
    }

    /// <summary>
    /// Carries the source blob's own metadata across, then applies the provenance stamp. Reserved
    /// backup keys are dropped from the inherited set so a restored live blob does not end up
    /// carrying the backup container's bookkeeping.
    /// </summary>
    private static Dictionary<string, string> BuildDestinationMetadata(
        IReadOnlyDictionary<string, string> sourceMetadata,
        Dictionary<string, string> stamp)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sourceMetadata != null)
        {
            foreach (var pair in sourceMetadata)
            {
                if (!ReservedMetadataKeys.Contains(pair.Key))
                {
                    metadata[pair.Key] = pair.Value;
                }
            }
        }

        foreach (var pair in stamp)
        {
            metadata[pair.Key] = pair.Value;
        }

        return metadata;
    }

    private static readonly HashSet<string> ReservedMetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        StorageBackupMetadataKeys.SourceETag,
        StorageBackupMetadataKeys.SourceLength,
        StorageBackupMetadataKeys.SourceLastModified,
        StorageBackupMetadataKeys.SourceMd5,
        StorageBackupMetadataKeys.SourceContainer,
        StorageBackupMetadataKeys.BackupRunId,
        StorageBackupMetadataKeys.BackupAt,
        StorageBackupMetadataKeys.RestoredFromETag,
        StorageBackupMetadataKeys.RestoredAt
    };

    private async Task<Dictionary<string, StorageBlobSnapshot>> BuildDestinationIndexAsync(string containerName)
    {
        var index = new Dictionary<string, StorageBlobSnapshot>(StringComparer.Ordinal);
        await foreach (var blob in _gateway.ListAsync(containerName, includeTags: false, includeMetadata: true, CancellationToken.None))
        {
            index[blob.Name] = blob;
        }

        return index;
    }

    private async Task<int> CountBlobsAsync(string containerName)
    {
        var count = 0;
        await foreach (var _ in _gateway.ListAsync(containerName, includeTags: false, includeMetadata: false, CancellationToken.None))
        {
            count++;
        }

        return count;
    }

    private int GetMaxParallelCopies()
    {
        var configured = _configuration.GetValue(AppSettingKeys.StorageBackupMaxParallelCopies, DefaultMaxParallelCopies);
        return Math.Clamp(configured, MinParallelCopies, MaxParallelCopies);
    }

    // ------------------------------------------------------------------
    // Progress persistence
    // ------------------------------------------------------------------

    private async Task<bool> IsCancellationRequestedAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.StorageBackupRuns.AsNoTracking()
            .AnyAsync(run => run.Id == runId && run.CancellationRequestedAt != null);
    }

    private async Task SetContainerStatusAsync(int containerId, StorageBackupContainerStatus status)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var progress = await context.StorageBackupContainerProgresses.FindAsync(containerId);
        if (progress == null) return;
        progress.Status = status;
        await context.SaveChangesAsync();
    }

    private async Task SetContainerTotalAsync(int containerId, int total)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var progress = await context.StorageBackupContainerProgresses.FindAsync(containerId);
        if (progress == null) return;
        progress.TotalBlobCount = total;
        await context.SaveChangesAsync();
    }

    private async Task FlushContainerProgressAsync(int containerId, ContainerTally tally)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var progress = await context.StorageBackupContainerProgresses.FindAsync(containerId);
        if (progress == null) return;
        ApplyTally(progress, tally);
        await context.SaveChangesAsync();
    }

    private async Task CompleteContainerAsync(
        int containerId,
        ContainerTally tally,
        StorageBackupContainerStatus status,
        string failureMessage,
        bool leaveOpen = false)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var progress = await context.StorageBackupContainerProgresses.FindAsync(containerId);
        if (progress == null) return;
        ApplyTally(progress, tally);
        progress.Status = status;
        progress.FailureMessage = failureMessage;
        if (!leaveOpen)
        {
            progress.CompletedAt = DateTime.UtcNow;
        }
        await context.SaveChangesAsync();
    }

    private static void ApplyTally(StorageBackupContainerProgress progress, ContainerTally tally)
    {
        progress.ProcessedCount = Volatile.Read(ref tally.Processed);
        progress.CopiedCount = Volatile.Read(ref tally.Copied);
        progress.SkippedCount = Volatile.Read(ref tally.Skipped);
        progress.SkippedNewerLiveCount = Volatile.Read(ref tally.SkippedNewerLive);
        progress.FailedCount = Volatile.Read(ref tally.Failed);
        progress.CopiedBytes = Interlocked.Read(ref tally.CopiedBytes);
    }

    private async Task RecordFailureAsync(
        int runId, string containerName, string blobName, string failureCode, string diagnostic)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            context.StorageBackupItemFailures.Add(new StorageBackupItemFailure
            {
                RunId = runId,
                ContainerName = Truncate(containerName, 128),
                BlobName = Truncate(blobName, 1024),
                FailureCode = Truncate(failureCode, 200),
                Diagnostic = Truncate(diagnostic, 2000)
            });
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Never let failure bookkeeping take down the run itself.
            _logger.LogWarning(ex, "Unable to record a storage backup failure for run {RunId}", runId);
        }
    }

    private async Task FinalizeRunAsync(int runId, bool cancelled)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.StorageBackupRuns
            .Include(item => item.Containers)
            .FirstOrDefaultAsync(item => item.Id == runId);
        if (run == null) return;

        run.TotalBlobCount = run.Containers.Sum(container => container.TotalBlobCount);
        run.ProcessedCount = run.Containers.Sum(container => container.ProcessedCount);
        run.CopiedCount = run.Containers.Sum(container => container.CopiedCount);
        run.SkippedCount = run.Containers.Sum(container => container.SkippedCount);
        run.SkippedNewerLiveCount = run.Containers.Sum(container => container.SkippedNewerLiveCount);
        run.FailedCount = run.Containers.Sum(container => container.FailedCount);
        run.CopiedBytes = run.Containers.Sum(container => container.CopiedBytes);

        run.Status = cancelled
            ? StorageBackupRunStatus.Cancelled
            : run.FailedCount > 0 || run.Containers.Any(container => container.Status == StorageBackupContainerStatus.Failed)
                ? StorageBackupRunStatus.CompletedWithErrors
                : StorageBackupRunStatus.Completed;
        run.ActiveLockKey = null;
        run.CompletedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    private async Task MarkFailedAsync(int runId, Exception exception)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.StorageBackupRuns.FindAsync(runId);
        if (run == null) return;

        if (run.Status is not (StorageBackupRunStatus.Completed
            or StorageBackupRunStatus.CompletedWithErrors
            or StorageBackupRunStatus.Cancelled))
        {
            run.Status = StorageBackupRunStatus.Failed;
            run.ActiveLockKey = null;
        }

        run.CompletedAt = DateTime.UtcNow;
        run.FailureMessage = SanitizeDiagnostic(exception);
        await context.SaveChangesAsync();
    }

    private async Task PurgeExpiredRunsAsync()
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var cutoff = DateTime.UtcNow.AddDays(-RunRetentionDays);
            var expired = await context.StorageBackupRuns
                .Include(run => run.Containers)
                .Include(run => run.Failures)
                .Where(run => run.CreatedAt < cutoff)
                .ToListAsync();
            if (expired.Count == 0) return;
            context.StorageBackupRuns.RemoveRange(expired);
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to purge expired storage backup runs.");
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string SanitizeDiagnostic(Exception exception)
    {
        var text = $"{exception.GetType().Name}: {exception.Message}"
            .Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 2000 ? text : text[..2000];
    }

    /// <summary>Mutable per-container counters, incremented from concurrent copy tasks.</summary>
    private sealed class ContainerTally
    {
        public int Processed;
        public int Copied;
        public int Skipped;
        public int SkippedNewerLive;
        public int Failed;
        public long CopiedBytes;

        /// <summary>Approximate under concurrency; only used as an abort heuristic.</summary>
        public int ConsecutiveFailures;
    }

    private sealed class ContainerAbortedException : Exception
    {
        public ContainerAbortedException(string message) : base(message) { }
    }
}
