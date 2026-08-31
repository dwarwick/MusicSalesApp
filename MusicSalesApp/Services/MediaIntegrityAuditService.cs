using System.Net;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Drives the media-integrity audit.
///
/// <para>
/// This no longer decodes anything itself. FFmpeg runs in an Azure Function, so a run here is a
/// <em>dispatcher</em>: it works out the candidate songs, puts one probe message on the queue for
/// each, and returns. Results arrive later through
/// <see cref="RecordProbedItemAsync"/>, and the run completes on the last one rather than when the
/// Hangfire job returns.
/// </para>
/// </summary>
public sealed class MediaIntegrityAuditService : IMediaIntegrityAuditService
{
    private const int BatchSize = 25;

    /// <summary>
    /// How long a Running audit may go without a single probe result before it is closed out with
    /// whatever arrived. Generous, because a slow queue is normal and a large catalogue can take
    /// hours in total - what this catches is a run that has stopped moving altogether.
    /// </summary>
    private static readonly TimeSpan StalledRunTimeout = TimeSpan.FromHours(2);

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IMediaProcessingQueueClient _queueClient;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly IBackgroundJobClient _jobs;
    private readonly IHlsPackageIntegrityChecker _hlsPackages;
    private readonly ILogger<MediaIntegrityAuditService> _logger;

    public MediaIntegrityAuditService(
        IDbContextFactory<AppDbContext> contextFactory,
        IMediaProcessingQueueClient queueClient,
        IEmailService email,
        IConfiguration configuration,
        IBackgroundJobClient jobs,
        IHlsPackageIntegrityChecker hlsPackages,
        ILogger<MediaIntegrityAuditService> logger)
    {
        _contextFactory = contextFactory;
        _queueClient = queueClient;
        _email = email;
        _configuration = configuration;
        _jobs = jobs;
        _hlsPackages = hlsPackages;
        _logger = logger;
    }

    public async Task<MediaIntegrityAuditRun> StartAsync(
        MediaAuditMode mode,
        int? initiatedByUserId,
        string initiatedByEmail,
        int? sourceRunId = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        if (await context.MediaIntegrityAuditRuns.AnyAsync(run =>
                run.Status == MediaAuditRunStatus.Queued || run.Status == MediaAuditRunStatus.Running))
        {
            throw new InvalidOperationException("A media-integrity audit is already queued or running.");
        }

        if (mode == MediaAuditMode.QuarantineConfirmedFailures)
        {
            if (!sourceRunId.HasValue)
            {
                throw new InvalidOperationException("Quarantine requires a completed source run.");
            }

            var sourceIsValid = await context.MediaIntegrityAuditRuns.AnyAsync(run =>
                run.Id == sourceRunId.Value
                && run.Status == MediaAuditRunStatus.Completed
                && run.Items.Any(item => item.Outcome == MediaAuditOutcome.ConfirmedUnplayable));
            if (!sourceIsValid)
            {
                throw new InvalidOperationException("The selected source run has no confirmed failures.");
            }
        }

        var auditRun = new MediaIntegrityAuditRun
        {
            Mode = mode,
            SourceRunId = sourceRunId,
            InitiatedByUserId = initiatedByUserId,
            InitiatedByEmail = initiatedByEmail?.Trim(),
            Status = MediaAuditRunStatus.Queued,
            ActiveLockKey = 1
        };
        context.MediaIntegrityAuditRuns.Add(auditRun);
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException("A media-integrity audit is already queued or running.", ex);
        }

