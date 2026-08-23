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

/// <summary>Why an edit to a song's timings was or was not accepted.</summary>
public enum LyricsEditOutcome
{
    Success,

    /// <summary>No such song, or it has no lyrics record.</summary>
    NotFound,

    /// <summary>The caller does not own this song.</summary>
    NotAllowed,

    /// <summary>The song has no timings to edit or publish yet.</summary>
    NoTimings,

    /// <summary>The timings would not be safe to show a listener. See the problems.</summary>
    Invalid,

    /// <summary>An administrator has taken these lyrics down. Only an administrator can undo it.</summary>
    AdminDisabled,

    /// <summary>An attempt is in flight, so there is nothing stable to act on yet.</summary>
    Running
}

/// <summary>The result of saving, discarding or publishing a set of timings.</summary>
/// <param name="Problems">
/// Plain-English reasons a publish was refused, straight from
/// <see cref="LyricsTimingsValidator.Validate"/>. Empty for every other outcome.
/// </param>
public sealed record LyricsEditResult(
    LyricsEditOutcome Outcome,
    string Message,
    IReadOnlyList<string> Problems)
{
    public bool Success => Outcome == LyricsEditOutcome.Success;

    public static LyricsEditResult Ok(string message) => new(LyricsEditOutcome.Success, message, []);

    public static LyricsEditResult Fail(LyricsEditOutcome outcome, string message) =>
        new(outcome, message, []);
}

/// <summary>The timings a creator is about to edit, and where they came from.</summary>
/// <param name="Document">Null unless <paramref name="Outcome"/> is success.</param>
/// <param name="HasUnpublishedChanges">
/// Whether what the editor is showing is work no listener has seen yet. The editor says so, because
/// "you are looking at edits you made and never published" is a materially different thing to be
/// looking at.
///
/// <para>
/// <b>Not "did this come out of the draft blob".</b> That was the old meaning and it was wrong:
/// publishing keeps the draft and stamps it level with the publish, so the draft file outlives the
/// state it described and the editor greeted a freshly published song by claiming it had unpublished
/// changes - while the songs grid, reading <see cref="SongLyrics.HasUnpublishedChanges"/>, said the
/// opposite. This is now that same property, so the two cannot disagree.
/// </para>
/// </param>
public sealed record LyricsEditableTimings(
    LyricsEditOutcome Outcome,
    LyricsTimingsDocument? Document,
    bool HasUnpublishedChanges,
    SongLyrics? Lyrics);

/// <summary>
/// What the creator's lyrics editor talks to: stores pasted lyrics and asks for them to be timed.
/// </summary>
public interface ISongLyricsService
{
    /// <summary>Whether lyric timing is available in this environment at all.</summary>
    bool IsAvailable { get; }

    /// <summary>The current lyrics state for a song, or null if it has never had any.</summary>
    Task<SongLyrics?> GetForSongAsync(int songMetadataId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The current lyrics state for many songs at once, keyed by song id, omitting songs that have
    /// never had any.
    ///
    /// <para>
    /// Exists so a grid can show a column without one query per row, the same reason
    /// <c>SongLikeService.GetBulkLikeCountsAsync</c> does. Comparing confidence across songs is the
    /// whole point of that column - the threshold it is read against is a judgement call that needs
    /// several songs in view at once - so the per-song call would be issued N times or not at all.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<int, SongLyrics>> GetForSongsAsync(
        IEnumerable<int> songMetadataIds,
        CancellationToken cancellationToken = default);

