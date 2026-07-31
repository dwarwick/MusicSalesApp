#nullable enable
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public sealed class ImageVariantBackfillService : IImageVariantBackfillService
{
    /// <summary>How many items are processed between counter flushes to the database.</summary>
    private const int ProgressFlushInterval = 25;

    /// <summary>
    /// The longest the run row may go without an update while work is still happening. The admin page
    /// polls every two seconds, and a single image takes a noticeable fraction of a second to decode
    /// and re-encode, so waiting for <see cref="ProgressFlushInterval"/> items would leave the
    /// progress bar at zero for most of a small run and all of a very small one.
    /// </summary>
    private static readonly TimeSpan ProgressFlushWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Stop after this many consecutive failures. A run that cannot read anything - bad storage
    /// credentials, a container that does not exist - should fail fast rather than grind through
    /// every row producing identical errors.
    /// </summary>
    private const int ConsecutiveFailureAbortThreshold = 50;

    private const int DefaultMaxParallelEncodes = 2;
    private const int MinParallelEncodes = 1;
    private const int MaxParallelEncodes = 8;
    private const int RunRetentionDays = 90;

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IImageVariantService _variantService;
    private readonly IBlobContainerFactory _containerFactory;
    private readonly IAzureStorageService _storageService;
    private readonly IConfiguration _configuration;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<ImageVariantBackfillService> _logger;
    private readonly TimeProvider _time;

    public ImageVariantBackfillService(
        IDbContextFactory<AppDbContext> contextFactory,
        IImageVariantService variantService,
        IBlobContainerFactory containerFactory,
        IAzureStorageService storageService,
        IConfiguration configuration,
        IBackgroundJobClient jobs,
        ILogger<ImageVariantBackfillService> logger,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _contextFactory = contextFactory;
        _variantService = variantService;
        _containerFactory = containerFactory;
        _storageService = storageService;
        _configuration = configuration;
        _jobs = jobs;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetTargetContainerNames() => _containerFactory.GetConfiguredContainerNames();

    /// <inheritdoc />
    public async Task<ImageVariantBackfillRun> StartAsync(
        ImageVariantBackfillScope scope,
        bool dryRun,
        bool regenerateExisting,
        bool removeLegacyPngSharingImages,
        int? initiatedByUserId,
        string? initiatedByEmail)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        if (await context.ImageVariantBackfillRuns.AnyAsync(r =>
                r.Status == ImageVariantBackfillStatus.Queued || r.Status == ImageVariantBackfillStatus.Running))
        {
            throw new InvalidOperationException("An image-variant backfill is already queued or running.");
        }

        var run = new ImageVariantBackfillRun
        {
            Scope = scope,
            DryRun = dryRun,
            RegenerateExisting = regenerateExisting,
            RemoveLegacyPngSharingImages = removeLegacyPngSharingImages,
            Status = ImageVariantBackfillStatus.Queued,
            ActiveLockKey = 1,
            InitiatedByUserId = initiatedByUserId,
            InitiatedByEmail = initiatedByEmail ?? string.Empty,
            TriggerSource = ImageVariantBackfillTriggerSources.Admin
        };

        context.ImageVariantBackfillRuns.Add(run);

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The filtered unique index on ActiveLockKey rejected a second active run. Racing with
            // another admin is the only way to get here, and the message should say so rather than
            // surface a constraint violation.
            throw new InvalidOperationException("An image-variant backfill is already queued or running.");
        }

        try
        {
            run.HangfireJobId = _jobs.Enqueue<IImageVariantBackfillService>(service => service.RunAsync(run.Id));
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // The row exists but nothing will ever pick it up, so release the lock rather than
            // leaving the feature permanently blocked by a phantom "active" run.
            _logger.LogError(ex, "Could not enqueue image-variant backfill run {RunId}", run.Id);
            run.Status = ImageVariantBackfillStatus.Failed;
            run.ActiveLockKey = null;
            run.CompletedAt = DateTime.UtcNow;
            run.FailureMessage = $"Could not enqueue the background job: {ex.Message}";
            await context.SaveChangesAsync();
            throw;
        }

        return run;
    }

    /// <inheritdoc />
    public async Task<List<ImageVariantBackfillRun>> GetRunsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.ImageVariantBackfillRuns
            .AsNoTracking()
            .OrderByDescending(run => run.CreatedAt)
            .Take(50)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<ImageVariantBackfillRun?> GetRunAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.ImageVariantBackfillRuns
            .AsNoTracking()
            .Include(run => run.Failures)
            .FirstOrDefaultAsync(run => run.Id == runId);
    }

    /// <inheritdoc />
    public async Task<ImageVariantBackfillRun?> GetActiveRunAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.ImageVariantBackfillRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(run =>
                run.Status == ImageVariantBackfillStatus.Queued || run.Status == ImageVariantBackfillStatus.Running);
    }

    /// <inheritdoc />
    public async Task RequestCancellationAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.ImageVariantBackfillRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (run == null || run.CancellationRequestedAt.HasValue)
            return;

        run.CancellationRequestedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task RunAsync(int runId)
    {
        await using var startContext = await _contextFactory.CreateDbContextAsync();
        var run = await startContext.ImageVariantBackfillRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (run == null)
        {
            _logger.LogWarning("Image-variant backfill run {RunId} no longer exists", runId);
            return;
        }

        // Failed is terminal here as much as the others are. RunAsync rethrows so the Hangfire
        // dashboard shows a real failure with a stack trace, and [AutomaticRetry] then re-invokes
        // this method - but FinalizeAsync has already released the singleton lock and written a
        // failure message the admin is looking at. Letting the retry restart the whole job would
        // silently resurrect a run the operator has been told is dead, and re-enter it holding no
        // lock. Recovery is deliberately manual: the job is resumable, so starting a new run picks
        // up exactly the work still outstanding.
        if (run.Status is ImageVariantBackfillStatus.Completed
            or ImageVariantBackfillStatus.CompletedWithErrors
            or ImageVariantBackfillStatus.Cancelled
            or ImageVariantBackfillStatus.Failed)
        {
            return;
        }

        run.Status = ImageVariantBackfillStatus.Running;
        run.StartedAt ??= DateTime.UtcNow;
        await startContext.SaveChangesAsync();

        var counters = new RunCounters();

        try
        {
            var work = await BuildWorkListAsync(run);
            run.TotalItemCount = work.Count;
            await startContext.SaveChangesAsync();

            _logger.LogInformation(
                "Image-variant backfill {RunId} starting: {Count} items, scope {Scope}, dryRun {DryRun}, containers {Containers}",
                run.Id, work.Count, run.Scope, run.DryRun, string.Join(", ", GetTargetContainerNames()));

            await ProcessAsync(run, work, counters);
            await SweepLegacySharingImagesAsync(run, counters);

            var cancelled = await IsCancellationRequestedAsync(run.Id);
            await FinalizeAsync(run.Id, counters, cancelled
                ? ImageVariantBackfillStatus.Cancelled
                : counters.Failed > 0
                    ? ImageVariantBackfillStatus.CompletedWithErrors
                    : ImageVariantBackfillStatus.Completed,
                failureMessage: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image-variant backfill run {RunId} failed", runId);
            await FinalizeAsync(run.Id, counters, ImageVariantBackfillStatus.Failed, ex.Message);
            throw;
        }
        finally
        {
            await PurgeOldRunsAsync();
        }
    }

    /// <summary>
    /// The images to process, read from the database rather than by listing the containers.
    ///
    /// <para>
    /// A blob with no row has no consumer: nothing links to it, and there is nowhere to record the
    /// widths, so renditions of it would be storage cost that is never served. Legacy name-based
    /// songs need no special handling - the rendition path derives from the stored blob path and
    /// <see cref="ImageVariantPaths"/> has no naming-scheme branch. Inactive and disabled songs are
    /// included so that re-enabling one does not require a second backfill.
    /// </para>
    /// </summary>
    private async Task<List<WorkItem>> BuildWorkListAsync(ImageVariantBackfillRun run)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var work = new List<WorkItem>();

        if (run.Scope is ImageVariantBackfillScope.All or ImageVariantBackfillScope.CoverArtOnly)
        {
            var songs = await context.SongMetadata
                .AsNoTracking()
                .Where(s => s.ImageBlobPath != null && s.ImageBlobPath != "")
                .Where(s => run.RegenerateExisting
                    || s.CoverArtVariantWidths == null
                    || s.CoverArtVariantWidths == "")
                .Select(s => new { s.Id, s.ImageBlobPath })
                .ToListAsync();

            work.AddRange(songs.Select(s =>
                new WorkItem(ImageVariantBackfillItemKind.CoverArt, s.Id, s.ImageBlobPath)));
        }

        if (run.Scope is ImageVariantBackfillScope.All or ImageVariantBackfillScope.PersonaOnly)
        {
            var personas = await context.CreatorPersonas
                .AsNoTracking()
                .Where(p => p.ImageBlobPath != null && p.ImageBlobPath != "")
                .Where(p => run.RegenerateExisting
                    || p.ImageVariantWidths == null
                    || p.ImageVariantWidths == "")
                .Select(p => new { p.Id, p.ImageBlobPath })
                .ToListAsync();

            work.AddRange(personas.Select(p =>
                new WorkItem(ImageVariantBackfillItemKind.PersonaImage, p.Id, p.ImageBlobPath!)));
        }

        return work;
    }

    private async Task ProcessAsync(ImageVariantBackfillRun run, List<WorkItem> work, RunCounters counters)
    {
        var maxParallel = ResolveMaxParallelEncodes();
        using var throttle = new SemaphoreSlim(maxParallel, maxParallel);
        var flushGate = new ProgressFlushGate(ProgressFlushInterval, ProgressFlushWindow, _time);
        var inFlight = new List<Task>();

        foreach (var item in work)
        {
            if (counters.ReadAborted() || await IsCancellationRequestedAsync(run.Id))
                break;

            // The wait is what enforces the limit; the item is then left to run while the loop moves
            // on to queue the next one. Awaiting the item here instead - as this originally did -
            // would make the semaphore decorative and the whole run sequential, which is exactly what
            // ImageVariants:MaxParallelEncodes is meant to control.
            await throttle.WaitAsync();
            inFlight.Add(ProcessAndTallyAsync(run, item, counters, throttle));

            // Completed tasks never fault - ProcessAndTallyAsync records its own failures - so pruning
            // here cannot swallow an error, and it keeps the list flat over a long run.
            if (inFlight.Count >= maxParallel * 4)
                inFlight.RemoveAll(task => task.IsCompleted);

            if (flushGate.ShouldFlush(counters.ReadProcessed()))
                await FlushProgressAsync(run.Id, counters);
        }

        // Whatever is still encoding when the loop ends - up to maxParallel items - would otherwise
        // finish invisibly, since the loop is the only other thing that writes progress.
        var drain = Task.WhenAll(inFlight);
        while (await Task.WhenAny(drain, Task.Delay(ProgressFlushWindow)) != drain)
        {
            await FlushProgressAsync(run.Id, counters);
        }

        await drain;
        await FlushProgressAsync(run.Id, counters);

        if (counters.ReadAborted())
        {
            throw new InvalidOperationException(
                $"Aborted after {ConsecutiveFailureAbortThreshold} consecutive failures. " +
                "Check storage connectivity and the run's failure list.");
        }
    }

    /// <summary>
    /// Processes one item and folds its outcome into the shared counters. Never throws: a fault here
    /// would surface from the drain and take down a run that has already recorded the failure.
    /// </summary>
    private async Task ProcessAndTallyAsync(
        ImageVariantBackfillRun run, WorkItem item, RunCounters counters, SemaphoreSlim throttle)
    {
        try
        {
            var outcome = await ProcessItemAsync(run, item);

            bool justAborted;
            lock (counters)
            {
                counters.Processed++;
                if (outcome.Succeeded)
                {
                    counters.Generated++;
                    counters.VariantBlobs += outcome.VariantCount;
                    counters.Bytes += outcome.BytesWritten;
                    if (outcome.UndersizedSource) counters.Undersized++;
                    counters.ConsecutiveFailures = 0;
                }
                else if (outcome.Skipped)
                {
                    counters.Skipped++;
                    counters.ConsecutiveFailures = 0;
                }
                else
                {
                    counters.Failed++;
                    counters.ConsecutiveFailures++;
                }

                justAborted = !counters.Aborted
                    && counters.ConsecutiveFailures >= ConsecutiveFailureAbortThreshold;
                if (justAborted) counters.Aborted = true;
            }

            if (justAborted)
            {
                _logger.LogError(
                    "Image-variant backfill {RunId} aborting after {Count} consecutive failures",
                    run.Id, ConsecutiveFailureAbortThreshold);
            }
        }
        finally
        {
            throttle.Release();
        }
    }

    private async Task<ItemOutcome> ProcessItemAsync(ImageVariantBackfillRun run, WorkItem item)
    {
        ImageVariantResult result;
        try
        {
            result = item.Kind == ImageVariantBackfillItemKind.CoverArt
                ? await _variantService.GenerateCoverArtVariantsAsync(item.BlobPath, run.DryRun)
                : await _variantService.GeneratePersonaVariantsAsync(item.BlobPath, run.DryRun);
        }
        catch (Exception ex)
        {
            // Each stage reports its own code. Filing everything under upload_failed - as this
            // originally did - makes the per-item failure list say the opposite of what happened
            // whenever the fault came from anywhere but the upload.
            _logger.LogWarning(ex, "Backfill item {Kind} {EntityId} threw while generating", item.Kind, item.EntityId);
            await RecordFailureAsync(run.Id, item, ImageVariantFailureCodes.Unexpected, ex.Message);
            return ItemOutcome.Failure();
        }

        if (!result.Succeeded)
        {
            await RecordFailureAsync(
                run.Id, item, result.FailureReason ?? ImageVariantFailureCodes.Unexpected, null);
            return ItemOutcome.Failure();
        }

        var ladder = item.Kind == ImageVariantBackfillItemKind.CoverArt
            ? ImageVariantSizes.CoverArt
            : ImageVariantSizes.Persona;

        // A source narrower than the smallest ladder rung yields one rendition at its own size.
        var undersized = result.SourceWidth > 0 && result.SourceWidth < ladder[0];

        if (run.DryRun)
            return ItemOutcome.Success(result.GeneratedWidths.Count, result.BytesWritten, undersized);

        try
        {
            if (!await PersistWidthsAsync(item, result.GeneratedWidths))
            {
                // Deleted while the run was in flight. Not a failure - there is nothing wrong and
                // nothing to fix - which is what the skipped counter is for.
                await RecordFailureAsync(run.Id, item, ImageVariantFailureCodes.EntityMissing, null);
                return ItemOutcome.Skip();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backfill item {Kind} {EntityId} could not record its widths", item.Kind, item.EntityId);
            await RecordFailureAsync(run.Id, item, ImageVariantFailureCodes.PersistFailed, ex.Message);
            return ItemOutcome.Failure();
        }

        return ItemOutcome.Success(result.GeneratedWidths.Count, result.BytesWritten, undersized);
    }

    /// <returns><see langword="false"/> when the row no longer exists.</returns>
    private async Task<bool> PersistWidthsAsync(WorkItem item, IReadOnlyList<int> widths)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var csv = ImageVariantSizes.ToCsv(widths);

        if (item.Kind == ImageVariantBackfillItemKind.CoverArt)
        {
            var song = await context.SongMetadata.FirstOrDefaultAsync(s => s.Id == item.EntityId);
            if (song == null)
                return false;

            song.CoverArtVariantWidths = csv;
            song.CoverArtVariantVersion++;
        }
        else
        {
            var persona = await context.CreatorPersonas.FirstOrDefaultAsync(p => p.Id == item.EntityId);
            if (persona == null)
                return false;

            persona.ImageVariantWidths = csv;
            persona.ImageVariantVersion++;
        }

        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Deletes superseded PNG sharing images, which nothing overwrites and which cost 1-2 MB each.
    ///
    /// <para>
    /// This is its own pass over every song with cover art rather than something folded into the
    /// rendition loop. The work list deliberately excludes songs whose renditions already exist, so
    /// running the sweep per item would make the option silently do nothing on every "generate
    /// missing" run after the first - leaving the full REGENERATE path, which rewrites every
    /// rendition blob in the environment, as the only way to reclaim the stragglers.
    /// </para>
    /// </summary>
    private async Task SweepLegacySharingImagesAsync(ImageVariantBackfillRun run, RunCounters counters)
    {
        if (!run.RemoveLegacyPngSharingImages
            || run.DryRun
            || run.Scope == ImageVariantBackfillScope.PersonaOnly)
        {
            return;
        }

        List<string> coverArtPaths;
        await using (var context = await _contextFactory.CreateDbContextAsync())
        {
            coverArtPaths = await context.SongMetadata
                .AsNoTracking()
                .Where(s => s.ImageBlobPath != null && s.ImageBlobPath != "")
                .Select(s => s.ImageBlobPath!)
                .ToListAsync();
        }

        var flushGate = new ProgressFlushGate(ProgressFlushInterval, ProgressFlushWindow, _time);

        foreach (var coverArtPath in coverArtPaths)
        {
            if (await IsCancellationRequestedAsync(run.Id))
                break;

            var candidates = SongMediaPaths.FacebookImageCandidatesFor(coverArtPath);
            var current = candidates.Count > 0 ? candidates[0] : null;

            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (await _storageService.DeleteAsync(candidate))
                    {
                        lock (counters) counters.LegacySharingImagesRemoved++;
                    }
                }
                catch (Exception ex)
                {
                    // Reclaiming disk is a bonus; it must never fail the run that produced the
                    // renditions.
                    _logger.LogWarning(ex, "Could not remove the superseded sharing image {Path}", candidate);
                }
            }

            if (flushGate.ShouldFlush(counters.ReadProcessed()))
                await FlushProgressAsync(run.Id, counters);
        }

        await FlushProgressAsync(run.Id, counters);
    }

    private async Task RecordFailureAsync(int runId, WorkItem item, string failureCode, string? message)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            context.ImageVariantBackfillItemFailures.Add(new ImageVariantBackfillItemFailure
            {
                RunId = runId,
                ItemKind = item.Kind,
                EntityId = item.EntityId,
                BlobPath = Truncate(item.BlobPath, 500),
                FailureCode = Truncate(failureCode, 50),
                Message = Truncate(message, 1000)
            });
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Losing a failure record must not take the run down with it.
            _logger.LogWarning(ex, "Could not record a backfill failure for run {RunId}", runId);
        }
    }

    private async Task<bool> IsCancellationRequestedAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.ImageVariantBackfillRuns
            .AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(run => run.CancellationRequestedAt)
            .FirstOrDefaultAsync() != null;
    }

    private async Task FlushProgressAsync(int runId, RunCounters counters)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.ImageVariantBackfillRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (run == null)
            return;

        ApplyCounters(run, counters);
        await context.SaveChangesAsync();
    }

    private async Task FinalizeAsync(
        int runId, RunCounters counters, ImageVariantBackfillStatus status, string? failureMessage)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.ImageVariantBackfillRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (run == null)
            return;

        ApplyCounters(run, counters);
        run.Status = status;
        run.CompletedAt = DateTime.UtcNow;
        run.FailureMessage = Truncate(failureMessage, 2000);

        // Releasing the singleton lock is the last thing that happens, and it happens on every exit
        // path - otherwise a crashed run blocks the feature until someone edits the table by hand.
        run.ActiveLockKey = null;

        await context.SaveChangesAsync();
    }

    private static void ApplyCounters(ImageVariantBackfillRun run, RunCounters counters)
    {
        lock (counters)
        {
            run.ProcessedCount = counters.Processed;
            run.GeneratedCount = counters.Generated;
            run.SkippedCount = counters.Skipped;
            run.FailedCount = counters.Failed;
            run.VariantBlobCount = counters.VariantBlobs;
            run.BytesWritten = counters.Bytes;
            run.UndersizedSourceCount = counters.Undersized;
            run.LegacySharingImagesRemoved = counters.LegacySharingImagesRemoved;
        }
    }

    private async Task PurgeOldRunsAsync()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RunRetentionDays);
            await using var context = await _contextFactory.CreateDbContextAsync();
            var stale = await context.ImageVariantBackfillRuns
                .Where(run => run.CreatedAt < cutoff)
                .ToListAsync();

            if (stale.Count == 0)
                return;

            context.ImageVariantBackfillRuns.RemoveRange(stale);
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not purge old image-variant backfill runs");
        }
    }

    /// <summary>
    /// How many images may be encoded at once. Two by default: each worker holds a fully decoded
    /// bitmap - up to about 36 MB for a 3000x3000 source - and Hangfire runs in-process with the web
    /// app, so this competes directly with request serving.
    /// </summary>
    private int ResolveMaxParallelEncodes()
    {
        var configured = _configuration.GetValue<int?>("ImageVariants:MaxParallelEncodes")
            ?? DefaultMaxParallelEncodes;

        return Math.Clamp(configured, MinParallelEncodes, MaxParallelEncodes);
    }

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    private sealed record WorkItem(ImageVariantBackfillItemKind Kind, int EntityId, string BlobPath);

    /// <summary>
    /// Shared across the encode workers. Every field is read and written under <c>lock (counters)</c>;
    /// the Read* helpers exist so the enqueueing loop can sample progress without duplicating that.
    /// </summary>
    private sealed class RunCounters
    {
        public int Processed;
        public int Generated;
        public int Skipped;
        public int Failed;
        public int VariantBlobs;
        public long Bytes;
        public int Undersized;
        public int LegacySharingImagesRemoved;
        public int ConsecutiveFailures;
        public bool Aborted;

        public int ReadProcessed()
        {
            lock (this) return Processed;
        }

        public bool ReadAborted()
        {
            lock (this) return Aborted;
        }
    }

    private readonly record struct ItemOutcome(
        bool Succeeded, bool Skipped, int VariantCount, long BytesWritten, bool UndersizedSource)
    {
        public static ItemOutcome Success(int variantCount, long bytes, bool undersized)
            => new(true, false, variantCount, bytes, undersized);

        public static ItemOutcome Failure() => new(false, false, 0, 0, false);

        /// <summary>The item is no longer there to process. Neither a success nor something to fix.</summary>
        public static ItemOutcome Skip() => new(false, true, 0, 0, false);
    }
}
