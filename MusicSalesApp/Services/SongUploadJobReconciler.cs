#nullable enable
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Closes out audio-processing work that stopped moving.
/// </summary>
public interface ISongUploadJobReconciler
{
    Task ReconcileAsync();
}

/// <summary>
/// The safety net under a pipeline that now spans two clouds.
///
/// <para>
/// A queue message can exhaust its retries and land in the poison queue, or the Function instance
/// holding it can die mid-transcode. Either way no callback ever arrives, and without this the
/// creator would watch a progress bar that can never move and an audit run would sit Running
/// forever - blocking every future run through the single-run lock.
/// </para>
///
/// <para>
/// It keys off <see cref="SongUploadJob.StepUpdatedAt"/> rather than job age: a legitimately slow
/// transcode keeps reporting steps, so it is not mistaken for a dead one.
/// </para>
/// </summary>
public sealed class SongUploadJobReconciler : ISongUploadJobReconciler
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IMediaProcessingCompletionService _completionService;
    private readonly IMediaIntegrityAuditService _auditService;
    private readonly IOptions<MediaProcessingOptions> _options;
    private readonly ILogger<SongUploadJobReconciler> _logger;

    public SongUploadJobReconciler(
        IDbContextFactory<AppDbContext> contextFactory,
        IMediaProcessingCompletionService completionService,
        IMediaIntegrityAuditService auditService,
        IOptions<MediaProcessingOptions> options,
        ILogger<SongUploadJobReconciler> logger)
    {
        _contextFactory = contextFactory;
        _completionService = completionService;
        _auditService = auditService;
        _options = options;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 1)]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ReconcileAsync()
    {
        var cutoff = DateTime.UtcNow - _options.Value.StalledJobTimeout;

        List<Guid> stalled;
        await using (var context = await _contextFactory.CreateDbContextAsync())
        {
            stalled = await context.SongUploadJobs
                .Where(job => job.Status == SongUploadJobStatus.Queued
                    || job.Status == SongUploadJobStatus.Processing)
                .Where(job => job.StepUpdatedAt < cutoff)
                .Select(job => job.MediaGuid)
                .ToListAsync();
        }

        foreach (var jobId in stalled)
        {
            _logger.LogWarning("Upload job {JobId} stopped reporting progress; marking it failed.", jobId);
            await _completionService.FailAsync(
                jobId,
                MediaProcessingFailureCodes.Abandoned,
                "Processing stopped unexpectedly. Please try uploading this song again.");
        }

        await _auditService.ReconcileStalledRunsAsync();
    }
}
