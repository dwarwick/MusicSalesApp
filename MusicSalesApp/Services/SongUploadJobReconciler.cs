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
/// The last resort under a pipeline that spans two clouds - and only the last resort.
///
/// <para>
/// <b>A message that exhausts its retries is no longer this class's problem.</b> It lands in the
/// poison queue, where <c>HandleTranscodePoisonFunction</c> reports it from the authoritative event,
/// promptly and correctly. What is left here is the narrow set of cases where no such event will
/// ever arrive: a queue purged by hand, an enqueue that reported success but lost its message, or a
/// Function App stopped long enough that messages sit un-dequeued. Audit runs are swept the same way,
/// so one does not sit Running forever and block every future run through the single-run lock.
/// </para>
///
/// <para>
/// It keys off <see cref="SongUploadJob.StepUpdatedAt"/> rather than job age, so a legitimately slow
/// transcode is not mistaken for a dead one. <b>That signal is weaker than it looks</b>, which is
/// why this is a backstop with a two-hour timeout rather than the primary detector it used to be:
/// liveness is refreshed only by the Function POSTing progress, and those posts swallow their own
/// failures by design. A web app restarting mid-batch therefore looks exactly like a dead Function.
/// When this ran at twenty minutes it would fail every song in flight during a deploy - telling
/// creators their uploads had broken, and deleting the staging that the perfectly healthy Functions
/// still working on them were about to need.
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
