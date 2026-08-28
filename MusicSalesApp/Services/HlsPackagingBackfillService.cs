#nullable enable
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// The pass that packages already-published songs as encrypted HLS.
///
/// <para>
/// Scoped to the environment it runs in: the containers, the queue and the database all come from
/// this process's configuration, so it is run once per environment — local, then test, then
/// production — rather than once globally. That mirrors
/// <see cref="IImageVariantBackfillService"/> exactly.
/// </para>
///
/// <para>
/// The structural difference from that job is that <b>no work happens here</b>. FFmpeg lives in the
/// Function app, so this only selects songs and puts messages on a queue; the packages come back as
/// callbacks over the following minutes or hours. A run therefore completes on its <b>last
/// callback</b>, not when its Hangfire job returns — the same arrangement the media-integrity audit
/// uses, and the reason <see cref="HlsPackagingBackfillStatus.AwaitingCallbacks"/> is a distinct
/// state rather than an inferred one.
/// </para>
/// </summary>
public interface IHlsPackagingBackfillService
{
    /// <summary>
    /// Queues a run, or throws <see cref="InvalidOperationException"/> if one is already active.
    /// </summary>
    Task<HlsPackagingBackfillRun> StartAsync(
        HlsPackagingBackfillScope scope,
        bool dryRun,
        int? initiatedByUserId,
        string? initiatedByEmail);

    /// <summary>Recent runs, newest first, for the admin history table.</summary>
    Task<List<HlsPackagingBackfillRun>> GetRunsAsync();

    /// <summary>One run with its failure rows.</summary>
    Task<HlsPackagingBackfillRun?> GetRunAsync(int runId);

    /// <summary>The queued or running run, if any.</summary>
    Task<HlsPackagingBackfillRun?> GetActiveRunAsync();

    /// <summary>
    /// Asks the running job to stop dispatching. Cooperative, and it only stops <em>dispatch</em> —
    /// messages already on the queue will still be processed and still call back. There is no way to
    /// unsend them, and pretending otherwise would leave songs packaged but unrecorded.
    ///
    /// <para>
    /// A run that has already finished dispatching is <b>ended outright</b> rather than flagged: at
    /// that point the Hangfire job has returned, so no cooperative flag has a reader, and the run
    /// would otherwise keep <c>ActiveLockKey</c> until every straggler called back — which a
    /// dead-lettered message never does.
    /// </para>
    /// </summary>
    Task RequestCancellationAsync(int runId);

    /// <summary>
    /// The containers this environment will write to, so an operator can confirm which environment
    /// they are about to act on before starting a run.
    /// </summary>
    IReadOnlyList<string> GetTargetContainerNames();

    /// <summary>
    /// The Hangfire entry point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No automatic retry</b>, unlike <see cref="IImageVariantBackfillService.RunAsync"/>. That
    /// job does its work inline and re-queries for what is still outstanding, so a retry resumes
    /// cleanly. This one dispatches queue messages, and a song that has been dispatched but has not
    /// called back yet still looks unpackaged — so a retry would re-select it, mint a second package
    /// folder for it, and reset the dispatch counter the completion check is computed from. The
    /// outcome would be self-healing (the later callback supersedes the earlier and sweeps it) but
    /// the run's own bookkeeping would not be.
    /// </para>
    /// <para>
    /// Recovery is to start a fresh run instead. The <c>Missing</c> scope selects on the package
    /// being absent, so it picks up exactly what is still outstanding — which is the same
    /// resumability, expressed where it actually holds.
    /// </para>
    /// <para>
    /// The job can legitimately run for hours: dispatch is throttled against outstanding callbacks,
    /// so on a large catalogue it spends most of its life waiting. Cancellation is cooperative and
    /// stops further dispatch.
    /// </para>
    /// </remarks>
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    Task RunAsync(int runId);
}

/// <inheritdoc />
public sealed class HlsPackagingBackfillService : IHlsPackagingBackfillService
{
    /// <summary>
    /// How many messages may be in flight at once.
    ///
    /// <para>
    /// <c>host.json</c> pins <c>batchSize: 1</c> so target-based scaling adds one Function instance
    /// per queued message. Releasing an entire catalogue at once would therefore ask Azure to scale
    /// to hundreds of instances simultaneously, all transcoding — which is both a cost spike and a
    /// good way to exhaust the storage account's request budget. Dispatch is capped and topped up as
    /// callbacks land.
    /// </para>
    /// </summary>
    private const int MaxInFlightMessages = 24;

