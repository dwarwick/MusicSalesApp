#nullable enable
using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>Why a lyrics submission was or was not accepted.</summary>
public enum LyricsSubmissionOutcome
{
    Accepted,
    TextEmpty,
    TextTooLong,
    TooManyLines,
    SongNotFound,
    NotAllowed,
    NoAudio,
    AlreadyRunning,
    Unavailable
}

/// <summary>The result of asking for a song's lyrics to be timed.</summary>
public sealed record LyricsSubmissionResult(LyricsSubmissionOutcome Outcome, Guid? JobId, string Message)
{
    public bool Accepted => Outcome == LyricsSubmissionOutcome.Accepted;
}

/// <summary>
/// What the creator's lyrics editor talks to: stores pasted lyrics and asks for them to be timed.
/// </summary>
public interface ISongLyricsService
{
    /// <summary>Whether lyric timing is available in this environment at all.</summary>
    bool IsAvailable { get; }

    /// <summary>The current lyrics state for a song, or null if it has never had any.</summary>
    Task<SongLyrics?> GetForSongAsync(int songMetadataId, CancellationToken cancellationToken = default);

    /// <summary>The attempt currently in flight for a song, if there is one.</summary>
    Task<LyricsAlignmentJob?> GetActiveJobAsync(int songMetadataId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="blobPath"/> is a lyrics artifact a listener is allowed to fetch.
    ///
    /// <para>
    /// <b>The gate is the row's status, not the path.</b> A path that merely looks like a lyrics
    /// artifact proves nothing: timings held back as <see cref="SongLyricsStatus.NeedsReview"/> sit
    /// at exactly the same path as published ones, because the path never changes between runs.
    /// Matching on shape alone would serve every low-confidence alignment the pipeline deliberately
    /// withheld - which is the entire point of having withheld them.
    /// </para>
    ///
    /// <para>
    /// The song's own visibility is checked too, so lyrics do not outlive the song: a creator who
    /// deletes a track, or an admin who disables one, takes its lyrics with it.
    /// </para>
    /// </summary>
    Task<bool> IsPubliclyReadableAsync(string blobPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the pasted lyrics and queues an alignment attempt.
    ///
    /// <para>
    /// Writes the row before asking Hangfire to do anything, following the ordering
    /// <c>SongUploadJobService.PersistAndEnqueueAsync</c> established: work that is queued but not
    /// recorded is invisible to every backstop, whereas work that is recorded but not queued is
    /// merely late and gets swept.
    /// </para>
    /// </summary>
    Task<LyricsSubmissionResult> SubmitAsync(
        int songMetadataId,
        int creatorId,
        string lyricsText,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops an attempt that is still running, and stops the orchestration behind it.
    /// </summary>
    Task<bool> CancelAsync(int songMetadataId, int creatorId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SongLyricsService : ISongLyricsService
{
    private const string LyricsContentType = "text/plain; charset=utf-8";

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IAzureStorageService _storageService;
    private readonly IDurableTaskClient _durableTaskClient;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<SongLyricsService> _logger;

    public SongLyricsService(
        IDbContextFactory<AppDbContext> contextFactory,
        IAzureStorageService storageService,
        IDurableTaskClient durableTaskClient,
        IBackgroundJobClient backgroundJobs,
        ILogger<SongLyricsService> logger)
    {
        _contextFactory = contextFactory;
        _storageService = storageService;
        _durableTaskClient = durableTaskClient;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable => _durableTaskClient.IsConfigured;

    /// <inheritdoc />
    public async Task<SongLyrics?> GetForSongAsync(
        int songMetadataId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.SongLyrics
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.SongMetadataId == songMetadataId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<LyricsAlignmentJob?> GetActiveJobAsync(
        int songMetadataId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.LyricsAlignmentJobs
            .AsNoTracking()
            .Where(job => job.SongMetadataId == songMetadataId)
            .Where(job => job.Status == LyricsAlignmentJobStatus.Queued
                || job.Status == LyricsAlignmentJobStatus.Processing)
            .OrderByDescending(job => job.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> IsPubliclyReadableAsync(
        string blobPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return false;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Ordinal comparison, matching how MusicController's existing whitelist compares paths - a
        // case-insensitive match here would let "ABC/ABC-LYRICS.JSON" through for a blob store that
        // is case-sensitive, which is a difference in what gets served rather than a formatting one.
        return await context.SongLyrics
            .AsNoTracking()
            .Where(row => row.Status == SongLyricsStatus.Published)
            .Where(row => row.TimingsBlobPath == blobPath || row.LrcBlobPath == blobPath)
            .Join(
                context.SongMetadata.Where(song => song.IsActive && song.IsEnabled),
                lyrics => lyrics.SongMetadataId,
                song => song.Id,
                (lyrics, song) => lyrics.Id)
            .AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<LyricsSubmissionResult> SubmitAsync(
        int songMetadataId,
        int creatorId,
        string lyricsText,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return new LyricsSubmissionResult(
                LyricsSubmissionOutcome.Unavailable,
                null,
                "Lyric timing is not configured for this environment.");
        }

        var normalized = NormalizeSubmittedText(lyricsText);
        if (normalized.Length == 0)
        {
            return new LyricsSubmissionResult(
                LyricsSubmissionOutcome.TextEmpty, null, "Please paste the lyrics for this song.");
        }

        if (normalized.Length > LyricsTextLimits.MaxCharacters)
        {
            return new LyricsSubmissionResult(
                LyricsSubmissionOutcome.TextTooLong,
                null,
                $"Lyrics must be {LyricsTextLimits.MaxCharacters:N0} characters or fewer.");
        }

        var lineCount = CountLines(normalized);
        if (lineCount > LyricsTextLimits.MaxLines)
        {
            return new LyricsSubmissionResult(
                LyricsSubmissionOutcome.TooManyLines,
                null,
                $"Lyrics must be {LyricsTextLimits.MaxLines:N0} lines or fewer.");
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var song = await context.SongMetadata
            .FirstOrDefaultAsync(row => row.Id == songMetadataId, cancellationToken);

        if (song is null)
        {
            return new LyricsSubmissionResult(
                LyricsSubmissionOutcome.SongNotFound, null, "That song no longer exists.");
        }

        // The destination is derived from the song's own record rather than supplied by the caller,
        // so an authenticated user cannot write to an arbitrary blob path.
        if (song.CreatorId is null || song.CreatorId != creatorId)
        {
            return new LyricsSubmissionResult(
                LyricsSubmissionOutcome.NotAllowed, null, "You can only add lyrics to your own songs.");
        }

        if (string.IsNullOrWhiteSpace(song.Mp3BlobPath))
        {
            return new LyricsSubmissionResult(
                LyricsSubmissionOutcome.NoAudio, null, "This song has no audio to align lyrics against.");
        }

        // One attempt at a time. Superseding instead would bill two separation runs in parallel for
        // the same song, with a non-deterministic winner deciding what gets published.
        var running = await context.LyricsAlignmentJobs
            .AnyAsync(
                job => job.SongMetadataId == songMetadataId
                    && (job.Status == LyricsAlignmentJobStatus.Queued
                        || job.Status == LyricsAlignmentJobStatus.Processing),
                cancellationToken);

        if (running)
        {
            return new LyricsSubmissionResult(
                LyricsSubmissionOutcome.AlreadyRunning,
                null,
                "These lyrics are already being timed. Cancel that first if you want to start again.");
        }

        var lyricsBlobPath = SongMediaPaths.ResolveLyricsTextTarget(
            song.Id, song.MediaGuid, song.Mp3BlobPath);

        // Written before the row, and to the media container rather than staging, because this is
        // creator content: it has to survive so the song can be re-aligned without asking them to
        // paste it again, which makes it a primary blob and therefore this app's to write.
        var bytes = Encoding.UTF8.GetBytes(normalized);
        await using (var stream = new MemoryStream(bytes))
        {
            await _storageService.UploadAsync(lyricsBlobPath, stream, LyricsContentType);
        }

        var now = DateTime.UtcNow;

        var lyrics = await context.SongLyrics
            .FirstOrDefaultAsync(row => row.SongMetadataId == songMetadataId, cancellationToken);

        if (lyrics is null)
        {
            lyrics = new SongLyrics
            {
                SongMetadataId = songMetadataId,
                CreatedAt = now
            };
            context.SongLyrics.Add(lyrics);
        }

        lyrics.LyricsBlobPath = lyricsBlobPath;
        lyrics.Status = SongLyricsStatus.Pending;
        lyrics.UpdatedAt = now;

        // Note what is deliberately NOT cleared: TimingsBlobPath, Confidence and Version. A song that
        // already had good timings keeps serving them while a re-run is in flight, and only loses
        // them if the new attempt actually produces better ones.

        var job = new LyricsAlignmentJob
        {
            JobId = Guid.NewGuid(),
            SongMetadataId = songMetadataId,
            CreatorId = creatorId,
            LyricsBlobPath = lyricsBlobPath,
            Status = LyricsAlignmentJobStatus.Queued,
            Step = LyricsAlignmentStep.Submitted,
            StepUpdatedAt = now,
            CreatedAt = now
        };

        context.LyricsAlignmentJobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);

        // Only now, with the attempt durably recorded, is anything asked to act on it. Hangfire
        // supplies the PerformContext in place of the null argument at execution time.
        _backgroundJobs.Enqueue<ILyricsAlignmentInvoker>(
            invoker => invoker.InvokeAsync(job.JobId, null));

        _logger.LogInformation(
            "Queued lyrics alignment {JobId} for song {SongId}.", job.JobId, songMetadataId);

        return new LyricsSubmissionResult(LyricsSubmissionOutcome.Accepted, job.JobId, "Lyrics submitted.");
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(
        int songMetadataId,
        int creatorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var job = await context.LyricsAlignmentJobs
            .Where(row => row.SongMetadataId == songMetadataId && row.CreatorId == creatorId)
            .Where(row => row.Status == LyricsAlignmentJobStatus.Queued
                || row.Status == LyricsAlignmentJobStatus.Processing)
            .OrderByDescending(row => row.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (job is null)
        {
            return false;
        }

        // The row is marked terminal first and unconditionally. Terminating the orchestration is
        // best-effort - if it cannot be reached the run continues and finishes, and its late callback
        // meets the already-terminal guard in the completion service, which is exactly what that
        // guard is for. Waiting on Azure before releasing the creator would be the wrong order.
        job.Status = LyricsAlignmentJobStatus.Failed;
        job.Step = LyricsAlignmentStep.Failed;
        job.StepUpdatedAt = DateTime.UtcNow;
        job.CompletedAt = DateTime.UtcNow;
        job.FailureCode = LyricsAlignmentFailureCodes.OrchestrationTerminated;
        job.FailureMessage = "Cancelled.";

        var lyrics = await context.SongLyrics
            .FirstOrDefaultAsync(row => row.SongMetadataId == songMetadataId, cancellationToken);

        // Cancelling a re-run must not take away timings an earlier run had already published.
        if (lyrics is not null && lyrics.Status == SongLyricsStatus.Pending)
        {
            lyrics.Status = SongLyricsStatus.Failed;
            lyrics.LastJobId = job.JobId;
            lyrics.UpdatedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken);

        if (job.DurableFunctionTaskId is not null)
        {
            await _durableTaskClient.TerminateAsync(
                job.DurableFunctionTaskId.Value, "Cancelled by the creator.", cancellationToken);
        }

        _logger.LogInformation("Cancelled lyrics alignment {JobId}.", job.JobId);
        return true;
    }

    /// <summary>
    /// Trims, normalises line endings and drops trailing blank lines.
    ///
    /// <para>
    /// Line endings matter more than they look: the aligner maps timings back onto <em>lines</em>, so
    /// a stray <c>\r</c> would become part of a word and a Windows-pasted lyric sheet would map
    /// differently from the same words pasted from a Mac.
    /// </para>
    /// </summary>
    internal static string NormalizeSubmittedText(string? lyricsText)
    {
        if (string.IsNullOrWhiteSpace(lyricsText))
        {
            return string.Empty;
        }

        return lyricsText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    internal static int CountLines(string normalizedText)
        => string.IsNullOrEmpty(normalizedText) ? 0 : normalizedText.Count(c => c == '\n') + 1;
}
