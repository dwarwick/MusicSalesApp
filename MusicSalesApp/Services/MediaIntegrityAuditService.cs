using System.Net;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public sealed class MediaIntegrityAuditService : IMediaIntegrityAuditService
{
    private const int BatchSize = 25;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IAzureStorageService _storage;
    private readonly IMusicService _music;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<MediaIntegrityAuditService> _logger;

    public MediaIntegrityAuditService(
        IDbContextFactory<AppDbContext> contextFactory,
        IAzureStorageService storage,
        IMusicService music,
        IEmailService email,
        IConfiguration configuration,
        IBackgroundJobClient jobs,
        ILogger<MediaIntegrityAuditService> logger)
    {
        _contextFactory = contextFactory;
        _storage = storage;
        _music = music;
        _email = email;
        _configuration = configuration;
        _jobs = jobs;
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

    [AutomaticRetry(Attempts = 2)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task RunAsync(int runId)
    {
        try
        {
            await ExecuteAsync(runId);
            await SendCompletionNotificationsAsync(runId);
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
        List<int> songIds;
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

            if (mode == MediaAuditMode.QuarantineConfirmedFailures)
            {
                songIds = await context.MediaIntegrityAuditItems
                    .Where(item => item.AuditRunId == run.SourceRunId
                        && item.Outcome == MediaAuditOutcome.ConfirmedUnplayable
                        && item.SongMetadataId.HasValue
                        && item.SongMetadata.IsActive
                        && item.SongMetadata.IsEnabled)
                    .Select(item => item.SongMetadataId!.Value)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToListAsync();
            }
            else
            {
                songIds = await context.SongMetadata
                    .Where(song => song.IsActive && song.IsEnabled && !song.IsAlbumCover
                        && song.Mp3BlobPath != null && song.Mp3BlobPath != string.Empty)
                    .Select(song => song.Id)
                    .OrderBy(id => id)
                    .ToListAsync();
            }

            var processed = await context.MediaIntegrityAuditItems
                .Where(item => item.AuditRunId == runId && item.SongMetadataId.HasValue)
                .Select(item => item.SongMetadataId!.Value)
                .ToListAsync();
            songIds = songIds.Except(processed).ToList();
            run.CandidateCount = processed.Count + songIds.Count;
            await context.SaveChangesAsync();
        }

        foreach (var batch in songIds.Chunk(BatchSize))
        {
            foreach (var songId in batch)
            {
                await AuditSongAsync(runId, songId, mode);
            }

            await RefreshTotalsAsync(runId);
        }

        await RefreshTotalsAsync(runId, complete: true);
    }

    private async Task AuditSongAsync(int runId, int songId, MediaAuditMode mode)
    {
        SongMetadata song;
        await using (var readContext = await _contextFactory.CreateDbContextAsync())
        {
            song = await readContext.SongMetadata.AsNoTracking()
                .Include(item => item.Creator).ThenInclude(creator => creator.User)
                .FirstOrDefaultAsync(item => item.Id == songId);
        }
        if (song == null || string.IsNullOrWhiteSpace(song.Mp3BlobPath))
        {
            return;
        }

        var item = await InspectWithConfirmationAsync(runId, song);

        await using var context = await _contextFactory.CreateDbContextAsync();
        var trackedSong = await context.SongMetadata.FindAsync(songId);
        if (trackedSong == null)
        {
            return;
        }

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

        context.MediaIntegrityAuditItems.Add(item);
        await context.SaveChangesAsync();
    }

    private async Task<MediaIntegrityAuditItem> InspectWithConfirmationAsync(
        int runId,
        SongMetadata song)
    {
        var first = await InspectOnceAsync(runId, song, 1);
        if (first.Outcome != MediaAuditOutcome.ConfirmedUnplayable)
        {
            return first;
        }

        var second = await InspectOnceAsync(runId, song, 2);
        if (second.Outcome == MediaAuditOutcome.ConfirmedUnplayable)
        {
            second.Attempts = 2;
            return second;
        }

        second.Outcome = MediaAuditOutcome.Inconclusive;
        second.FailureCode = "FailureNotReproduced";
        second.Diagnostic = "The suspected failure was not reproduced on a fresh Azure stream.";
        second.Attempts = 2;
        return second;
    }

    private async Task<MediaIntegrityAuditItem> InspectOnceAsync(
        int runId,
        SongMetadata song,
        int attempt)
    {
        var item = NewItem(runId, song, attempt);
        string tempPath = null;
        try
        {
            await PopulateOriginalSourceEvidenceAsync(item, song);
            var properties = await _storage.GetFileInfoAsync(song.Mp3BlobPath!);
            if (properties == null)
            {
                return Confirmed(item, "PlaybackBlobMissing", "The registered playback blob does not exist.");
            }

            item.BlobLength = properties.Length;
            item.ContentType = properties.ContentType;
            item.ETag = properties.ETag;
            item.BlobLastModified = properties.LastModified;
            tempPath = Path.Combine(Path.GetTempPath(), $"media-audit-{Guid.NewGuid():N}.tmp");
            long copied = 0;
            await using (var source = await _storage.OpenReadAsync(song.Mp3BlobPath!))
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    copied += read;
                }
            }
            if (copied != properties.Length)
            {
                return Confirmed(item, "LengthMismatch", $"Azure reported {properties.Length} bytes but {copied} bytes were copied.");
            }

            await using var stream = File.OpenRead(tempPath);
            MediaFileContentValidator.AudioContentMatchesExtension(stream, song.Mp3BlobPath!, out var detected);
            item.DetectedFormat = detected;
            stream.Position = 0;
            var decode = await _music.ValidateAudioDecodeAsync(stream, song.Mp3BlobPath!);
            item.DecodedDuration = decode.Duration;
            if (decode.Status == AudioDecodeStatus.Inconclusive)
            {
                item.Outcome = MediaAuditOutcome.Inconclusive;
                item.FailureCode = decode.FailureCode ?? "DecoderInfrastructureFailure";
                item.Diagnostic = decode.Diagnostic;
                return item;
            }
            if (decode.Status != AudioDecodeStatus.Playable
                || !decode.Duration.HasValue
                || decode.Duration <= 0)
            {
                return Confirmed(
                    item,
                    decode.FailureCode ?? "DecoderRejected",
                    decode.Diagnostic ?? "FFmpeg could not completely decode an audio stream with positive duration.");
            }

            var metadataMissing = string.IsNullOrWhiteSpace(song.SongTitle)
                || !song.TrackLength.HasValue || song.TrackLength <= 0;
            // Filenames are no longer constrained, so only the container and content type are
            // worth warning about here.
            var containerWarning =
                !string.Equals(detected, ".mp3", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(properties.ContentType, "audio/mpeg", StringComparison.OrdinalIgnoreCase);
            item.Outcome = metadataMissing
                ? MediaAuditOutcome.MetadataRepairable
                : containerWarning
                    ? MediaAuditOutcome.NamingWarning
                    : item.IsOriginalSourceMissing
                        ? MediaAuditOutcome.OriginalSourceMissing
                        : MediaAuditOutcome.Healthy;
            if (item.IsOriginalSourceMissing)
            {
                item.FailureCode = "OriginalSourceMissing";
                item.Diagnostic = string.IsNullOrWhiteSpace(song.OriginalAudioBlobPath)
                    ? "This legacy record has no retained original source registered."
                    : "The retained original source path is registered but the blob is missing.";
            }
            return item;
        }
        catch (Exception ex)
        {
            item.Outcome = MediaAuditOutcome.Inconclusive;
            item.FailureCode = ex is TimeoutException ? "Timeout" : "InfrastructureFailure";
            item.Diagnostic = SanitizeDiagnostic(ex);
            return item;
        }
        finally
        {
            if (tempPath != null)
            {
                TempFileHelper.TryDelete(tempPath, _logger);
            }
        }
    }

    private async Task PopulateOriginalSourceEvidenceAsync(
        MediaIntegrityAuditItem item,
        SongMetadata song)
    {
        if (string.IsNullOrWhiteSpace(song.OriginalAudioBlobPath))
        {
            item.IsOriginalSourceMissing = true;
            return;
        }

        try
        {
            item.IsOriginalSourceMissing = !await _storage.ExistsAsync(song.OriginalAudioBlobPath);
        }
        catch (Exception ex)
        {
            item.IsOriginalSourceCheckInconclusive = true;
            _logger.LogWarning(
                ex,
                "Unable to verify original source {OriginalPath} for song {SongId}",
                song.OriginalAudioBlobPath,
                song.Id);
        }
    }

    private static MediaIntegrityAuditItem NewItem(int runId, SongMetadata song, int attempt)
        => new()
        {
            AuditRunId = runId,
            SongMetadataId = song.Id,
            EffectiveTitle = SongTitleHelper.GetEffectiveTitle(song.SongTitle, song.Mp3BlobPath, song.BlobPath),
            PlaybackBlobPath = song.Mp3BlobPath,
            OriginalAudioBlobPath = song.OriginalAudioBlobPath,
            Attempts = attempt,
            CheckedAt = DateTime.UtcNow
        };

    private static MediaIntegrityAuditItem Confirmed(
        MediaIntegrityAuditItem item,
        string code,
        string diagnostic)
    {
        item.Outcome = MediaAuditOutcome.ConfirmedUnplayable;
        item.FailureCode = code;
        item.Diagnostic = diagnostic;
        return item;
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
            run.Status = MediaAuditRunStatus.Completed;
            run.ActiveLockKey = null;
            run.CompletedAt = DateTime.UtcNow;
        }
        await context.SaveChangesAsync();
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