    /// <summary>How long to wait between checks when the in-flight cap has been reached.</summary>
    private static readonly TimeSpan DispatchPollInterval = TimeSpan.FromSeconds(10);

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IMediaProcessingQueueClient _queueClient;
    private readonly IBlobContainerFactory _containerFactory;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<HlsPackagingBackfillService> _logger;

    public HlsPackagingBackfillService(
        IDbContextFactory<AppDbContext> contextFactory,
        IMediaProcessingQueueClient queueClient,
        IBlobContainerFactory containerFactory,
        IBackgroundJobClient jobs,
        ILogger<HlsPackagingBackfillService> logger)
    {
        _contextFactory = contextFactory;
        _queueClient = queueClient;
        _containerFactory = containerFactory;
        _jobs = jobs;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetTargetContainerNames()
        => new[] { _containerFactory.GetStreamingContainer().Name };

    /// <inheritdoc />
    public async Task<HlsPackagingBackfillRun> StartAsync(
        HlsPackagingBackfillScope scope,
        bool dryRun,
        int? initiatedByUserId,
        string? initiatedByEmail)
    {
        if (!_queueClient.IsPackagingConfigured)
        {
            throw new InvalidOperationException(
                "The packaging queue is not configured, so no run can be started.");
        }

        await using var context = await _contextFactory.CreateDbContextAsync();

        // Checked here as well as enforced by the index below, matching StorageBackupService. The
        // read gives the operator a clean message rather than a DbUpdateException; the index is what
        // actually makes it race-proof. Neither alone is enough - the read has a window, and the
        // index alone would surface as an opaque failure.
        if (await context.HlsPackagingBackfillRuns.AnyAsync(r => r.ActiveLockKey != null))
        {
            throw new InvalidOperationException(
                "An HLS packaging run is already active. Wait for it to finish or cancel it first.");
        }

        var run = new HlsPackagingBackfillRun
        {
            Scope = scope,
            DryRun = dryRun,
            Status = HlsPackagingBackfillStatus.Queued,
            InitiatedByUserId = initiatedByUserId,
            InitiatedByEmail = initiatedByEmail,

            // A dry run takes the lock too. It reads the same rows and reports the same counts, and
            // letting one overlap a real run would report figures that were already being consumed.
            ActiveLockKey = 1
        };

        context.HlsPackagingBackfillRuns.Add(run);

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The filtered unique index on ActiveLockKey rejected it, which is the point: the check
            // is the database's, not a read-then-write race here.
            throw new InvalidOperationException(
                "An HLS packaging run is already active. Wait for it to finish or cancel it first.");
        }

        run.HangfireJobId = _jobs.Enqueue<IHlsPackagingBackfillService>(service => service.RunAsync(run.Id));
        await context.SaveChangesAsync();

        return run;
    }