    /// <summary>The attempt currently in flight for a song, if there is one.</summary>
    Task<LyricsAlignmentJob?> GetActiveJobAsync(int songMetadataId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The published timings for a song, for a listener's player. Null unless they are published.
    /// </summary>
    /// <remarks>
    /// <b>Read on the server because the words are rendered on the server.</b> The scroller draws one
    /// span per word from a document C# holds; a browser-side fetch can hand the highlighter its
    /// timings but cannot produce the elements for it to highlight, so a player given only a URL
    /// renders no lyrics at all and sits on its empty message forever. Anything that wants to SHOW
    /// lyrics needs this; the URL path can only ever decorate something already drawn.
    /// </remarks>
    Task<LyricsTimingsDocument?> GetPublishedTimingsAsync(
        int songMetadataId,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// The timings a creator should be editing: their unpublished draft if they have one, otherwise
    /// whatever the aligner last produced.
    ///
    /// <para>
    /// <b>Read here rather than fetched by the browser, because unpublished timings are not
    /// reachable over HTTP at all.</b> <see cref="IsPubliclyReadableAsync"/> gates on
    /// <see cref="SongLyricsStatus.Published"/>, and since alignment no longer publishes anything,
    /// the state a creator most needs to preview is precisely the state that 404s. A read SAS would
    /// route around that and should not: it is an unrevocable bearer URL to content nobody has
    /// approved, and fetching it from the page would need CORS on the storage account that is not
    /// configured.
    /// </para>
    /// </summary>
    Task<LyricsEditableTimings> GetEditableTimingsAsync(
        int songMetadataId,
        int creatorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Store the creator's work in progress, without changing anything a listener can see.
    /// </summary>
    Task<LyricsEditResult> SaveDraftAsync(
        int songMetadataId,
        int creatorId,
        LyricsTimingsDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Release the current timings to listeners: validate, write them over the live artifacts,
    /// regenerate the LRC, and bump the cache-busting version.
    /// </summary>
    Task<LyricsEditResult> PublishAsync(
        int songMetadataId,
        int creatorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Take a song's lyrics off the air without destroying them.
    /// </summary>
    /// <remarks>
    /// The reversible half of turning lyrics off: listeners fall back to cover art immediately, on
    /// web and on both mobile apps, while the timings and every minute of tapping behind them
    /// survive. One press of Publish puts them back.
    /// </remarks>
    Task<LyricsEditResult> UnpublishAsync(
        int songMetadataId,
        int creatorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a song's lyrics outright, as though they had never been pasted.
    /// </summary>
    /// <remarks>
    /// The destructive half, and the only way back to a genuinely blank slate: the row goes, the
    /// blobs go, and the songs grid offers the paste box again. Refuses while an attempt is in
    /// flight rather than racing the completion callback, which would otherwise write the row
    /// straight back.
    /// </remarks>
    Task<LyricsEditResult> RemoveAsync(
        int songMetadataId,
        int creatorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// An administrator takes a song's lyrics down, or puts them back.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same operation as <see cref="UnpublishAsync"/>. That one moves
    /// <c>Status</c>, which the creator can move straight back; this sets a column the publish path
    /// refuses to write past, so a takedown holds. No creator id, because an administrator is not
    /// acting as the owner - the caller is responsible for having checked the permission.
    /// </remarks>
    Task<LyricsEditResult> SetAdminDisabledAsync(
        int songMetadataId,
        int adminUserId,
        bool disabled,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Throw the draft away and go back to the last aligned or published timings.
    /// </summary>
    Task<LyricsEditResult> DiscardDraftAsync(
        int songMetadataId,
        int creatorId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SongLyricsService : ISongLyricsService
{
    private const string LyricsContentType = "text/plain; charset=utf-8";

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IAzureStorageService _storageService;
    private readonly IDurableTaskClient _durableTaskClient;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly IAdminNotificationService _adminNotifications;
    private readonly ILogger<SongLyricsService> _logger;

    public SongLyricsService(
        IDbContextFactory<AppDbContext> contextFactory,
        IAzureStorageService storageService,
        IDurableTaskClient durableTaskClient,
        IBackgroundJobClient backgroundJobs,
        IAdminNotificationService adminNotifications,
        ILogger<SongLyricsService> logger)
    {
        _contextFactory = contextFactory;
        _storageService = storageService;
        _durableTaskClient = durableTaskClient;
        _backgroundJobs = backgroundJobs;
        _adminNotifications = adminNotifications;
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
    public async Task<IReadOnlyDictionary<int, SongLyrics>> GetForSongsAsync(
        IEnumerable<int> songMetadataIds,
        CancellationToken cancellationToken = default)
    {
        var ids = songMetadataIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, SongLyrics>();
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.SongLyrics
            .AsNoTracking()
            .Where(row => ids.Contains(row.SongMetadataId))
            .ToDictionaryAsync(row => row.SongMetadataId, cancellationToken);
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
    public async Task<LyricsTimingsDocument?> GetPublishedTimingsAsync(
        int songMetadataId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var lyrics = await context.SongLyrics
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.SongMetadataId == songMetadataId, cancellationToken);

        // Gated on the row for the same reason IsPubliclyReadableAsync is: timings held back for
        // review, or taken down by an administrator, sit at exactly the same blob path as published
        // ones, so the path proves nothing. IsVisibleToListeners is the single expression of that
        // rule - see the remarks on it.
        if (lyrics is null
            || !lyrics.IsVisibleToListeners
            || string.IsNullOrWhiteSpace(lyrics.TimingsBlobPath))
        {
            return null;
        }

        return await ReadDocumentAsync(lyrics.TimingsBlobPath);
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
            // The IsVisibleToListeners rule, spelled out because this one runs in SQL and cannot
            // call a [NotMapped] property. Any change to that rule belongs here too.
            .Where(row => row.Status == SongLyricsStatus.Published && row.DisabledAt == null)
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

        // Recorded on the creator's ACTION, not on the outcome. "Who is working on lyrics" is the
        // question this answers for an admin, and an attempt that later fails is still an answer.
        await NotifyAdminAsync(
            () => _adminNotifications.NotifyLyricsAddedAsync(creatorId, songMetadataId),
            "added",
            songMetadataId);

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

    private const string TimingsContentType = "application/json; charset=utf-8";
    private const string LrcContentType = "text/plain; charset=utf-8";

    /// <inheritdoc />
    public async Task<LyricsEditableTimings> GetEditableTimingsAsync(
        int songMetadataId,
        int creatorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var (lyrics, song, failure) = await LoadOwnedAsync(context, songMetadataId, creatorId, cancellationToken);
        if (failure is not null)
        {
            return new LyricsEditableTimings(failure.Value, null, false, null);
        }

        // The draft wins when there is one: it is what the creator was last working on, and showing
        // them the aligner's output instead would silently discard their work the moment they saved.
        //
        // NOTE THIS ANSWERS "WHICH BLOB", NOT "IS THIS UNPUBLISHED" - the two came apart the moment
        // publishing started keeping the draft rather than deleting it, and conflating them is what
        // told a creator who had just published that they had unpublished changes. See below.
        var readingDraft = !string.IsNullOrWhiteSpace(lyrics!.DraftTimingsBlobPath);
        var path = readingDraft ? lyrics.DraftTimingsBlobPath : lyrics.TimingsBlobPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            return new LyricsEditableTimings(LyricsEditOutcome.NoTimings, null, false, lyrics);
        }

        var document = await ReadDocumentAsync(path);

        if (document is null && readingDraft)
        {
            // The draft blob is gone or unreadable. Falling back to the published timings beats
            // showing the creator an error page for a file they never asked about - they lose the
            // unsaved work either way, and this way they can carry on.
            _logger.LogWarning(
                "Draft timings for song {SongId} could not be read; falling back to the live ones.",
                songMetadataId);

            document = await ReadDocumentAsync(lyrics.TimingsBlobPath);
            readingDraft = false;
        }

        // THE SAME QUESTION THE SONGS GRID ASKS, deliberately answered from the same property.
        // Publishing keeps the draft blob and stamps it level with the publish, so "there is a draft
        // file" stays true forever afterwards - which is why the editor greeted a freshly published
        // song with "these are your unpublished changes" while the grid, reading the timestamps,
        // correctly said there were none. The two must not be able to disagree.
        var hasUnpublishedChanges = readingDraft && lyrics.HasUnpublishedChanges;

        return document is null
            ? new LyricsEditableTimings(LyricsEditOutcome.NoTimings, null, false, lyrics)
            : new LyricsEditableTimings(LyricsEditOutcome.Success, document, hasUnpublishedChanges, lyrics);
    }

    /// <inheritdoc />
    public async Task<LyricsEditResult> SaveDraftAsync(
        int songMetadataId,
        int creatorId,
        LyricsTimingsDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var (lyrics, song, failure) = await LoadOwnedAsync(context, songMetadataId, creatorId, cancellationToken);
        if (failure is not null)
        {
            return LyricsEditResult.Fail(failure.Value, "These lyrics could not be saved.");
        }

        // Repaired, not validated. A creator mid-way through a record pass has a document that
        // briefly contradicts itself, and refusing to save that would lose their work for being
        // untidy. Validation belongs at Publish, where it is the last gate before listeners.
        LyricsTimingsValidator.Normalize(document);

        var draftPath = string.IsNullOrWhiteSpace(lyrics!.DraftTimingsBlobPath)
            ? SongMediaPaths.ResolveLyricsDraftTimingsTarget(song!.Id, song.MediaGuid, song.Mp3BlobPath)
            : lyrics.DraftTimingsBlobPath;

        await UploadDocumentAsync(draftPath, document);

        lyrics.DraftTimingsBlobPath = draftPath;
        lyrics.DraftUpdatedAt = DateTime.UtcNow;
        lyrics.UpdatedAt = lyrics.DraftUpdatedAt.Value;

        await context.SaveChangesAsync(cancellationToken);

        return LyricsEditResult.Ok("Saved. Listeners still see the last version you published.");
    }

    /// <inheritdoc />
    public async Task<LyricsEditResult> PublishAsync(
        int songMetadataId,
        int creatorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var (lyrics, song, failure) = await LoadOwnedAsync(context, songMetadataId, creatorId, cancellationToken);
        if (failure is not null)
        {
            return LyricsEditResult.Fail(failure.Value, "These lyrics could not be published.");
        }

        // THE TEETH IN AN ADMINISTRATOR'S TAKEDOWN. Everything else about a disabled song still
        // works - the creator can open the editor, tap, and save a draft - because none of that
        // reaches a listener. This is the one door out to an audience, and it is the one that is
        // locked.
        if (lyrics!.DisabledAt is not null)
        {
            return LyricsEditResult.Fail(
                LyricsEditOutcome.AdminDisabled,
                "An administrator has disabled these lyrics, so they can't be published.");
        }

        var source = string.IsNullOrWhiteSpace(lyrics.DraftTimingsBlobPath)
            ? lyrics.TimingsBlobPath
            : lyrics.DraftTimingsBlobPath;

        if (string.IsNullOrWhiteSpace(source))
        {
            return LyricsEditResult.Fail(LyricsEditOutcome.NoTimings, "There are no timings to publish yet.");
        }

        var document = await ReadDocumentAsync(source);
        if (document is null)
        {
            return LyricsEditResult.Fail(LyricsEditOutcome.NoTimings, "These timings could not be read.");
        }

        LyricsTimingsValidator.Normalize(document);

        var problems = LyricsTimingsValidator.Validate(document);
        if (problems.Count > 0)
        {
            return new LyricsEditResult(
                LyricsEditOutcome.Invalid,
                "These timings aren't ready to publish yet.",
                problems);
        }

        var timingsPath = string.IsNullOrWhiteSpace(lyrics.TimingsBlobPath)
            ? SongMediaPaths.ResolveLyricsTimingsTarget(song!.Id, song.MediaGuid, song.Mp3BlobPath)
            : lyrics.TimingsBlobPath;

        var lrcPath = string.IsNullOrWhiteSpace(lyrics.LrcBlobPath)
            ? SongMediaPaths.ResolveLyricsLrcTarget(song!.Id, song.MediaGuid, song.Mp3BlobPath)
            : lyrics.LrcBlobPath;

        await UploadDocumentAsync(timingsPath, document);

        // The LRC is regenerated rather than left alone, because the Download LRC button would
        // otherwise keep handing out the timings from before this edit - two files describing the
        // same song differently, with nothing anywhere to say which is current.
        var lrc = LyricsLrcWriter.Write(
            document,
            SongTitleHelper.GetEffectiveTitle(song!.SongTitle, song.Mp3BlobPath, song.BlobPath),
            song.GetEffectiveArtistNameFull());

        await using (var lrcStream = new MemoryStream(Encoding.UTF8.GetBytes(lrc)))
        {
            await _storageService.UploadAsync(lrcPath, lrcStream, LrcContentType);
        }

        var now = DateTime.UtcNow;

        lyrics.TimingsBlobPath = timingsPath;
        lyrics.LrcBlobPath = lrcPath;
        lyrics.Status = SongLyricsStatus.Published;
        lyrics.PublishedAt = now;
        lyrics.UpdatedAt = now;

        // The draft is kept and stamped, not deleted, so re-opening the editor resumes from what was
        // published rather than from whatever the aligner produced. Stamping it equal to PublishedAt
        // is what clears the "unpublished changes" indicator.
        lyrics.DraftUpdatedAt = now;

        // The cache-buster. The blob path never changes between versions and the response carries
        // an immutable, year-long cache header, so without this a re-publish is invisible to every
        // browser that has already seen the song - permanently.
        lyrics.Version++;

        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Creator {CreatorId} published lyric timings for song {SongId} at version {Version}.",
            creatorId,
            songMetadataId,
            lyrics.Version);

        await NotifyAdminAsync(
            () => _adminNotifications.NotifyLyricsPublishedAsync(creatorId, songMetadataId),
            "published",
            songMetadataId);

        return LyricsEditResult.Ok("Published. Listeners will see these timings from now on.");
    }

    /// <summary>
    /// Fire an admin notification without letting it affect the creator.
    /// </summary>
    /// <remarks>
    /// <b>Swallowed on purpose.</b> These run after the work is already committed, and they exist so
    /// an admin can watch what creators are doing - which is worth exactly nothing measured against
    /// the submit or publish the creator actually asked for. An unreachable SMTP server must not
    /// surface as "we couldn't publish your lyrics" on a publish that already happened.
    /// </remarks>
    private async Task NotifyAdminAsync(Func<Task> notify, string what, int songMetadataId)
    {
        try
        {
            await notify();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not send the admin notification for lyrics {What} on song {SongId}.",
                what,
                songMetadataId);
        }
    }

    /// <inheritdoc />
    public async Task<LyricsEditResult> UnpublishAsync(
        int songMetadataId,
        int creatorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var (lyrics, _, failure) = await LoadOwnedAsync(context, songMetadataId, creatorId, cancellationToken);
        if (failure is not null)
        {
            return LyricsEditResult.Fail(failure.Value, "These lyrics could not be hidden.");
        }

        if (lyrics!.Status != SongLyricsStatus.Published)
        {
            return LyricsEditResult.Ok("These lyrics were already hidden from listeners.");
        }

        // NeedsReview rather than a new state, because that is precisely what this song now is:
        // timings that exist and nobody outside can see. PublishedAt is left alone - it records that
        // a publish once happened, which is still true, and clearing it would tell
        // HasUnpublishedChanges that every saved edit is suddenly unreleased.
        lyrics.Status = SongLyricsStatus.NeedsReview;
        lyrics.UpdatedAt = DateTime.UtcNow;

        // The paths do not change, so a phone holding a cached copy has no other way to notice.
        lyrics.Version++;

        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Creator {CreatorId} hid the lyrics for song {SongId} from listeners.",
            creatorId,
            songMetadataId);

        return LyricsEditResult.Ok(
            "Hidden. Listeners see the cover art again, and your timings are kept.");
    }

    /// <inheritdoc />
    public async Task<LyricsEditResult> RemoveAsync(
        int songMetadataId,
        int creatorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var (lyrics, _, failure) = await LoadOwnedAsync(context, songMetadataId, creatorId, cancellationToken);
        if (failure is not null)
        {
            return LyricsEditResult.Fail(failure.Value, "These lyrics could not be removed.");
        }

        // Refused rather than raced. The completion callback writes the row back if it finds it
        // missing - deliberately, so timings that cost minutes of compute are never dropped - so
        // removing under a running attempt would delete the row and then watch it reappear.
        var running = await context.LyricsAlignmentJobs
            .AnyAsync(
                job => job.SongMetadataId == songMetadataId
                    && (job.Status == LyricsAlignmentJobStatus.Queued
                        || job.Status == LyricsAlignmentJobStatus.Processing),
                cancellationToken);

        if (running)
        {
            return LyricsEditResult.Fail(
                LyricsEditOutcome.Running,
                "These lyrics are being timed. Cancel that first, then remove them.");
        }

        var paths = new[]
        {
            lyrics!.TimingsBlobPath,
            lyrics.LrcBlobPath,
            lyrics.DraftTimingsBlobPath,
            lyrics.LyricsBlobPath
        };

        context.SongLyrics.Remove(lyrics);
        await context.SaveChangesAsync(cancellationToken);

        // AFTER the row, and best-effort. With the row gone nothing points at these blobs and nothing
        // will serve them - IsPubliclyReadableAsync resolves a path back through the row - so a blob
        // that refuses to delete is litter, not exposure, and is not worth failing the removal over.
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            try
            {
                await _storageService.DeleteAsync(path!);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete the lyrics artifact {Path}.", path);
            }
        }

        _logger.LogInformation(
            "Creator {CreatorId} removed the lyrics for song {SongId}.", creatorId, songMetadataId);

        return LyricsEditResult.Ok("Removed. This song is back to having no lyrics at all.");
    }

    /// <inheritdoc />
    public async Task<LyricsEditResult> SetAdminDisabledAsync(
        int songMetadataId,
        int adminUserId,
        bool disabled,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var lyrics = await context.SongLyrics
            .FirstOrDefaultAsync(row => row.SongMetadataId == songMetadataId, cancellationToken);

        if (lyrics is null)
        {
            return LyricsEditResult.Fail(
                LyricsEditOutcome.NotFound, "This song has no lyrics to disable.");
        }

        lyrics.DisabledAt = disabled ? DateTime.UtcNow : null;
        lyrics.DisabledByUserId = disabled ? adminUserId : null;
        lyrics.DisabledReason = disabled ? Truncate(reason, 500) : null;
        lyrics.UpdatedAt = DateTime.UtcNow;

        // Bumped on the way DOWN as well as up. The blob path is unchanged and cached for a year, so
        // a phone that already holds these timings needs a reason to ask again - and re-enabling
        // without one would leave the song silently lyric-less on every device that had seen it.
        lyrics.Version++;

        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Administrator {AdminUserId} {Action} the lyrics for song {SongId}.",
            adminUserId,
            disabled ? "disabled" : "re-enabled",
            songMetadataId);

        return LyricsEditResult.Ok(
            disabled
                ? "Disabled. Listeners see the cover art, and the creator can't publish these again."
                : "Re-enabled. The creator can publish these lyrics again.");
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxLength ? value : value[..maxLength];

    /// <inheritdoc />
    public async Task<LyricsEditResult> DiscardDraftAsync(
        int songMetadataId,
        int creatorId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var (lyrics, _, failure) = await LoadOwnedAsync(context, songMetadataId, creatorId, cancellationToken);
        if (failure is not null)
        {
            return LyricsEditResult.Fail(failure.Value, "That draft could not be discarded.");
        }

        if (string.IsNullOrWhiteSpace(lyrics!.DraftTimingsBlobPath))
        {
            return LyricsEditResult.Ok("There were no unpublished changes.");
        }

        var path = lyrics.DraftTimingsBlobPath;

        lyrics.DraftTimingsBlobPath = null;
        lyrics.DraftUpdatedAt = null;
        lyrics.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        // The row is cleared first and the blob deleted after. The other order leaves a row pointing
        // at a blob that is gone if the delete succeeds and the save then fails, which reads to the
        // editor as a corrupt draft rather than no draft.
        try
        {
            await _storageService.DeleteAsync(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the discarded draft blob {Path}.", path);
        }

        return LyricsEditResult.Ok("Your unpublished changes were discarded.");
    }

    /// <summary>
    /// Load a song's lyrics row, refusing anyone who does not own the song.
    ///
    /// <para>
    /// The ownership check is the same one <c>SubmitAsync</c> makes and for the same reason: the
    /// route is gated on "is a creator", which is not the same claim as "owns this song".
    /// </para>
    /// </summary>
    private static async Task<(SongLyrics? Lyrics, SongMetadata? Song, LyricsEditOutcome? Failure)> LoadOwnedAsync(
        AppDbContext context,
        int songMetadataId,
        int creatorId,
        CancellationToken cancellationToken)
    {
        var song = await context.SongMetadata
            .FirstOrDefaultAsync(row => row.Id == songMetadataId, cancellationToken);

        if (song is null)
        {
            return (null, null, LyricsEditOutcome.NotFound);
        }

        if (song.CreatorId != creatorId)
        {
            return (null, null, LyricsEditOutcome.NotAllowed);
        }

        var lyrics = await context.SongLyrics
            .FirstOrDefaultAsync(row => row.SongMetadataId == songMetadataId, cancellationToken);

        return lyrics is null
            ? (null, song, LyricsEditOutcome.NotFound)
            : (lyrics, song, null);
    }

    private async Task<LyricsTimingsDocument?> ReadDocumentAsync(string? blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath))
        {
            return null;
        }

        try
        {
            await using var stream = await _storageService.OpenReadAsync(blobPath);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return LyricsTimingsSerializer.Deserialize(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read lyric timings from {Path}.", blobPath);
            return null;
        }
    }

    private async Task UploadDocumentAsync(string blobPath, LyricsTimingsDocument document)
    {
        var bytes = Encoding.UTF8.GetBytes(LyricsTimingsSerializer.Serialize(document));
        await using var stream = new MemoryStream(bytes);
        await _storageService.UploadAsync(blobPath, stream, TimingsContentType);
    }
}