        try
        {
            auditRun.HangfireJobId = _jobs.Enqueue<IMediaIntegrityAuditService>(
                service => service.RunAsync(auditRun.Id));
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            auditRun.Status = MediaAuditRunStatus.Failed;
            auditRun.ActiveLockKey = null;
            auditRun.CompletedAt = DateTime.UtcNow;
            auditRun.FailureMessage = SanitizeDiagnostic(ex);
            await context.SaveChangesAsync();
            throw new InvalidOperationException("The audit run could not be queued in Hangfire.", ex);
        }
        return auditRun;
    }

    public async Task<List<MediaIntegrityAuditRun>> GetRunsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.MediaIntegrityAuditRuns.AsNoTracking()
            .OrderByDescending(run => run.CreatedAt)
            .Take(100)
            .ToListAsync();
    }

    public async Task<MediaIntegrityAuditRun> GetRunAsync(int runId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.MediaIntegrityAuditRuns.AsNoTracking()
            .Include(run => run.Items.OrderBy(item => item.Id))
            .FirstOrDefaultAsync(run => run.Id == runId);
    }

    public async Task RunAsync(int runId)
    {
        try
        {
            // Dispatch only. The run is *not* finished when this returns - decoding happens in the
            // Azure Function, and the run completes on the last probe result to come back through
            // RecordProbedItemAsync. The completion notifications fire from there.
            await ExecuteAsync(runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Media-integrity audit {RunId} aborted", runId);
            await MarkFailedAsync(runId, ex);
            await SendFailureNotificationAsync(runId);
            throw;
        }
    }

    private async Task ExecuteAsync(int runId)
    {
        if (!_queueClient.IsConfigured)
        {
            // Nothing can be dispatched. Failing here is honest and releases the single-run lock,
            // rather than stranding the run Running until the reconciler two hours later.
            throw new InvalidOperationException(
                "Audio processing is not configured, so the media-integrity audit cannot dispatch probes.");
        }

        List<AudioProbeRequest> probes;
        MediaAuditMode mode;

        await using (var context = await _contextFactory.CreateDbContextAsync())
        {
            var run = await context.MediaIntegrityAuditRuns.FindAsync(runId)
                ?? throw new InvalidOperationException($"Audit run {runId} was not found.");
            if (run.Status == MediaAuditRunStatus.Completed)
            {
                return;
            }

            run.Status = MediaAuditRunStatus.Running;
            run.ActiveLockKey = 1;
            run.StartedAt ??= DateTime.UtcNow;
            mode = run.Mode;

            // Candidates and their blob paths are resolved in ONE read, and CandidateCount is set
            // from what actually gets dispatched. Selecting ids and paths in separate queries let
            // them diverge: a song deleted between the two produced fewer probes than candidates,
            // so the completion condition (ProcessedCount >= CandidateCount) could never hold and
            // the run held its lock until the reconciler closed it hours later.
            List<AudioProbeRequest> candidates;
            if (mode == MediaAuditMode.QuarantineConfirmedFailures)
            {
                candidates = await context.MediaIntegrityAuditItems
                    .Where(item => item.AuditRunId == run.SourceRunId
                        && item.Outcome == MediaAuditOutcome.ConfirmedUnplayable
                        && item.SongMetadataId.HasValue
                        && item.SongMetadata.IsActive
                        && item.SongMetadata.IsEnabled
                        // The report-only branch has always had this guard; without it here, a song
                        // whose playback blob was since cleared dispatches a probe with a null path,
                        // which throws inside the Function and poisons instead of calling back.
                        && item.SongMetadata.Mp3BlobPath != null
                        && item.SongMetadata.Mp3BlobPath != string.Empty)
                    .Select(item => new AudioProbeRequest
                    {
                        Kind = AudioProbeKind.MediaIntegrityAudit,
                        AuditRunId = runId,
                        SongMetadataId = item.SongMetadataId!.Value,
                        BlobPath = item.SongMetadata.Mp3BlobPath,
                        Attempt = 1
                    })
                    .ToListAsync();

                candidates = candidates
                    .GroupBy(candidate => candidate.SongMetadataId)
                    .Select(group => group.First())
                    .OrderBy(candidate => candidate.SongMetadataId)
                    .ToList();
            }
            else
            {
                candidates = await context.SongMetadata
                    .Where(song => song.IsActive && song.IsEnabled && !song.IsAlbumCover
                        && song.Mp3BlobPath != null && song.Mp3BlobPath != string.Empty)
                    .OrderBy(song => song.Id)
                    .Select(song => new AudioProbeRequest
                    {
                        Kind = AudioProbeKind.MediaIntegrityAudit,
                        AuditRunId = runId,
                        SongMetadataId = song.Id,
                        BlobPath = song.Mp3BlobPath,
                        Attempt = 1
                    })
                    .ToListAsync();
            }

            var processed = await context.MediaIntegrityAuditItems
                .Where(item => item.AuditRunId == runId && item.SongMetadataId.HasValue)
                .Select(item => item.SongMetadataId!.Value)
                .ToListAsync();

            probes = candidates.Where(candidate => !processed.Contains(candidate.SongMetadataId)).ToList();
            run.CandidateCount = processed.Count + probes.Count;
            await context.SaveChangesAsync();
        }

        if (probes.Count == 0)
        {
            // Nothing left to probe - an empty catalogue, or a resumed run that had already
            // finished its work. There will be no callback to complete the run, so close it here.
            await RefreshTotalsAsync(runId, complete: true);
            await SendCompletionNotificationsAsync(runId);
            return;
        }

        foreach (var probe in probes)
        {
            probe.ProbeId = Guid.NewGuid();
        }

        // Chunked so a very large catalogue does not hold one long-running enqueue loop open, and
        // so partial progress survives an interruption - anything not enqueued is left pending and
        // swept up by the reconciler.
        foreach (var batch in probes.Chunk(BatchSize))
        {
            await _queueClient.EnqueueProbesAsync(batch);
        }

        _logger.LogInformation(
            "Media-integrity audit {RunId} dispatched {Count} probe(s) in {Mode} mode",
            runId,
            probes.Count,
            mode);
    }

    /// <inheritdoc />
    public async Task RecordProbedItemAsync(
        MediaIntegrityAuditItem item,
        MediaAuditMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var trackedSong = item.SongMetadataId.HasValue
                ? await context.SongMetadata.FindAsync([item.SongMetadataId.Value], cancellationToken)
                : null;

            if (trackedSong is not null)
            {
                if (mode == MediaAuditMode.RepairSafeMetadata
                    && item.Outcome == MediaAuditOutcome.MetadataRepairable
                    && item.DecodedDuration > 0)
                {
                    if (string.IsNullOrWhiteSpace(trackedSong.SongTitle))
                    {
                        var repairedTitle = SongTitleHelper.GetEffectiveTitle(
                            trackedSong.SongTitle,
                            trackedSong.Mp3BlobPath,
                            trackedSong.BlobPath);
                        trackedSong.SongTitle = string.IsNullOrWhiteSpace(repairedTitle)
                            ? $"Song {trackedSong.Id}"
                            : repairedTitle[..Math.Min(repairedTitle.Length, SongTitleHelper.MaxTitleLength)];
                        if (string.IsNullOrWhiteSpace(repairedTitle))
                        {
                            trackedSong.StatusReason = "Media integrity repair: title requires manual correction.";
                        }
                    }

                    if (!trackedSong.TrackLength.HasValue || trackedSong.TrackLength <= 0)
                    {
                        trackedSong.TrackLength = item.DecodedDuration;
                    }

                    trackedSong.UpdatedAt = DateTime.UtcNow;
                    item.MetadataRepaired = true;
                }

                if (mode == MediaAuditMode.QuarantineConfirmedFailures
                    && item.Outcome == MediaAuditOutcome.ConfirmedUnplayable)
                {
                    trackedSong.IsEnabled = false;
                    trackedSong.IsActive = true;
                    trackedSong.StatusReason = MediaIntegrityConstants.QuarantineReason;
                    trackedSong.UpdatedAt = DateTime.UtcNow;
                    context.SongStatusHistories.Add(new SongStatusHistory
                    {
                        SongMetadataId = trackedSong.Id,
                        IsEnabled = false,
                        Reason = MediaIntegrityConstants.QuarantineReason,
                        ChangedByUserId = null,
                        ChangedAt = DateTime.UtcNow
                    });
                    item.Quarantined = true;
                }
            }

            context.MediaIntegrityAuditItems.Add(item);
            await context.SaveChangesAsync(cancellationToken);
        }

        await CompleteIfFinishedAsync(item.AuditRunId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordSkippedItemAsync(
        int runId,
        int songMetadataId,
        CancellationToken cancellationToken = default)
    {
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var alreadyRecorded = await context.MediaIntegrityAuditItems.AnyAsync(
                existing => existing.AuditRunId == runId && existing.SongMetadataId == songMetadataId,
                cancellationToken);
            if (alreadyRecorded)
            {
                return;
            }

            context.MediaIntegrityAuditItems.Add(new MediaIntegrityAuditItem
            {
                AuditRunId = runId,
                SongMetadataId = songMetadataId,
                Outcome = MediaAuditOutcome.Inconclusive,
                FailureCode = "SongUnavailable",
                Diagnostic = "The song was removed or lost its playback blob before it could be checked.",
                Attempts = 1,
                CheckedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        await CompleteIfFinishedAsync(runId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReconcileStalledRunsAsync(CancellationToken cancellationToken = default)
    {
        List<int> stalledRunIds;
        var cutoff = DateTime.UtcNow - StalledRunTimeout;

        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            // "Stalled" means no item has landed recently, not merely that the run is old - a large
            // catalogue can legitimately take hours to work through.
            stalledRunIds = await context.MediaIntegrityAuditRuns
                .Where(run => run.Status == MediaAuditRunStatus.Running)
                .Where(run => !context.MediaIntegrityAuditItems
                    .Where(item => item.AuditRunId == run.Id)
                    .Any(item => item.CheckedAt > cutoff))
                .Where(run => (run.StartedAt ?? run.CreatedAt) < cutoff)
                .Select(run => run.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var runId in stalledRunIds)
        {
            _logger.LogWarning(
                "Media-integrity audit {RunId} stopped receiving probe results; completing it with what arrived.",
                runId);
            await RefreshTotalsAsync(runId, complete: true);
            await SendCompletionNotificationsAsync(runId);
        }
    }

    private async Task CompleteIfFinishedAsync(int runId, CancellationToken cancellationToken)
    {
        await RefreshTotalsAsync(runId);

        bool finished;
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var run = await context.MediaIntegrityAuditRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == runId, cancellationToken);
            finished = run is not null
                && run.Status == MediaAuditRunStatus.Running
                && run.CandidateCount > 0
                && run.ProcessedCount >= run.CandidateCount;
        }

        if (!finished)
        {
            return;
        }

        await RefreshTotalsAsync(runId, complete: true);
        await SendCompletionNotificationsAsync(runId);
    }


    private async Task RefreshTotalsAsync(int runId, bool complete = false)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.MediaIntegrityAuditRuns.FindAsync(runId);
        if (run == null) return;
        var items = context.MediaIntegrityAuditItems.Where(item => item.AuditRunId == runId);
        run.ProcessedCount = await items.CountAsync();
        run.HealthyCount = await items.CountAsync(item => item.Outcome == MediaAuditOutcome.Healthy);
        run.RepairableCount = await items.CountAsync(item => item.Outcome == MediaAuditOutcome.MetadataRepairable);
        run.NamingWarningCount = await items.CountAsync(item => item.Outcome == MediaAuditOutcome.NamingWarning);
        run.OriginalSourceMissingCount = await items.CountAsync(item => item.IsOriginalSourceMissing);
        run.ConfirmedUnplayableCount = await items.CountAsync(item => item.Outcome == MediaAuditOutcome.ConfirmedUnplayable);
        run.InconclusiveCount = await items.CountAsync(item => item.Outcome == MediaAuditOutcome.Inconclusive);
        run.RepairedCount = await items.CountAsync(item => item.MetadataRepaired);
        run.QuarantinedCount = await items.CountAsync(item => item.Quarantined);
        if (complete)
        {
            // Swept once, as the run closes, rather than per probe callback. It is a HEAD request per
            // song against a container the probes never touch, so there is nothing to interleave with
            // and no reason to repeat it as each result trickles in.
            //
            // Failure here must not fail the audit: everything above is already established, and the
            // package sweep is an additional question rather than the point of the run.
            try
            {
                var packages = await _hlsPackages.CheckAsync();
                run.HlsPackagesCheckedCount = packages.CheckedCount;
                run.HlsPackagesMissingCount = packages.ProblemCount;
                run.HlsPackageCheckInconclusive = packages.WasInconclusive;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The HLS package check failed during audit run {RunId}.", runId);
                run.HlsPackageCheckInconclusive = true;
            }

            run.Status = MediaAuditRunStatus.Completed;
            run.ActiveLockKey = null;
            run.CompletedAt = DateTime.UtcNow;
        }
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// The encrypted-HLS line of the completion email, and only when there is something to say.
    ///
    /// <para>
    /// Silent on a clean sweep on purpose. This mail is read at a glance, and a line that says
    /// "0 missing" every night is a line nobody reads on the night it says something else.
    /// </para>
    /// </summary>
    private static string HlsPackageParagraph(MediaIntegrityAuditRun run)
    {
        if (run.HlsPackageCheckInconclusive)
        {
            return "<p><strong>Encrypted HLS packages could not be checked</strong> - storage was "
                + "unreachable during the sweep, so this run says nothing about whether the "
                + "catalogue is playable.</p>";
        }

        if (run.HlsPackagesMissingCount == 0)
        {
            return string.Empty;
        }

        return $"<p><strong>{run.HlsPackagesMissingCount} of {run.HlsPackagesCheckedCount} songs have "
            + "an encrypted-HLS package recorded that storage does not hold, or does not hold "
            + "completely.</strong> Those songs will not play - the stream endpoint answers 503 for "
            + "each of them. This is what a restore that missed the streaming container looks like. "
            + "Fix it by running the HLS packaging backfill with scope <em>RepairMissing</em>, which "
            + "selects on the package being absent and needs no database edit.</p>";
    }

    private async Task SendCompletionNotificationsAsync(int runId)
    {
        var run = await GetRunAsync(runId);
        if (run == null) return;
        var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var admin = _configuration[AppSettingKeys.EmailAdminEmail];
        if (!string.IsNullOrWhiteSpace(admin)) recipients.Add(admin);
        if (!string.IsNullOrWhiteSpace(run.InitiatedByEmail)) recipients.Add(run.InitiatedByEmail);
        var url = $"{_email.GetAppBaseUrl().TrimEnd('/')}/admin/media-integrity?runId={run.Id}";
        var body = $"{_email.GetEmailLogoHtml()}<h2>Media integrity audit completed</h2>"
            + $"<p>Mode: <strong>{WebUtility.HtmlEncode(run.Mode.ToString())}</strong>; Run: {run.Id}</p>"
            + $"<p>Processed {run.ProcessedCount}; healthy {run.HealthyCount}; repaired {run.RepairedCount}; "
            + $"missing originals {run.OriginalSourceMissingCount}; confirmed failures {run.ConfirmedUnplayableCount}; "
            + $"inconclusive {run.InconclusiveCount}; quarantined {run.QuarantinedCount}; "
            + $"notification failures {run.NotificationFailureCount}.</p>"
            + HlsPackageParagraph(run)
            + $"<p><a href='{url}'>View audit results</a></p>";
        foreach (var recipient in recipients)
        {
            await using var notificationContext = await _contextFactory.CreateDbContextAsync();
            var notification = await notificationContext.MediaIntegrityAuditNotifications
                .SingleOrDefaultAsync(item => item.AuditRunId == runId
                    && item.NotificationType == MediaIntegrityNotificationTypes.AdminCompletion
                    && item.Recipient == recipient);
            if (notification?.Sent == true)
            {
                continue;
            }

            notification ??= new MediaIntegrityAuditNotification
            {
                AuditRunId = runId,
                NotificationType = MediaIntegrityNotificationTypes.AdminCompletion,
                Recipient = recipient
            };
            if (notification.Id == 0)
                notificationContext.MediaIntegrityAuditNotifications.Add(notification);
            notification.Attempts++;
            try
            {
                notification.Sent = await _email.SendEmailAsync(
                    recipient,
                    $"Media integrity audit {run.Id} completed",
                    body);
                notification.SentAt = notification.Sent ? DateTime.UtcNow : null;
                notification.LastError = notification.Sent ? null : "The email provider returned an unsuccessful result.";
            }
            catch (Exception ex)
            {
                notification.Sent = false;
                notification.LastError = SanitizeDiagnostic(ex);
            }
            if (!notification.Sent)
            {
                var notificationRun = await notificationContext.MediaIntegrityAuditRuns.FindAsync(runId);
                if (notificationRun != null)
                    notificationRun.NotificationFailureCount++;
            }
            await notificationContext.SaveChangesAsync();
        }

        await using (var statusContext = await _contextFactory.CreateDbContextAsync())
        {
            var statusRun = await statusContext.MediaIntegrityAuditRuns.FindAsync(runId);
            if (statusRun != null)
            {
                statusRun.AdminNotificationSent = recipients.Count == 0
                    || await statusContext.MediaIntegrityAuditNotifications.CountAsync(item =>
                        item.AuditRunId == runId
                        && item.NotificationType == MediaIntegrityNotificationTypes.AdminCompletion
                        && item.Sent) == recipients.Count;
                await statusContext.SaveChangesAsync();
            }
        }

        if (run.Mode == MediaAuditMode.QuarantineConfirmedFailures)
        {
            await SendCreatorNotificationsAsync(run);
        }
    }

    private async Task<bool> SendCreatorNotificationsAsync(MediaIntegrityAuditRun run)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var pendingItems = await context.MediaIntegrityAuditItems
            .Where(item => item.AuditRunId == run.Id && item.Quarantined && !item.CreatorNotificationSent)
            .Include(item => item.SongMetadata).ThenInclude(song => song.Creator).ThenInclude(creator => creator.User)
            .ToListAsync();
        var groups = pendingItems.GroupBy(item => item.SongMetadata?.Creator?.User?.Email).ToList();
        var allSent = true;
        var failureCount = 0;
        foreach (var group in groups.Where(group => !string.IsNullOrWhiteSpace(group.Key)))
        {
            var songs = string.Join("", group.Select(item =>
                $"<li>{WebUtility.HtmlEncode(item.EffectiveTitle)} — original source "
                + GetOriginalSourceEmailStatus(item) + "</li>"));
            var baseUrl = _email.GetAppBaseUrl().TrimEnd('/');
            var body = $"{_email.GetEmailLogoHtml()}<h2>One or more songs need a new audio upload</h2>"
                + "<p>Our integrity check confirmed that the stored playback file could not be decoded. This is a technical file problem, not a policy violation.</p>"
                + $"<ul>{songs}</ul><p>Please replace the affected audio from <a href='{baseUrl}/creator/songs'>Manage My Songs</a>.</p>"
                + $"<p><a href='{baseUrl}/manage-account'>Manage your email preferences</a></p>";
            var sent = await _email.SendEmailAsync(group.Key!, "Action needed: unreadable song audio", body);
            allSent &= sent;
            if (!sent) failureCount++;
            if (sent)
            {
                foreach (var item in group) item.CreatorNotificationSent = true;
                await context.SaveChangesAsync();
            }
        }
        if (failureCount > 0)
        {
            var auditRun = await context.MediaIntegrityAuditRuns.FindAsync(run.Id);
            if (auditRun != null)
            {
                auditRun.NotificationFailureCount += failureCount;
                await context.SaveChangesAsync();
            }
        }
        return allSent;
    }

    private static string GetOriginalSourceEmailStatus(MediaIntegrityAuditItem item)
        => item.IsOriginalSourceCheckInconclusive
            ? "could not be verified"
            : item.IsOriginalSourceMissing
                ? "is not retained"
                : "is retained";

    private async Task MarkFailedAsync(int runId, Exception exception)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var run = await context.MediaIntegrityAuditRuns.FindAsync(runId);
        if (run == null) return;
        if (run.Status != MediaAuditRunStatus.Completed)
        {
            run.Status = MediaAuditRunStatus.Failed;
            run.ActiveLockKey = null;
        }
        run.CompletedAt = DateTime.UtcNow;
        run.FailureMessage = SanitizeDiagnostic(exception);
        await context.SaveChangesAsync();
    }

    private async Task SendFailureNotificationAsync(int runId)
    {
        var run = await GetRunAsync(runId);
        var admin = _configuration[AppSettingKeys.EmailAdminEmail];
        if (run == null || string.IsNullOrWhiteSpace(admin)) return;
        var body = $"{_email.GetEmailLogoHtml()}<h2>Media integrity audit failed</h2>"
            + $"<p>Run {run.Id} stopped after {run.ProcessedCount} completed items. The run is resumable by retrying its Hangfire job.</p>"
            + $"<p>{WebUtility.HtmlEncode(run.FailureMessage)}</p>";
        await _email.SendEmailAsync(admin, $"Media integrity audit {run.Id} failed", body);
    }

    private static string SanitizeDiagnostic(Exception exception)
    {
        var text = $"{exception.GetType().Name}: {exception.Message}"
            .Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 2000 ? text : text[..2000];
    }
}