    /// <inheritdoc />
    public async Task<List<HlsPackagingBackfillRun>> GetRunsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.HlsPackagingBackfillRuns
            .AsNoTracking()
            .OrderByDescending(run => run.CreatedAt)
            .Take(50)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<HlsPackagingBackfillRun?> GetRunAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.HlsPackagingBackfillRuns
            .AsNoTracking()
            .Include(run => run.Failures)
            .FirstOrDefaultAsync(run => run.Id == runId);
    }

    /// <inheritdoc />
    public async Task<HlsPackagingBackfillRun?> GetActiveRunAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.HlsPackagingBackfillRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(run => run.ActiveLockKey != null);
    }

    /// <inheritdoc />
    public async Task RequestCancellationAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.HlsPackagingBackfillRuns.FindAsync(runId);
        if (run == null || run.CancellationRequestedAt != null)
        {
            return;
        }

        run.CancellationRequestedAt = DateTime.UtcNow;

        // Once dispatch is over there is nobody left to observe the flag: the Hangfire job has
        // returned and everything after it happens in callbacks. Leaving the run in
        // AwaitingCallbacks would make cancelling appear to work while the run went on holding
        // ActiveLockKey - and if a dispatched message never calls back at all (dead-lettered, or a
        // Function that never ran), it would hold that lock forever and StartAsync would refuse
        // every future run until someone edited the database. Cancelling here therefore ends the run
        // outright, which is what an operator pressing cancel at this point means.
        //
        // Callbacks still landing afterwards stay harmless: they update the counters, and the
        // completion check acts only on AwaitingCallbacks, so none of them resurrects a run that was
        // deliberately stopped.
        if (run.Status == HlsPackagingBackfillStatus.AwaitingCallbacks)
        {
            run.Status = HlsPackagingBackfillStatus.Cancelled;
            run.CompletedAt = DateTime.UtcNow;
            run.ActiveLockKey = null;

            _logger.LogInformation(
                "HLS packaging run {RunId} cancelled while awaiting callbacks; {Outstanding} message(s) may still be in flight.",
                runId,
                run.OutstandingCount);
        }

        await context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task RunAsync(int runId)
    {
        await using (var context = await _contextFactory.CreateDbContextAsync())
        {
            var run = await context.HlsPackagingBackfillRuns.FindAsync(runId);
            if (run == null || run.Status is HlsPackagingBackfillStatus.Completed
                or HlsPackagingBackfillStatus.Cancelled
                or HlsPackagingBackfillStatus.Failed)
            {
                return;
            }

            run.Status = HlsPackagingBackfillStatus.Dispatching;
            run.StartedAt ??= DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        try
        {
            var candidates = await SelectCandidatesAsync(runId);

            await using (var context = await _contextFactory.CreateDbContextAsync())
            {
                var run = await context.HlsPackagingBackfillRuns.FindAsync(runId);
                if (run != null)
                {
                    run.TotalItemCount = candidates.Count;
                    await context.SaveChangesAsync();
                }
            }

            _logger.LogInformation(
                "HLS packaging run {RunId} selected {Count} songs (dry run: {DryRun})",
                runId,
                candidates.Count,
                await IsDryRunAsync(runId));

            if (await IsDryRunAsync(runId))
            {
                await FinishAsync(runId, HlsPackagingBackfillStatus.Completed, null);
                return;
            }

            await DispatchAsync(runId, candidates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HLS packaging run {RunId} failed while dispatching", runId);
            await FinishAsync(runId, HlsPackagingBackfillStatus.Failed, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Picks the songs this run is for, and the audio each should be packaged from.
    ///
    /// <para>
    /// Source preference is the creator's retained original when it is a genuinely different blob
    /// from the playback MP3, because packaging re-encodes to AAC and going via the MP3 would cost a
    /// second generation of loss. For an MP3 upload the two paths are the same blob by design
    /// (<c>SongMediaPaths.OriginalAudio</c> returns the playback path), so there is nothing to gain
    /// and the check collapses harmlessly.
    /// </para>
    /// </summary>
    private async Task<List<PackagingCandidate>> SelectCandidatesAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var run = await context.HlsPackagingBackfillRuns.AsNoTracking().FirstAsync(r => r.Id == runId);

        var query = context.SongMetadata
            .AsNoTracking()
            .Where(song => song.IsActive
                && song.IsEnabled
                && song.Mp3BlobPath != null
                && song.Mp3BlobPath != string.Empty);

        query = run.Scope switch
        {
            HlsPackagingBackfillScope.Missing => query.Where(song => song.HlsStreamId == null),
            HlsPackagingBackfillScope.RepairMissing => query.Where(song => song.HlsStreamId != null),
            _ => query
        };

        var songs = await query
            .Select(song => new
            {
                song.Id,
                song.Mp3BlobPath,
                song.OriginalAudioBlobPath,
                song.HlsStreamId
            })
            .ToListAsync();

        var candidates = new List<PackagingCandidate>(songs.Count);

        foreach (var song in songs)
        {
            if (run.Scope == HlsPackagingBackfillScope.RepairMissing
                && song.HlsStreamId is { } existing
                && await PackageExistsAsync(existing))
            {
                // Storage holds what the row claims, so there is nothing to repair.
                continue;
            }

            var source = !string.IsNullOrWhiteSpace(song.OriginalAudioBlobPath)
                && !string.Equals(song.OriginalAudioBlobPath, song.Mp3BlobPath, StringComparison.Ordinal)
                    ? song.OriginalAudioBlobPath
                    : song.Mp3BlobPath;

            // A fresh id per run, never the existing one. Repackaging into the live folder would
            // overwrite a working package in place - and a run that then failed halfway would leave
            // the song pointing at a folder that is half old and half new.
            candidates.Add(new PackagingCandidate(song.Id, source!, Guid.NewGuid()));
        }

        return candidates;
    }

    /// <summary>True when the streaming container actually holds this package's manifest.</summary>
    private async Task<bool> PackageExistsAsync(Guid hlsStreamId)
    {
        try
        {
            var blob = _containerFactory
                .GetStreamingContainer()
                .GetBlobClient(HlsPackagePaths.Manifest(hlsStreamId));

            return await blob.ExistsAsync();
        }
        catch (Exception ex)
        {
            // Cannot tell - so do not repair. Repackaging on a storage blip would churn the whole
            // catalogue for nothing, which is far worse than missing a genuinely absent package that
            // the next run will find.
            _logger.LogWarning(
                ex,
                "Could not check whether HLS package {StreamId} exists; leaving it alone.",
                hlsStreamId);

            return true;
        }
    }

    /// <summary>
    /// Puts the messages on the queue, capped so Azure is not asked to scale to one Function
    /// instance per song in the catalogue at once.
    /// </summary>
    private async Task DispatchAsync(int runId, IReadOnlyList<PackagingCandidate> candidates)
    {
        var dispatched = 0;

        foreach (var candidate in candidates)
        {
            if (await IsCancellationRequestedAsync(runId))
            {
                _logger.LogInformation(
                    "HLS packaging run {RunId} cancelled after dispatching {Dispatched} of {Total}",
                    runId,
                    dispatched,
                    candidates.Count);

                await FinishAsync(runId, HlsPackagingBackfillStatus.Cancelled, null);
                return;
            }

            while (await CountInFlightAsync(runId) >= MaxInFlightMessages)
            {
                await Task.Delay(DispatchPollInterval);

                if (await IsCancellationRequestedAsync(runId))
                {
                    await FinishAsync(runId, HlsPackagingBackfillStatus.Cancelled, null);
                    return;
                }
            }

            await _queueClient.EnqueuePackageAsync(new AudioPackageRequest
            {
                SongMetadataId = candidate.SongMetadataId,
                SourceBlobPath = candidate.SourceBlobPath,
                HlsStreamId = candidate.HlsStreamId,
                BackfillRunId = runId
            });

            dispatched++;

            // Recorded per message rather than at the end, because this counter is what the
            // in-flight cap and the completion check are both computed from. Batching the write
            // would let the cap be exceeded by however many messages had not been counted yet.
            await using var context = await _contextFactory.CreateDbContextAsync();
            var run = await context.HlsPackagingBackfillRuns.FindAsync(runId);
            if (run != null)
            {
                run.DispatchedCount = dispatched;
                await context.SaveChangesAsync();
            }
        }

        await using (var finalContext = await _contextFactory.CreateDbContextAsync())
        {
            var run = await finalContext.HlsPackagingBackfillRuns.FindAsync(runId);
            if (run != null)
            {
                // Dispatch is done; the work is not. Everything from here happens in callbacks, and
                // this status is what stops the run looking abandoned once the Hangfire job returns.
                run.Status = HlsPackagingBackfillStatus.AwaitingCallbacks;

                if (run.OutstandingCount == 0)
                {
                    run.Status = HlsPackagingBackfillStatus.Completed;
                    run.CompletedAt = DateTime.UtcNow;
                    run.ActiveLockKey = null;
                }

                await finalContext.SaveChangesAsync();
            }
        }

        _logger.LogInformation(
            "HLS packaging run {RunId} dispatched {Count} messages and is awaiting callbacks",
            runId,
            dispatched);
    }

    private async Task<int> CountInFlightAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.HlsPackagingBackfillRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
        return run?.OutstandingCount ?? 0;
    }

    private async Task<bool> IsCancellationRequestedAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.HlsPackagingBackfillRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
        return run?.CancellationRequestedAt != null;
    }

    private async Task<bool> IsDryRunAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.HlsPackagingBackfillRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
        return run?.DryRun ?? false;
    }

    private async Task FinishAsync(int runId, HlsPackagingBackfillStatus status, string? failureMessage)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.HlsPackagingBackfillRuns.FindAsync(runId);
        if (run == null)
        {
            return;
        }

        run.Status = status;
        run.CompletedAt = DateTime.UtcNow;

        // Truncated to the column's width. FailureMessage is nvarchar(2000) and this is handed a raw
        // ex.Message - an AggregateException's, or a SQL error quoting a whole statement, runs well
        // past that and throws on save. This is also the path that releases ActiveLockKey, so
        // throwing here would stand the run's lock up permanently while reporting nothing at all:
        // the failure that mattered would be replaced by a DbUpdateException about a string length.
        run.FailureMessage = Truncate(failureMessage, 2000);

        // Releasing the lock is what allows the next run to start. Held until here even on the
        // failure path, so a run that died mid-dispatch does not leave a second one racing its
        // still-outstanding callbacks.
        run.ActiveLockKey = null;

        await context.SaveChangesAsync();
    }

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    private sealed record PackagingCandidate(int SongMetadataId, string SourceBlobPath, Guid HlsStreamId);
}
