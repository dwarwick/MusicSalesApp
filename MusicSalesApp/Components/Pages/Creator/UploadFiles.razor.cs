using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Imaging;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSalesApp.Components.Pages.Creator;



public class UploadFilesModel : BlazorBase, IAsyncDisposable
{
    private sealed class AntiforgeryTokenResponse
    {
        public string Token { get; set; }
        public string FieldName { get; set; }
    }

    protected sealed class InitialUploadProgressItem
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty;
        public long Size { get; set; }
        public string FormattedSize { get; set; } = string.Empty;
        public int Progress { get; set; }
        public string StatusMessage { get; set; } = "Waiting...";
    }

    private sealed class InitialUploadProgressState
    {
        public long TotalBytes { get; set; }
        public long BytesReceived { get; set; }
        public DateTime LastReportedAtUtc { get; set; } = DateTime.MinValue;
    }

    private CancellationTokenSource _uploadCts = new CancellationTokenSource();

    protected List<UploadPairItem> _uploadItems = new List<UploadPairItem>();

    // The review step collects genre, persona and the AI flags, so it needs the lists to
    // choose from. Loaded once when the page opens rather than when the step appears: by then
    // the creator is waiting, and a batch that has finished decoding should not then stall on
    // two lookups.
    protected List<Genre> _genres = new();
    protected List<CreatorPersona> _personas = new();
    protected string _creatorDisplayName = string.Empty;

    // Apply-to-all. Deliberately overwrites rows the creator has already filled: the
    // alternative - only filling blanks - would do different things depending on state that is
    // not visible, and someone who genuinely wants one genre across fifty songs would have to
    // clear rows first. Every row visibly changes, and any row can be changed back.
    protected string _applyAllGenre = string.Empty;
    protected int? _applyAllPersonaId;

    // The AI disclosure in bulk is the same two-group choice as a row, not a shortcut for one
    // side of it: a batch is usually one release, so "this whole EP has AI vocals" is as normal
    // an answer as "all of it is original".
    protected bool _applyAllOriginal;
    protected bool _applyAllAiMusic;
    protected bool _applyAllAiVocals;
    protected bool _applyAllAiLyrics;

    // Inline creation. Neither may navigate: leaving /upload-files drops the live FileList and
    // with it the whole batch, which the creator has already waited through a decode for.
    protected bool _showAddGenre;
    protected string _newGenreName = string.Empty;
    protected string _addGenreError = string.Empty;
    protected bool _addingGenre;
    protected UploadPairItem _addGenreForRow;

    protected bool _showAddPersona;
    protected string _newPersonaName = string.Empty;
    protected string _addPersonaError = string.Empty;
    protected bool _addingPersona;
    protected UploadPairItem _addPersonaForRow;
    protected List<InitialUploadProgressItem> _initialUploadItems = new List<InitialUploadProgressItem>();
    protected string _initialUploadStatusMessage = string.Empty;
    protected int _initialUploadBatchProgress = 0;
    protected string _validationErrorMessage = string.Empty;
    protected List<string> _unmatchedMp3Files = new List<string>();
    protected List<string> _unmatchedCoverArtFiles = new List<string>();
    protected List<string> _duplicateSongFiles = new List<string>();

    /// <summary>Selected files whose extension is not a supported audio or cover-art type.</summary>
    protected List<string> _skippedFiles = new List<string>();

    // Titles are no longer derived from filenames, so the batch pauses after matching to let the
    // creator review and edit each title. The buffered temp files have to outlive that pause,
    // which is why they are held here rather than in a local `finally`.
    /// <remarks>
    /// Visible to the test assembly so the leave guard can be driven into the state it exists for -
    /// a batch reviewed but not yet uploaded, which is the one that is silently discarded.
    /// </remarks>
    protected internal bool _awaitingTitleConfirmation = false;
    protected readonly List<PendingUpload> _pendingUploads = new();
    private readonly List<string> _pendingTempFiles = new();

    // Progress for a queued song arrives over SignalR from a different request entirely - the
    // Azure Function posts to the API, which broadcasts to this creator's group - so the row has to
    // be findable by media GUID rather than by position in the batch.
    private readonly Dictionary<Guid, UploadPairItem> _jobRows = new();
    private readonly object _jobRowsLock = new();

    // Fallback for when the progress hub is unavailable. Everything still processes correctly in
    // that case - the only symptom is a bar frozen on "Waiting to be processed" forever, which is
    // indistinguishable from a stuck pipeline. Polling the job rows gives step-level movement
    // regardless of SignalR. Percentages are not persisted, so this is coarser than the live feed
    // and deliberately only fills in what the hub failed to deliver.
    private static readonly TimeSpan JobStatePollInterval = TimeSpan.FromSeconds(5);
    private CancellationTokenSource _jobPollCts;

    /// <summary>
    /// One reviewed song, waiting to be staged.
    ///
    /// <para>
    /// Carries only the audio, and by two alternatives rather than a set: on the server path the
    /// bytes are on this machine and named by <see cref="AudioTempPath"/>; on the direct path they
    /// never touch it, and what is carried instead is where the browser can find them
    /// (<see cref="BrowserAudioIndex"/>).
    /// </para>
    ///
    /// <para>
    /// Deliberately holds nothing about the cover art. The creator can re-pair artwork during the
    /// review step, so a path captured when this was built would be stale by the time it is used -
    /// the assignment lives on <see cref="UploadPairItem.CoverArtFileName"/>, which the UI binds to,
    /// and the path is resolved from it at upload time.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Protected rather than private, along with <see cref="_pendingUploads"/>, so a test can seed a
    /// reviewed batch on either upload path - the same reason <see cref="_uploadProgressRouters"/> is.
    /// Which path a row is on is not observable from anywhere else, and it decides where a cover
    /// image the creator browses for has to be put.
    /// </remarks>
    protected sealed record PendingUpload(
        UploadPairItem Item,
        string AudioTempPath,
        int? BrowserAudioIndex = null);


    // Creator ID - will be populated if the current user is a creator
    private int? _currentCreatorId = null;
    private string _currentUserEmail = null;
    private bool _hasLoadedCreatorId = false;

    // Track upload state for navigation/close warnings
    protected bool _isUploading = false;
    protected bool _isProcessingFiles = false;
    private bool _disposed = false;

    /// <summary>
    /// Guards the batch notification so it is sent once per batch, however many times the last song
    /// is reported terminal - the hub and the fallback poller can both deliver that transition.
    /// </summary>
    private bool _batchNotificationSent;

    // Cover-art matching runs in the Azure Function and answers over SignalR, so the batch has to be
    // findable when the reply lands on a different request entirely - same reason as _jobRows above.
    // No database row backs any of this: a match batch exists only while this page is waiting, its
    // staged images are swept by the container's lifecycle rule, and a lost reply costs a worse
    // pairing rather than a lost song.
    private readonly Dictionary<Guid, TaskCompletionSource<CoverArtMatchResult>> _matchWaiters = new();
    private readonly object _matchWaitersLock = new();

    // Filename -> position in the browser's FileList. The only handle JavaScript can use to reach a
    // selected file; an IBrowserFile is a server-side reference JS has no way to address.
    private readonly Dictionary<string, int> _browserFileIndexes = new(StringComparer.OrdinalIgnoreCase);

    // Direct-to-storage upload state. Null until the module is imported on first render.
    private IJSObjectReference _uploadModule;
    private DotNetObjectReference<UploadFilesModel> _uploadCallbackRef;

    /// <summary>
    /// The shared anchor-click guard, and the reference it calls back on.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="_uploadCallbackRef"/>, which only exists when direct-to-storage
    /// upload is configured. Warning somebody that their files are about to be discarded is not
    /// something to make conditional on an unrelated feature being switched on.
    /// </remarks>
    private IJSObjectReference _leaveGuardModule;
    private DotNetObjectReference<UploadFilesModel> _leaveGuardRef;

    private readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<DirectUploadResult>>> _uploadPhases = new();
    private readonly object _uploadPhasesLock = new();

    // Where each phase's progress reports go. Keyed by phase id because the image phase drives the
    // single batch bar while every audio phase drives one song's own row, and both arrive through the
    // same callback. Without this the audio percentages would overwrite the batch bar of a phase that
    // finished minutes earlier.
    // Protected rather than private so a test can seed a phase and drive ReportUploadProgress as the
    // browser would, matching how _uploadItems and _isUploading are already reached.
    protected readonly Dictionary<string, Action<int[], int[]>> _uploadProgressRouters = new();

    // Cover art filename -> the blob it was uploaded to in this batch's staging folder. Populated
    // only on the direct path; the pairing arrives as filenames, and this is what turns one back into
    // the blob CreateFromStagedAsync copies into the song's own folder.
    private readonly Dictionary<string, string> _stagedImagePaths = new(StringComparer.OrdinalIgnoreCase);

    // The other half of the same lookup, for the server path: cover art filename -> the temp file
    // holding it. A field rather than a local because the creator can re-pair artwork at the review
    // step, so the mapping has to outlive the method that built it.
    private readonly Dictionary<string, string> _coverArtTempPaths = new(StringComparer.OrdinalIgnoreCase);

    // Cover art filename -> size, for the review table. Kept separately because after a re-pairing
    // the row's image is no longer the one it was built with, and IBrowserFile is long gone by then.
    private readonly Dictionary<string, long> _coverArtFileSizes = new(StringComparer.OrdinalIgnoreCase);

    // Cover art filename -> a small inline thumbnail, for images the JS previewer cannot reach.
    // Everything selected with the batch is addressed by its position in the main file input's
    // FileList; an image browsed for at the review step arrives through a different input and has no
    // position there, so its thumbnail is rendered here instead. Downscaled to 128px first - a data
    // URL of the original would put megabytes of base64 in the DOM for a 48px picture.
    private readonly Dictionary<string, string> _coverArtPreviewDataUrls = new(StringComparer.OrdinalIgnoreCase);

    // The next free slot in the match-batch folder. Seeded past the images the batch staged for
    // itself, because those own indexes 0..n-1 and writing over one would replace a different song's
    // artwork with this one.
    private int _nextStagedImageIndex;

    // The row whose Browse button opened the file dialog. Held rather than passed, because the file
    // arrives on the shared input's change event, which knows nothing about which row asked for it.
    private UploadPairItem _rowCoverArtTargetItem;

    // Same hazard as _isProcessingFiles: a second change event on an InputFile invalidates the
    // IBrowserFile from the first, so only the first is processed. Protected because the markup
    // disables both Browse buttons while one is in flight.
    protected bool _isProcessingRowCoverArt;

    /// <summary>
    /// Whether the creator wants to check cover-art pairings before the batch uploads.
    ///
    /// <para>
    /// A pre-flight choice, offered on the drop box rather than at the review step, because the point
    /// of turning it off is to never see that step - a switch you have to reach the interface to
    /// reach cannot turn the interface off.
    /// </para>
    ///
    /// <para>
    /// Only ever suppresses the <em>manual</em> pass. Automatic matching still runs: it is the thing
    /// producing the pairings, and the creator turning this off is saying they trust it, not that
    /// they would rather have no artwork.
    /// </para>
    /// </summary>

    /// <summary>
    /// Where the preference is remembered.
    ///
    /// <para>
    /// The browser, not the database. It is a habit rather than an account setting, and localStorage
    /// costs no schema change, no migration and no round trip - a creator who never wants the pause
    /// unchecks it once instead of once per batch.
    /// </para>
    /// </summary>

    /// <summary>
    /// The cover image the creator is currently moving, or null.
    ///
    /// <para>
    /// Serves both input methods. HTML5 drag-and-drop could carry this in its own dataTransfer, but
    /// holding it here means the same state drives click-to-select, which is what makes the feature
    /// work at all on a touch screen - where there is no drag event to listen for.
    /// </para>
    /// </summary>
    private string _heldCoverArt;

    // The match batch whose staged images are still needed. On the direct path those images ARE the
    // cover art - nothing else holds a copy - so the folder cannot be swept until every song that
    // matched one has been created.
    // Protected so a test can confirm that art added at the review step is put under this sweep.
    // Nothing else records that a staged blob exists, so failing to set it is silent until the
    // container's lifecycle rule eventually clears up after us.
    protected Guid? _pendingImageBatchId;

    /// <summary>What the browser reports back for one file it tried to upload.</summary>
    /// <param name="Index">Position in the browser's FileList, echoed back so this can be matched up.</param>
    /// <param name="ErrorCode">
    /// Azure's own <c>x-ms-error-code</c> where there is one. Carried because the two failures that
    /// matter most here - an expired token and storage CORS not allowing this site - are invisible in
    /// every log the server owns, so if this does not surface them nothing does.
    /// </param>
    public sealed record DirectUploadResult(
        int Index,
        bool Ok,
        int HttpStatus,
        string ErrorCode,
        string ErrorMessage);

    // Configuration for chunked uploads
    private const int MaxFilesAllowed = 50;
    private const int ChunkSize = 8;

    /// <summary>
    /// How many files the browser uploads at once on the direct path.
    ///
    /// <para>
    /// Deliberately lower than <see cref="ChunkSize"/> rather than sharing it. That number bounds
    /// transfers leaving a datacentre; this one bounds transfers leaving a creator's house. Eight
    /// concurrent 57 MB PUTs over a residential uplink do not go faster - they divide the same
    /// bandwidth eight ways, so every bar crawls and nothing finishes early, which is precisely the
    /// "it looks hung" symptom this change exists to remove.
    /// </para>
    /// </summary>
    private const int BrowserUploadConcurrency = 4;
    private const string UploadFailedUserMessage = "There was an issue uploading your files. It is being investigated. Please try again later.";
    private static readonly TimeSpan InitialUploadProgressUpdateInterval = TimeSpan.FromSeconds(1);

    private long _maxAudioFileSize = 100 * 1024 * 1024; // default 100 MB, loaded from settings
    protected int _maxAudioUploadSizeMBDisplay = 100; // MB value for display in UI
    private long _maxImageFileSize = 20 * 1024 * 1024; // default 20 MB, loaded from settings
    protected int _maxImageUploadSizeMBDisplay = 20; // MB value for display in UI

    private static readonly string[] ValidAudioExtensions = MusicFileExtensions.ValidAudioExtensions;
    private static readonly string[] ValidCoverArtExtensions = MusicFileExtensions.ValidCoverArtExtensions;
    
    /// <summary>
    /// True once the receiving phase has completed in this session, so the batch bar can credit it
    /// as done. Rows restored on a later visit never went through receiving here, and
    /// <see cref="_uploadItems"/> being populated stands in for it then.
    /// </summary>
    private bool _filesReceived;

    /// <summary>
    /// One bar for the whole batch: receiving every file, then processing every song. Shown from
    /// the moment files are selected until the last song reaches a terminal step.
    /// </summary>
    protected bool ShowOverallProgress => _initialUploadItems.Any() || _uploadItems.Any();

    /// <summary>
    /// Whether at least one song from this batch has actually reached the catalogue.
    ///
    /// <para>
    /// Gates the pointer to Manage My Songs. Deliberately narrower than "the batch has finished":
    /// a creator whose second song failed still has a first one worth going to look at, and a link
    /// that appears only on a flawless run is missing exactly when it is most wanted.
    /// </para>
    ///
    /// <para>
    /// Just as deliberately not shown earlier. During the review step nothing has been uploaded at
    /// all, and leaving the page there discards the whole batch - so an invitation to go somewhere
    /// else would be pointing at songs that do not exist and costing the ones that nearly did.
    /// </para>
    /// </summary>
    protected bool AnySongPublished
        => _uploadItems.Any(item => item.Step == AudioProcessingStep.Completed);

    /// <summary>
    /// True while at least one song is still moving through the pipeline.
    ///
    /// <para>
    /// This is what hides the drop box during phase two. <see cref="_isUploading"/> covers only the
    /// receiving half and goes false the moment the last file is staged, which is exactly when
    /// processing starts - so on its own it let the box reappear underneath a batch that was still
    /// transcoding, inviting a second batch on top of the first.
    /// </para>
    ///
    /// <para>
    /// Keyed on <c>MediaGuid</c> rather than status so the title-review pause is excluded: those
    /// rows exist but have not been staged, and choosing different files there has always been a
    /// legitimate way to replace the batch.
    /// </para>
    /// </summary>
    protected bool IsBatchProcessing => _uploadItems.Any(item =>
        item.MediaGuid != Guid.Empty && !AudioProcessingProgressCalculator.IsTerminal(item.Step));

    /// <summary>
    /// Whether the drop box is out of the way: while files are being received, while they are being
    /// inspected, while the creator is reviewing, and for the whole of processing. It comes back once
    /// every song is terminal, which is the same moment the batch bar reaches 100%.
    ///
    /// <para>
    /// The review step used to keep it, on the reasoning that dropping different files there was a
    /// legitimate way to replace a batch. That was never worth what it cost. A drop box sitting under
    /// a table the creator is being asked to act on invites exactly one accident, and the accident is
    /// unrecoverable - it discards every title they edited and every pairing they fixed. It happened
    /// on the first real run. Cancel is two inches away and says what it does.
    /// </para>
    ///
    /// <para>
    /// Hidden, not removed. The file input inside it is where the browser's FileList lives, and this
    /// batch's audio is still read from it after the review step - taking the element out of the DOM
    /// would strand the upload it is about to serve.
    /// </para>
    /// </summary>
    protected bool HideUploadBox
        => _isUploading || _isProcessingFiles || _awaitingTitleConfirmation || IsBatchProcessing;

    /// <summary>
    /// Batch progress, 0-100. Terminal songs count as complete - a failed song will not progress
    /// further, and treating it as unfinished would strand this below 100 forever.
    /// </summary>
    protected int OverallProgressPercent
    {
        get
        {
            var receiving = (_filesReceived || _uploadItems.Any())
                ? 100d
                : _initialUploadBatchProgress;

            var songPercents = _uploadItems
                .Select(item => AudioProcessingProgressCalculator.IsTerminal(item.Step)
                    ? 100d
                    : item.Progress)
                .ToList();

            return (int)AudioProcessingProgressCalculator.ToBatchPercent(receiving, songPercents);
        }
    }

    /// <summary>
    /// The upload button's text. Held in one place because the progress message names it, and an
    /// instruction that names a button which says something else is worse than no instruction.
    /// </summary>
    /// <summary>How many rows still need a look, for the message beside the upload button.</summary>
    protected int UnconfirmedTitleCount => _uploadItems.Count(item => !item.TitleConfirmed);

    protected int MissingGenreCount => _uploadItems.Count(item => string.IsNullOrWhiteSpace(item.Genre));

    protected int UndeclaredAiCount => _uploadItems.Count(item => !item.AiDeclared);

    protected bool ReviewIsComplete =>
        UnconfirmedTitleCount == 0 && MissingGenreCount == 0 && UndeclaredAiCount == 0;

    /// <summary>
    /// What is still outstanding, in one sentence, or empty when nothing is.
    /// </summary>
    protected string ReviewOutstandingMessage
    {
        get
        {
            var parts = new List<string>();
            if (UnconfirmedTitleCount == 1)
            {
                parts.Add("1 title still needs a look");
            }
            else if (UnconfirmedTitleCount > 1)
            {
                parts.Add($"{UnconfirmedTitleCount} titles still need a look");
            }

            if (MissingGenreCount == 1)
            {
                parts.Add("1 song needs a genre");
            }
            else if (MissingGenreCount > 1)
            {
                parts.Add($"{MissingGenreCount} songs need a genre");
            }

            if (UndeclaredAiCount == 1)
            {
                parts.Add("1 song needs an AI answer");
            }
            else if (UndeclaredAiCount > 1)
            {
                parts.Add($"{UndeclaredAiCount} songs need an AI answer");
            }

            return parts.Count == 0 ? string.Empty : string.Join(", and ", parts) + ".";
        }
    }

    /// <summary>
    /// The name a listener will actually see on this song.
    ///
    /// <para>
    /// Persona, then the creator display name. The chain already exists in the data; the upload
    /// page just has to show it, so that choosing no persona is an informed choice rather than
    /// a blank.
    /// </para>
    /// </summary>
    protected string ArtistNameFor(UploadPairItem item)
    {
        if (item.PersonaId is int personaId)
        {
            var persona = _personas.FirstOrDefault(p => p.Id == personaId);
            if (persona != null)
            {
                return persona.Name;
            }
        }

        return string.IsNullOrWhiteSpace(_creatorDisplayName) ? "your display name" : _creatorDisplayName;
    }

    /// <summary>
    /// Whether a title still reads like the filename it came from.
    ///
    /// <para>
    /// Only used to decide how loudly a row asks to be checked - the confirmation is required
    /// either way. The rule is deliberately narrow: a leading track number, a trailing version
    /// word, or a run of digits long enough to be a timestamp. Guessing more aggressively would
    /// nag people who name their files properly.
    /// </para>
    /// </summary>
    /// <remarks>Internal, like ShouldPauseForReview, so the rule can be tested directly: which
    /// titles it does and does not flag is the whole of it, and none of that is observable from
    /// the rendered markup.</remarks>
    internal static bool TitleLooksLikeAFileName(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var trimmed = title.Trim();

        // A leading number alone is NOT enough: "99 Red Balloons" and "4 Minutes" are songs.
        // What marks a track number is being zero-padded, or carrying a separator after it.
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^0\d")
            || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d{1,3}\s*[-_.]\s*\S"))
        {
            return true;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(
                trimmed, @"\b(final|copy|mix|master|v\d+|take\s?\d+|untitled|track)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"\d{6,}");
    }

    protected string UploadButtonLabel
        => _uploadItems.Count == 1 ? "Upload 1 Song" : $"Upload {_uploadItems.Count} Songs";

    /// <summary>Wording for the batch bar, so it says which half of the run is underway.</summary>
    protected string OverallProgressMessage
    {
        get
        {
            if (_uploadItems.Count == 0)
            {
                return _initialUploadItems.Any() ? "Receiving your files..." : string.Empty;
            }

            var finished = _uploadItems.Count(item => AudioProcessingProgressCalculator.IsTerminal(item.Step));
            if (finished == _uploadItems.Count)
            {
                var failed = _uploadItems.Count(item => item.Step == AudioProcessingStep.Failed);
                return failed == 0
                    ? $"All {_uploadItems.Count} song(s) published."
                    : $"Finished: {_uploadItems.Count - failed} published, {failed} failed.";
            }

            if (_awaitingTitleConfirmation)
            {
                // The step is no longer reached only when something is broken, so it can no longer
                // claim something is. Saying "fix the highlighted titles" above a table with nothing
                // highlighted sends the creator hunting for a problem that does not exist.
                var broken = _uploadItems.Count(item => item.TitleError != null);

                // Names the button. "Ready to upload" described the state and left the creator
                // waiting for something to happen - which the bar, striped and animated at the time,
                // was actively encouraging.
                return broken switch
                {
                    0 => $"Nothing has been uploaded yet. Check the titles and cover art, then choose "
                         + $"“{UploadButtonLabel}”.",
                    1 => "One title needs a change before this batch can upload.",
                    _ => $"{broken} titles need a change before this batch can upload."
                };
            }

            return $"Processing songs ({finished} of {_uploadItems.Count} finished)...";
        }
    }

    protected InputFile FileInput { get; set;}

    /// <summary>
    /// The input behind every row's Browse button.
    ///
    /// <para>
    /// Separate from <see cref="FileInput"/> and, critically, without its <c>direct-upload-input</c>
    /// class. That class is a singleton contract: both the direct uploader and the thumbnail helper
    /// find the batch's files with a single <c>querySelector</c> and then index into the FileList
    /// they get back, so a second element wearing it could be handed over instead and every index
    /// would resolve against the wrong list. The batch's own input cannot be reused either - its
    /// change handler begins by discarding the batch, and its FileList is still where the audio for
    /// every row is read from.
    /// </para>
    /// </summary>
    protected InputFile RowCoverArtInput { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedCreatorId)
        {
            _hasLoadedCreatorId = true;
            await LoadCreatorIdAsync();
            await LoadMaxAudioFileSizeAsync();

            // Before any files arrive rather than when the review step opens: by then the
            // creator is already waiting, and a batch that has finished decoding should not
            // then stall on two lookups.
            await LoadReviewChoicesAsync();

            UploadProgressHubClient.OnProgress += ApplyProgressAsync;
            UploadProgressHubClient.OnMatchProgress += ApplyMatchProgressAsync;
            UploadProgressHubClient.OnMatchResult += ApplyMatchResultAsync;
            await UploadProgressHubClient.StartAsync();

            await InitialiseDirectUploadAsync();
            await ArmLeaveGuardAsync();

            // Songs queued on a previous visit are still being processed, so rebuild their rows
            // rather than showing an empty page while work is in flight.
            await RestoreInFlightJobsAsync();
        }

        // Every render while reviewing, not just the first: re-pairing moves an image to a different
        // element, and the element it lands on starts with no source.
        if (_awaitingTitleConfirmation && CanRepairCoverArt)
        {
            await RefreshCoverArtPreviewsAsync();
        }
    }

    /// <summary>
    /// Rebuilds progress rows for jobs that have not finished, at the step they actually reached.
    ///
    /// <para>
    /// Processing outlives the circuit now: a creator can queue a batch, navigate away or refresh,
    /// and come back to find it still running. The persisted step is what makes that resumable -
    /// the percentage itself is never stored, it is recomputed from the step.
    /// </para>
    /// </summary>
    private async Task RestoreInFlightJobsAsync()
    {
        if (_currentCreatorId is null)
        {
            return;
        }

        try
        {
            var active = await SongUploadJobService.GetActiveJobsAsync(_currentCreatorId.Value);
            if (active.Count == 0)
            {
                return;
            }

            foreach (var job in active)
            {
                var row = new UploadPairItem
                {
                    SongTitle = job.SongTitle,
                    AudioFileName = job.SourceFileName,
                    AudioFileSize = job.SourceFileSize,
                    CoverArtFileName = job.CoverArtFileName ?? string.Empty,
                    HasCoverArt = !string.IsNullOrEmpty(job.CoverArtBlobPath),
                    MediaGuid = job.MediaGuid,
                    Step = job.Step,
                    Status = UploadStatus.Processing,
                    StatusMessage = DescribeStep(job.Step),
                    Progress = (int)AudioProcessingProgressCalculator.ToOverallPercent(job.Step)
                };

                _uploadItems.Add(row);
                lock (_jobRowsLock)
                {
                    _jobRows[job.MediaGuid] = row;
                }
            }

            StartJobStatePolling();
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            // Cosmetic: the songs keep processing regardless of whether their rows are shown.
            Logger.LogWarning(ex, "Could not restore in-flight upload jobs for creator {CreatorId}", _currentCreatorId);
        }
    }

    /// <summary>
    /// Loads the current user's Creator ID and email from the authentication state.
    /// Can be called multiple times safely - always refreshes from the database.
    /// </summary>
    private async Task LoadCreatorIdAsync()
    {
        try
        {
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            if (user.Identity?.IsAuthenticated == true)
            {
                var appUser = await UserManager.GetUserAsync(user);
                if (appUser != null)
                {
                    // Check if the user is a creator and get their creator ID
                    _currentCreatorId = await CreatorService.GetCreatorIdForUserAsync(appUser.Id);
                    _currentUserEmail = appUser.Email;
                    Logger.LogInformation("UploadFiles: Loaded CreatorId={CreatorId} for UserId={UserId}, Email={Email}", _currentCreatorId, appUser.Id, _currentUserEmail);
                }
                else
                {
                    Logger.LogWarning("UploadFiles: UserManager.GetUserAsync returned null for authenticated user");
                }
            }
            else
            {
                Logger.LogWarning("UploadFiles: User is not authenticated");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: Failed to determine creator status");
            _currentCreatorId = null;
        }
    }

    /// <summary>
    /// Loads the maximum audio and image file upload sizes from the application settings.
    /// </summary>
    private async Task LoadMaxAudioFileSizeAsync()
    {
        try
        {
            var audioSizeMB = await AppSettingsService.GetMaxAudioUploadSizeMBAsync();
            _maxAudioFileSize = (long)audioSizeMB * 1024 * 1024;
            _maxAudioUploadSizeMBDisplay = audioSizeMB;

            var imageSizeMB = await AppSettingsService.GetMaxImageUploadSizeMBAsync();
            _maxImageFileSize = (long)imageSizeMB * 1024 * 1024;
            _maxImageUploadSizeMBDisplay = imageSizeMB;

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: Failed to load max upload size settings. Using defaults.");
        }
    }

    protected async Task HandleFileSelected(InputFileChangeEventArgs e)
    {
        // Guard against concurrent invocations — in .NET 9+ Blazor, a second change event
        // on InputFile invalidates file references from the first event. Drop events can
        // trigger multiple change events, so we must process only the first one.
        if (_isProcessingFiles)
            return;
        _isProcessingFiles = true;

        // Track temp files for centralized cleanup — declared outside try so
        // the finally block can always reach them.
        var audioTempPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var coverArtTempPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // The MP3s the preflight transcodes, kept so the upload does not transcode again.
        // Only populated for non-MP3 sources.
        var beforeUnloadEnabled = false;

        try
        {
        // A batch still awaiting title review is abandoned by this new selection, so release the
        // files it was holding rather than leaking them on the server.
        //
        // Including its staged cover art. That folder used to survive here: the temp files went, the
        // rows went, and the images the browser had already sent to Azure stayed behind with nothing
        // left referencing them - so the only thing that would ever remove them was the container's
        // seven-day rule. Choosing files again is a normal way to change your mind, not an edge case.
        _awaitingTitleConfirmation = false;
        CleanupPendingTempFiles();
        await SweepPendingImageBatchAsync();

        // Clear previous validation errors
        ClearValidationError();
        _filesReceived = false;
        _uploadItems.Clear();

        // Cleared with them, not left to accumulate. This is what MaybeNotifyBatchCompletedAsync
        // reads, and _batchNotificationSent is reset per batch - so a second batch in the same visit
        // used to email the creator a list containing the previous batch's songs as well as its own.
        // Worse in the other direction: one row from an earlier batch left non-terminal, by a
        // poisoned message or a lost callback, blocked the "is everything finished" guard forever and
        // no later batch ever produced a notification at all.
        lock (_jobRowsLock)
        {
            _jobRows.Clear();
        }
        _initialUploadItems.Clear();
        _initialUploadStatusMessage = string.Empty;
        _initialUploadBatchProgress = 0;
        _unmatchedCoverArtFiles.Clear();
        _duplicateSongFiles.Clear();
        _skippedFiles.Clear();

        // Ensure CreatorId is loaded
        if (_currentCreatorId == null)
        {
            Logger.LogWarning("UploadFiles: CreatorId is null at file selection time. Attempting to reload...");
            await LoadCreatorIdAsync();
        }

        // Final check - if CreatorId is still null, the upload cannot proceed
        if (_currentCreatorId == null)
        {
            var selectedFileNames = string.Join(", ", e.GetMultipleFiles(MaxFilesAllowed).Select(f => f.Name));
            Logger.LogError("UploadFiles: CreatorId is still null after reload for user {Email}. Cannot proceed with upload of: {FileNames}", _currentUserEmail, selectedFileNames);
            await SendCreatorIdFailureEmailAsync(selectedFileNames);
            _validationErrorMessage = UploadFailedUserMessage;
            await InvokeAsync(StateHasChanged);
            return;
        }
        
        // Reset upload tracking for this batch
        ResetUploadCancellation();
        _batchNotificationSent = false;

        try
        {
            await EnableBeforeUnloadAsync();
            beforeUnloadEnabled = true;
        }
        catch (JSDisconnectedException) { }

        // Capture file references BEFORE showing the spinner / calling StateHasChanged.
        // In .NET 9+ Blazor Server, calling StateHasChanged before GetMultipleFiles can
        // invalidate IBrowserFile references, causing "There is no file with ID X" errors
        // when OpenReadStream is later called on an audio-only upload.
        var files = e.GetMultipleFiles(MaxFilesAllowed); // Allow up to 50 files

        // Filenames themselves are unconstrained now that storage paths come from a GUID, so a file
        // with an unsupported extension is simply skipped rather than failing the whole batch.
        _skippedFiles.AddRange(files
            .Where(file => !MusicFileExtensions.IsAudioFile(file.Name)
                && !MusicFileExtensions.IsCoverArtFile(file.Name))
            .Select(file => file.Name));

        // Separate files into audio and cover art by original filename
        var audioFilesByName = new Dictionary<string, IBrowserFile>(StringComparer.OrdinalIgnoreCase);
        var coverArtFilesByName = new Dictionary<string, IBrowserFile>(StringComparer.OrdinalIgnoreCase);

        // Position in the browser's own FileList is recorded alongside the partition, because that is
        // the only handle JavaScript can use: an IBrowserFile is a server-side reference into a list
        // JS cannot address, so a direct upload has to say "the file at index 7" instead.
        _browserFileIndexes.Clear();
        _stagedImagePaths.Clear();
        _coverArtTempPaths.Clear();
        _coverArtFileSizes.Clear();
        _coverArtPreviewDataUrls.Clear();
        _heldCoverArt = null;
        _rowCoverArtTargetItem = null;

        // Reserved before the batch's own images are staged, so the review step can never mint an
        // index that collides with one of theirs. Counting the images rather than the whole
        // selection would be wrong the moment a selection contains audio too.
        _nextStagedImageIndex = files.Count;

        for (var browserIndex = 0; browserIndex < files.Count; browserIndex++)
        {
            var file = files[browserIndex];
            var extension = Path.GetExtension(file.Name).ToLowerInvariant();

            if (ValidAudioExtensions.Contains(extension))
            {
                audioFilesByName[file.Name] = file;
            }
            else if (ValidCoverArtExtensions.Contains(extension))
            {
                coverArtFilesByName[file.Name] = file;

                // Recorded now, while IBrowserFile is still valid. The review step can move this
                // image to a different song long after these references have gone stale.
                _coverArtFileSizes[file.Name] = file.Size;
            }
            else
            {
                continue;
            }

            _browserFileIndexes[file.Name] = browserIndex;
        }

        // Size is checked from the metadata the browser already reported, BEFORE any stream is
        // opened. IBrowserFile.OpenReadStream(maxAllowedSize) throws IOException the moment a file
        // exceeds the cap, and an IOException escaping this event handler takes the circuit down
        // rather than rejecting one file - so the creator lost the whole batch to a crash instead of
        // being told which file was too big.
        var oversizedFiles = FindOversizedFiles(
            audioFilesByName.Values, coverArtFilesByName.Values, _maxAudioFileSize, _maxImageFileSize);

        if (oversizedFiles.Count > 0)
        {
            // Whole batch, matching how a corrupt or mislabelled file is handled below: the creator
            // fixes the selection and re-drops it, rather than discovering half of it uploaded.
            _validationErrorMessage =
                "No files were uploaded. These files are larger than the current limit "
                + $"({_maxAudioUploadSizeMBDisplay} MB for audio, {_maxImageUploadSizeMBDisplay} MB for cover art): "
                + string.Join(", ", oversizedFiles);
            _isUploading = false;
            _initialUploadItems.Clear();
            _initialUploadStatusMessage = string.Empty;
            _initialUploadBatchProgress = 0;
            await InvokeAsync(StateHasChanged);
            return;
        }

        // On the direct path the server never receives the audio, so it has nothing to report
        // receiving. Listing those files here would leave a row per song sitting at "Waiting..." for
        // the whole batch and never moving - the audio gets its own bar later, in the processing
        // table, fed by the browser's own progress.
        var receivesAudio = !DirectUploadAvailable;

        _initialUploadItems = BuildInitialUploadProgressFiles(
            receivesAudio ? audioFilesByName.Values : [],
            coverArtFilesByName.Values);
        var initialUploadItemsByName = _initialUploadItems.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var initialUploadProgress = new InitialUploadProgressState
        {
            TotalBytes = _initialUploadItems.Sum(f => f.Size)
        };

        if (_initialUploadItems.Any())
        {
            _isUploading = true;
            _initialUploadStatusMessage = _initialUploadItems.Count == 1
                ? "Receiving 1 file..."
                : $"Receiving {_initialUploadItems.Count} files...";
            await InvokeAsync(StateHasChanged);
        }

        // Buffer ALL IBrowserFile streams to temp files BEFORE calling StateHasChanged.
        // In .NET 9+ Blazor Server, re-rendering the InputFile component clears the
        // JS-side file list, invalidating all IBrowserFile references from the current
        // selection. After buffering to disk, re-renders are safe.

        try
        {
            // The headline saving, and the whole reason this change exists: on the direct path this
            // loop does not run. A 24-song batch is ~1.4 GB that used to be written to a shared
            // host's temp disk in full - over the SignalR circuit, one file at a time - before a
            // single byte reached Azure. Now the browser sends it to Azure and the server never
            // sees it.
            if (receivesAudio)
            {
                foreach (var kvp in audioFilesByName)
                {
                    var tempPath = Path.GetTempFileName();
                    audioTempPaths[kvp.Key] = tempPath;
                    initialUploadItemsByName.TryGetValue(kvp.Key, out var initialUploadItem);
                    await BufferBrowserFileToTempFileAsync(kvp.Value, tempPath, _maxAudioFileSize, initialUploadItem, initialUploadProgress);
                }
            }

            foreach (var kvp in coverArtFilesByName)
            {
                var tempPath = Path.GetTempFileName();
                coverArtTempPaths[kvp.Key] = tempPath;
                initialUploadItemsByName.TryGetValue(kvp.Key, out var initialUploadItem);
                await BufferBrowserFileToTempFileAsync(kvp.Value, tempPath, _maxImageFileSize, initialUploadItem, initialUploadProgress);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // IOException is caught alongside the incomplete-transfer case as a backstop. The size
            // check above should mean OpenReadStream never trips its own limit, but anything else
            // that faults mid-stream - a dropped circuit, a full temp disk - used to escape this
            // handler entirely and kill the page rather than reporting a failed batch.
            Logger.LogWarning(ex, "UploadFiles: File transfer was incomplete.");
            _validationErrorMessage = "No files were uploaded. The upload was interrupted before it finished — please try again.";
            _isUploading = false;
            _initialUploadItems.Clear();
            _initialUploadStatusMessage = string.Empty;
            _initialUploadBatchProgress = 0;
            await DisableBeforeUnloadAsync();
            beforeUnloadEnabled = false;
            await InvokeAsync(StateHasChanged);
            return;
        }


        // Preflight is now a header check only.
        //
        // This used to fully decode every audio file here, and transcode every non-MP3 one - three
        // FFmpeg passes per WAV, on this circuit, before a byte reached Azure. That was the long
        // freeze after "100% received". FFmpeg moved to the audio-processing Azure Function, so
        // what is left is a magic-byte sniff that costs 64 bytes per file and catches the obvious
        // cases (a renamed file, a zero-length one) immediately.
        //
        // The trade: a file that sniffs correctly but does not actually decode is no longer caught
        // here. It is caught in the Function, and the creator is told through the job's progress
        // row rather than an error on this page.
        // Reported per file, and the image half runs off the circuit thread.
        //
        // Not a micro-optimisation: ImageContentMatchesExtension is synchronous and fully decodes
        // each image into an uncompressed bitmap (width x height x 4 - a cap on the *file* size does
        // not bound that). Run inline it blocks the circuit, so nothing renders and no progress is
        // reported for the whole batch. A slow validation pass and a hung one then look identical
        // from the browser: a bar frozen on the last received file, with nothing in the log because
        // nothing threw.
        //
        // On the direct path the audio half of this moves rather than disappearing. There is no local
        // copy to sniff, so the same 64-byte check runs server-side against the staged blob as a
        // ranged read, in CreateFromStagedAsync, after the upload. The cost of that ordering is that
        // a renamed file is rejected one song at a time instead of failing the batch up front - and
        // the creator has already spent the upload before hearing about it.
        var invalidContentFiles = new List<string>();
        var validated = 0;
        var totalToValidate = (receivesAudio ? audioFilesByName.Count : 0) + coverArtFilesByName.Count;

        async Task ReportValidationProgressAsync(string fileName)
        {
            validated++;
            _initialUploadStatusMessage = $"Checking {fileName} ({validated} of {totalToValidate})...";
            await InvokeAsync(StateHasChanged);
        }

        if (receivesAudio)
        {
            foreach (var file in audioFilesByName)
            {
                // Header only - 64 bytes, no decode. Cheap enough to stay on this thread.
                await using (var stream = File.OpenRead(audioTempPaths[file.Key]))
                {
                    if (!AudioContainerSniffer.ContentMatchesExtension(stream, file.Key, out _))
                    {
                        invalidContentFiles.Add(file.Key);
                    }
                }

                await ReportValidationProgressAsync(file.Key);
            }
        }

        foreach (var file in coverArtFilesByName)
        {
            var tempPath = coverArtTempPaths[file.Key];
            var fileName = file.Key;

            var decoded = await Task.Run(() =>
            {
                using var stream = File.OpenRead(tempPath);
                return MediaFileContentValidator.ImageContentMatchesExtension(stream, fileName, out _);
            }, _uploadCts.Token);

            if (!decoded)
            {
                invalidContentFiles.Add(file.Key);
            }

            await ReportValidationProgressAsync(file.Key);
        }
        if (invalidContentFiles.Count > 0)
        {
            _validationErrorMessage =
                "No files were uploaded. These files are corrupt or do not match their extensions: "
                + string.Join(", ", invalidContentFiles);
            _isUploading = false;
            _initialUploadItems.Clear();
            _initialUploadStatusMessage = string.Empty;
            _initialUploadBatchProgress = 0;
            await DisableBeforeUnloadAsync();
            beforeUnloadEnabled = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        // Receiving is only the first slice of this phase: the images still have to be staged and
        // paired before any song exists. Reporting 100 here was the old lie - the bar sat full with a
        // static message for the whole of a matching run nobody could see.
        //
        // Picks up where receiving left off. This used to assign ToOverallPercent(Staging), whose
        // band *start* is 0, so the bar ran to 100 and then snapped back to nothing before jumping
        // forward again. Every stage in this phase now goes through the same calculator.
        // Worded for what actually happened. On the direct path the audio has not been sent anywhere
        // yet - it goes straight from the browser to Azure after the review step - so telling the
        // creator their files were received would be a promise nothing has kept.
        _initialUploadStatusMessage = receivesAudio
            ? "Files received. Matching cover art..."
            : "Matching cover art...";
        _initialUploadBatchProgress = (int)CoverArtMatchProgressCalculator.ToStagingImagesPercent(0d);
        await InvokeAsync(StateHasChanged);

        var matchingResult = await MatchCoverArtAsync(
            audioFilesByName.Keys.ToList(),
            coverArtFilesByName.Keys.ToList(),
            coverArtTempPaths);

        // Track image files that could not be matched (shown below progress table, no error)
        _unmatchedCoverArtFiles = matchingResult.UnmatchedImageFiles;

        // Build the upload item list. All files are already on disk as temp files,
        // so we just pass temp paths through to the upload methods.
        var uploadItemsWithFiles = new List<PendingUpload>();

        foreach (var pair in matchingResult.Pairs)
        {
            // What locates the audio differs by path: a temp file the server wrote, or a position in
            // the browser's own FileList. Either is enough to proceed; neither being present means
            // this pairing names a file nothing can reach, so it is skipped as before.
            audioTempPaths.TryGetValue(pair.AudioFileName, out var audioTempPath);

            // Deliberately not `TryGetValue(...) out var i` folded straight into a nullable. A miss
            // leaves the out parameter at 0, which is a perfectly valid FileList position - so the
            // guard below could never fire on the direct path, and a filename the matcher returned
            // in a form this dictionary does not hold (re-cased, trimmed, normalised) would upload
            // the FIRST selected file's bytes under this song's title. The header sniff downstream
            // would not catch it either, because those bytes are usually still valid audio.
            int? directAudioIndex = null;
            if (!receivesAudio
                && _browserFileIndexes.TryGetValue(pair.AudioFileName, out var browserAudioIndex))
            {
                directAudioIndex = browserAudioIndex;
            }

            if (audioTempPath == null && directAudioIndex == null)
            {
                Logger.LogWarning(
                    "UploadFiles: skipping '{AudioFileName}' - the pairing named a file that is "
                    + "neither buffered on this server nor still present in the browser.",
                    pair.AudioFileName);
                continue;
            }

            audioFilesByName.TryGetValue(pair.AudioFileName, out var audioFileMeta);
            if (audioFileMeta == null)
                continue;

            // The pairing is only a starting point now - the creator can move any of this at the
            // review step - so the row records which image it was given and nothing about where that
            // image lives. Reachability is asked at upload time instead, of whichever image the row
            // is holding by then.
            var matchedImage = pair.ImageFileName;
            var isReachable = !string.IsNullOrEmpty(matchedImage)
                && (directAudioIndex is not null
                    ? _stagedImagePaths.ContainsKey(matchedImage)
                    : coverArtTempPaths.ContainsKey(matchedImage));

            // The filename no longer determines storage. It only seeds the title, which the
            // creator can edit in the review step before anything is uploaded.
            var uploadItem = new UploadPairItem
            {
                SongTitle = SongTitleHelper.FromFileName(pair.AudioFileName),
                TitleSourceFileName = pair.AudioFileName,
                AudioFileName = audioFileMeta.Name,
                AudioFileSize = audioFileMeta.Size,
                Status = UploadStatus.Pending,
                Progress = 0,
                StatusMessage = "Pending"
            };

            SetCoverArt(uploadItem, isReachable ? matchedImage : null);

            // An image the matcher paired but nothing can reach is not "used" - it belongs in the
            // pool, where the creator can at least see it and place it somewhere that works.
            if (!string.IsNullOrEmpty(matchedImage) && !isReachable)
            {
                ReturnToPool(matchedImage);
            }

            _uploadItems.Add(uploadItem);
            uploadItemsWithFiles.Add(new PendingUpload(uploadItem, audioTempPath, directAudioIndex));
        }

        // Receiving is over. The batch bar credits it in full from here, including through the
        // title-review pause when no song has started processing yet.
        _filesReceived = true;
        _initialUploadItems.Clear();
        _initialUploadStatusMessage = string.Empty;
        _initialUploadBatchProgress = 0;

        if (!uploadItemsWithFiles.Any())
        {
            _isUploading = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        // Ownership of the buffered files transfers to the fields below, so this method's
        // `finally` must no longer delete them. Clearing the locals is what signals that.
        _pendingUploads.AddRange(uploadItemsWithFiles);
        _pendingTempFiles.AddRange(audioTempPaths.Values);
        _pendingTempFiles.AddRange(coverArtTempPaths.Values);

        // The cover-art mapping outlives the local, because the review step resolves paths from
        // whichever image a row is holding *then* - which may not be the one it was built with.
        foreach (var (fileName, tempPath) in coverArtTempPaths)
        {
            _coverArtTempPaths[fileName] = tempPath;
        }

        audioTempPaths.Clear();
        coverArtTempPaths.Clear();

        // This used to upload straight through whenever every title happened to be usable, which
        // meant the one step where a creator could see what was about to be published only appeared
        // when something was already wrong - and cover-art pairing, which is a guess and is wrong
        // more often than a title is, could never be corrected at all.
        //
        // Titles are checked either way, so anything broken is already highlighted if the step does
        // appear. A batch with no artwork at all has nothing to show and does not stop.
        var titlesNeedAttention = await MarkInvalidPendingTitlesAsync();

        if (!ShouldPauseForReview())
        {
            await InvokeAsync(StateHasChanged);
            await RunPendingUploadsAsync();
            beforeUnloadEnabled = false;
            return;
        }

        _awaitingTitleConfirmation = true;
        _isUploading = false;
        await DisableBeforeUnloadAsync();
        beforeUnloadEnabled = false;
        await InvokeAsync(StateHasChanged);
        }
        catch (JSException ex) when (
            ex.Message.Contains("There is no file with ID", StringComparison.OrdinalIgnoreCase) &&
            ex.Message.Contains("file list may have changed", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning(ex, "UploadFiles: Browser file list changed before selected files could be buffered.");
            _isUploading = false;
            _validationErrorMessage = "The browser changed the selected file list before StreamTunes could read it. Please drop the files again.";

            _initialUploadItems.Clear();
            _initialUploadStatusMessage = string.Empty;
            _initialUploadBatchProgress = 0;
            await DisableBeforeUnloadAsync();
            beforeUnloadEnabled = false;
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            _isProcessingFiles = false;
            // If an error occurred after showing the spinner via JS but before
            // Blazor took over (_isUploading was set), sync the render state so
            // the spinner is hidden and the upload box is re-enabled.
            if (!_isUploading)
            {
                try { await InvokeAsync(StateHasChanged); }
                catch { }
            }

            if (beforeUnloadEnabled)
            {
                await DisableBeforeUnloadAsync();
            }

            if (!_isUploading)
            {
                _initialUploadItems.Clear();
                _initialUploadStatusMessage = string.Empty;
                _initialUploadBatchProgress = 0;
            }

            // Clean up ALL temp files (safe to call even if already deleted).
            // On the success path these dictionaries were emptied when ownership of the buffered
            // files transferred to the review step, so nothing is deleted out from under it.
            foreach (var path in audioTempPaths.Values)
                TempFileHelper.TryDelete(path, Logger);
            foreach (var path in coverArtTempPaths.Values)
                TempFileHelper.TryDelete(path, Logger);
        }
    }

    // -----------------------------------------------------------------
    // Cover-art re-pairing.
    //
    // Matching is a guess - OCR against a filename - and it gets two things wrong: it leaves an
    // image unpaired, and it pairs the wrong one. Both used to be permanent by the time the creator
    // saw them, and the second was invisible: the old notice listed the unmatched *image* but never
    // said which *song* had ended up bare, so a creator with 24 tracks had to work that out.
    //
    // The invariant everything below maintains: every image selected in this batch is either
    // assigned to exactly one row, or sitting in the pool. Never both, never neither.
    // -----------------------------------------------------------------

    /// <summary>True when this batch has cover art the creator can move around.</summary>
    protected bool CanRepairCoverArt
        => _awaitingTitleConfirmation
            && (_unmatchedCoverArtFiles.Count > 0 || _uploadItems.Any(item => item.HasCoverArt));

    /// <summary>
    /// Whether the pool is worth the space it takes.
    ///
    /// <para>
    /// Only when it holds something, or when the creator is mid-gesture and needs somewhere to let
    /// go. An empty dashed box sitting above a table where every pairing is already correct reads as
    /// a broken widget - it announces a problem that does not exist. Re-pairing stays fully available
    /// either way: the rows themselves are the drag sources, and picking one up is what brings the
    /// box back.
    /// </para>
    /// </summary>
    protected bool ShowCoverArtPool
        => CanRepairCoverArt && (_unmatchedCoverArtFiles.Count > 0 || _heldCoverArt is not null);

    /// <summary>
    /// Whether this batch stops for review before uploading.
    ///
    /// <para>
    /// A broken title always stops it - the upload would fail otherwise. Beyond that the only reason
    /// to pause is artwork, so a batch with no images at all has nothing to show: there is no pairing
    /// to check, no pool to drag from, and the step would be a confirmation dialog wearing a table.
    /// </para>
    /// </summary>
    /// <summary>
    /// Whether the batch stops so the creator can check it before anything publishes.
    ///
    /// <para>
    /// It always does now, and the parameters are kept because they still say WHY. A song
    /// cannot publish without a genre, and the review step is the only place on this page a
    /// genre can be set - so skipping it would trade one pause for a batch the server would
    /// reject. The cover-art checkbox still decides whether the re-pairing controls appear
    /// within the step; it no longer decides whether the step happens.
    /// </para>
    /// </summary>
    internal static bool ShouldPauseForReview() => true;

    /// <summary>The image the creator picked up, so the UI can highlight it and every drop target.</summary>
    protected string HeldCoverArt => _heldCoverArt;

    /// <summary>Songs that will publish with no artwork. Named so the creator does not have to infer it.</summary>
    protected int SongsWithoutCoverArtCount => _uploadItems.Count(item => !item.HasCoverArt);

    /// <summary>Size of a pooled image, for its chip.</summary>
    protected long CoverArtSizeOf(string fileName)
        => fileName is not null && _coverArtFileSizes.TryGetValue(fileName, out var size) ? size : 0;

    /// <summary>
    /// Picks an image up, or puts it down again if it was already held.
    ///
    /// <para>
    /// Toggling matters for the click path: without it a creator who picks the wrong chip has no way
    /// to cancel except by completing a placement they did not want.
    /// </para>
    /// </summary>
    protected void HoldCoverArt(string fileName)
        => _heldCoverArt = string.Equals(_heldCoverArt, fileName, StringComparison.OrdinalIgnoreCase)
            ? null
            : fileName;

    /// <summary>Puts down whatever is held without assigning it.</summary>
    protected void ReleaseCoverArt() => _heldCoverArt = null;

    /// <summary>
    /// Assigns the held image to a song.
    ///
    /// <para>
    /// One image per song, because that is what the pipeline enforces downstream:
    /// <c>CreateFromStagedAsync</c> writes a single cover blob per media GUID, so two rows sharing an
    /// assignment here would just mean the second one silently wins. Everything below is about where
    /// the image being displaced goes instead of nowhere.
    /// </para>
    ///
    /// <para>
    /// Dragging between two songs that both have artwork <em>swaps</em> them rather than pushing one
    /// into the pool. A creator dragging A's cover onto B is almost always fixing a pair the matcher
    /// got backwards, and stranding B's cover would make them do the second half by hand.
    /// </para>
    /// </summary>
    protected void AssignHeldCoverArt(UploadPairItem item)
    {
        if (item is null || _heldCoverArt is null)
        {
            return;
        }

        var incoming = _heldCoverArt;
        _heldCoverArt = null;

        // Already where it was going. Returning early rather than falling through, which would put
        // the image back in the pool and then take it out again.
        if (string.Equals(item.CoverArtFileName, incoming, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var displaced = item.CoverArtFileName;

        // Null when it came straight from the pool.
        var source = _uploadItems.FirstOrDefault(other =>
            !ReferenceEquals(other, item)
            && string.Equals(other.CoverArtFileName, incoming, StringComparison.OrdinalIgnoreCase));

        RemoveFromPool(incoming);
        SetCoverArt(item, incoming);

        if (source is not null)
        {
            // The swap. When the target had nothing, this correctly leaves the source bare rather
            // than handing it an empty string.
            SetCoverArt(source, string.IsNullOrEmpty(displaced) ? null : displaced);
        }
        else if (!string.IsNullOrEmpty(displaced))
        {
            ReturnToPool(displaced);
        }
    }

    /// <summary>
    /// Drops the held image back into the pool, taking it off whichever song was using it.
    ///
    /// <para>
    /// Exists so artwork can be removed from a song without first having somewhere else to put it -
    /// dragging it back to the pool is the same gesture as dragging it onto another song, which is
    /// not true of the row's own remove button.
    /// </para>
    /// </summary>
    protected void ReturnHeldCoverArtToPool()
    {
        if (_heldCoverArt is null)
        {
            return;
        }

        var released = _heldCoverArt;
        _heldCoverArt = null;

        foreach (var item in _uploadItems)
        {
            if (string.Equals(item.CoverArtFileName, released, StringComparison.OrdinalIgnoreCase))
            {
                SetCoverArt(item, null);
            }
        }

        ReturnToPool(released);
    }

    /// <summary>
    /// A stable DOM id for one image's thumbnail.
    ///
    /// <para>
    /// Derived from the filename rather than a counter, because the same image moves between the pool
    /// and a row as the creator works: a positional id would point at a different picture after every
    /// re-pairing, and the thumbnails would silently swap.
    /// </para>
    /// </summary>
    internal static string PreviewElementId(string fileName)
        => "cover-preview-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(fileName ?? string.Empty)))[..16];

    /// <summary>
    /// An inline thumbnail for one image, or null to leave the element for JavaScript to fill.
    ///
    /// <para>
    /// The two halves of one job. Images that came with the batch are addressable in the browser's
    /// own FileList, so <see cref="RefreshCoverArtPreviewsAsync"/> points at those directly and no
    /// bytes cross the circuit; images browsed for at the review step are not, and carry a small
    /// pre-rendered thumbnail instead. Returning null rather than an empty string matters - Blazor
    /// omits a null attribute entirely, leaving the JS path an element with no <c>src</c> to
    /// replace, which is exactly what it expects to find.
    /// </para>
    ///
    /// <para>
    /// Both the row thumbnail and the pool chip go through here, so an image keeps its picture as
    /// the creator moves it between them.
    /// </para>
    /// </summary>
    protected string CoverArtPreviewSource(string fileName)
        => !string.IsNullOrEmpty(fileName)
            && _coverArtPreviewDataUrls.TryGetValue(fileName, out var dataUrl)
                ? dataUrl
                : null;

    /// <summary>
    /// Points every thumbnail at the browser's own copy of its file.
    ///
    /// <para>
    /// Re-run on each render of the review step, because a re-pairing moves an image between elements
    /// and the new element starts with no source. Cheap: creating an object URL does not read the
    /// file, and the previous batch's URLs are revoked before new ones are handed out.
    /// </para>
    /// </summary>
    private async Task RefreshCoverArtPreviewsAsync()
    {
        // Every image on screen, every render, with no attempt to work out whether anything needs
        // doing. Two goes at deciding that from here were both wrong - .NET cannot see whether a
        // given <img> currently holds the right src, and re-pairing and re-rendering both replace
        // elements behind its back. The JS side does the comparing, against the DOM, and only mints
        // a URL when one is genuinely missing.
        var pairs = new List<object>();

        foreach (var fileName in _unmatchedCoverArtFiles)
        {
            if (_browserFileIndexes.TryGetValue(fileName, out var index))
            {
                pairs.Add(new { browserIndex = index, elementId = PreviewElementId(fileName) });
            }
        }

        foreach (var item in _uploadItems)
        {
            if (item.HasCoverArt && _browserFileIndexes.TryGetValue(item.CoverArtFileName, out var index))
            {
                pairs.Add(new { browserIndex = index, elementId = PreviewElementId(item.CoverArtFileName) });
            }
        }

        if (pairs.Count == 0)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("uploadFilesHelper.showCoverArtPreviews", pairs);
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            // Thumbnails are an aid, not the mechanism - the filename beside each one is what the
            // creator is really pairing on, and it renders either way.
            Logger.LogDebug(ex, "Could not render cover-art thumbnails.");
        }
    }

    /// <summary>Returns a song's artwork to the pool, leaving the song to publish without any.</summary>
    protected void ClearCoverArt(UploadPairItem item)
    {
        if (item is null || !item.HasCoverArt)
        {
            return;
        }

        var removed = item.CoverArtFileName;
        SetCoverArt(item, null);
        ReturnToPool(removed);
    }

    /// <summary>
    /// Writes an assignment onto a row, keeping the three display fields consistent.
    ///
    /// <para>
    /// <c>HasCoverArt</c> is set here rather than computed on the row, because rows restored from the
    /// job table on a later visit have a cover but no filename to derive it from.
    /// </para>
    /// </summary>
    private void SetCoverArt(UploadPairItem item, string fileName)
    {
        item.CoverArtFileName = fileName ?? string.Empty;
        item.CoverArtFileSize = fileName is null ? 0 : CoverArtSizeOf(fileName);
        item.HasCoverArt = fileName is not null;
    }

    private void ReturnToPool(string fileName)
    {
        if (!string.IsNullOrEmpty(fileName)
            && !_unmatchedCoverArtFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            _unmatchedCoverArtFiles.Add(fileName);
        }
    }

    /// <summary>
    /// Takes an image out of the pool, matching the way everything else in this page compares
    /// filenames.
    ///
    /// <para>
    /// <c>List&lt;string&gt;.Remove</c> is ordinal and case-sensitive, while every dictionary the
    /// filename travels through - <see cref="_browserFileIndexes"/>, <see cref="_stagedImagePaths"/>,
    /// <see cref="_coverArtTempPaths"/> - is <c>OrdinalIgnoreCase</c>, as is the membership test in
    /// <see cref="ReturnToPool"/>. Mixing the two breaks the invariant this whole area rests on: a
    /// pool holding "Cover.PNG" and a held "cover.png" would remove nothing and then assign it,
    /// leaving one image both pooled and owned, assignable to a second song.
    /// </para>
    /// </summary>
    private void RemoveFromPool(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        _unmatchedCoverArtFiles.RemoveAll(
            pooled => string.Equals(pooled, fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where the assigned image lives on this machine, or null. Server path only.</summary>
    /// <remarks>
    /// Protected, with <see cref="ResolveCoverArtStagedPath"/>, because these two are the exact
    /// question the upload asks of a row - so a test that adds artwork asserts through them rather
    /// than against whichever dictionary happens to be holding it.
    /// </remarks>
    protected string ResolveCoverArtTempPath(UploadPairItem item)
        => item.HasCoverArt && _coverArtTempPaths.TryGetValue(item.CoverArtFileName, out var path)
            ? path
            : null;

    /// <summary>Where the assigned image already sits in staging, or null. Direct path only.</summary>
    protected string ResolveCoverArtStagedPath(UploadPairItem item)
        => item.HasCoverArt && _stagedImagePaths.TryGetValue(item.CoverArtFileName, out var path)
            ? path
            : null;

    /// <summary>
    /// How wide the thumbnail for a browsed image is rendered.
    ///
    /// <para>
    /// The smallest rung of <see cref="ImageVariantSizes.CoverArt"/>, for the same reason that rung
    /// exists: it is the size a 48-DIP picture actually needs. This one is inlined into the page as
    /// base64 rather than fetched, so its size is paid in DOM rather than in bandwidth - a data URL
    /// of a 20 MB master would be some 27 MB of text in the markup for a thumbnail.
    /// </para>
    /// </summary>
    private const int BrowsedCoverArtThumbnailWidth = 128;

    /// <summary>
    /// Opens the file dialog for one song's cover art.
    ///
    /// <para>
    /// Which row asked is remembered here rather than carried through the event, because the change
    /// event arrives on a single shared input that knows nothing about rows. One input rather than
    /// one per row is safe only because the bytes are captured the moment the file is picked - by the
    /// time a second row is browsed, the first row's file has already been read and no longer needs
    /// to be in the input's FileList.
    /// </para>
    /// </summary>
    protected async Task BrowseRowCoverArt(UploadPairItem item)
    {
        if (item is null || !_awaitingTitleConfirmation || _isProcessingRowCoverArt || RowCoverArtInput is null)
        {
            return;
        }

        _rowCoverArtTargetItem = item;

        try
        {
            await JS.InvokeVoidAsync("triggerClick", RowCoverArtInput.Element);
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
    }

    /// <summary>
    /// Takes one image the creator browsed for and gives it to the row that asked.
    ///
    /// <para>
    /// The validation is the same sequence the creator pages use for a song's artwork - extension,
    /// then declared size, then a complete transfer, then a real decode - and in that order, because
    /// each step is cheaper than the one after it and rejects a different lie. The
    /// <c>accept</c> attribute on the input is a filter, not a guarantee; it is trivially bypassed by
    /// picking "All files", so nothing here trusts it.
    /// </para>
    ///
    /// <para>
    /// Nothing is written to the row until every check has passed. A rejected image therefore leaves
    /// whatever artwork the row already had exactly where it was, rather than clearing it and
    /// reporting a problem - which would punish a mis-click by destroying a good pairing.
    /// </para>
    /// </summary>
    protected async Task HandleRowCoverArtSelected(InputFileChangeEventArgs e)
    {
        // Same guard, and the same reason, as HandleFileSelected: a second change event invalidates
        // the IBrowserFile the first one is still reading.
        if (_isProcessingRowCoverArt)
        {
            return;
        }

        _isProcessingRowCoverArt = true;

        var item = _rowCoverArtTargetItem;
        _rowCoverArtTargetItem = null;

        try
        {
            // Captured before anything can re-render, for the reason documented throughout this
            // file: in .NET 9+ Blazor Server a render pass invalidates outstanding IBrowserFile
            // references, and everything below either reads this file or reports why it did not.
            await ApplyBrowsedCoverArtAsync(item, e.File);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: could not attach browsed cover art to a review row.");

            if (item is not null)
            {
                item.CoverArtError = "That image could not be added. Please try again.";
            }
        }
        finally
        {
            _isProcessingRowCoverArt = false;
            await InvokeAsync(StateHasChanged);
            await RefreshCoverArtPreviewsAsync();
        }
    }

    /// <summary>
    /// Checks one browsed image, makes it reachable by whichever upload path this row is on, and
    /// hands it to the row.
    ///
    /// <para>
    /// Split from the event handler so the decisions can be exercised without a renderer: everything
    /// here is state and I/O, while the caller owns the re-entrancy flag and the re-render, neither
    /// of which means anything outside a live circuit.
    /// </para>
    /// </summary>
    protected async Task ApplyBrowsedCoverArtAsync(UploadPairItem item, IBrowserFile file)
    {
        if (item is null || file is null || !_awaitingTitleConfirmation)
        {
            return;
        }

        item.CoverArtError = null;

        if (!MusicFileExtensions.IsCoverArtFile(file.Name))
        {
            item.CoverArtError =
                $"'{file.Name}' is not a supported image. Use "
                + $"{string.Join(", ", ValidCoverArtExtensions)}.";
            return;
        }

        if (file.Size > _maxImageFileSize)
        {
            item.CoverArtError =
                $"'{file.Name}' is larger than the {_maxImageUploadSizeMBDisplay} MB limit for cover art.";
            return;
        }

        // Which path this row is on is read from the row's own pending upload rather than from
        // DirectUploadAvailable, because that is the flag the submit dispatch actually branches on.
        // Asking a different question here could register the image in the dictionary the upload
        // never consults, and the song would publish with no artwork and no error anywhere.
        var pending = _pendingUploads.FirstOrDefault(candidate => ReferenceEquals(candidate.Item, item));
        if (pending is null)
        {
            item.CoverArtError = "This song is no longer part of the batch.";
            return;
        }

        var tempPath = Path.GetTempFileName();
        await BufferBrowserFileToTempFileAsync(
            file, tempPath, _maxImageFileSize, progressItem: null, new InitialUploadProgressState());

        // Registered before the decode, so a file that turns out not to be an image is still cleaned
        // up with the batch rather than left behind on the server.
        _pendingTempFiles.Add(tempPath);

        await using (var probe = new FileStream(
            tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            if (!MediaFileContentValidator.ImageContentMatchesExtension(probe, file.Name, out _))
            {
                item.CoverArtError =
                    $"'{file.Name}' is not a readable image, or its contents do not match its extension.";
                return;
            }
        }

        // Every dictionary in this area is keyed by filename, and a creator browsing for artwork has
        // no idea what the batch already contains. Two images sharing a name would share a staged
        // blob, a size, a thumbnail and a preview element id - so the second row would silently show
        // and publish the first row's picture.
        var fileName = UniqueCoverArtFileName(file.Name);

        if (pending.BrowserAudioIndex is not null)
        {
            // Direct path. The image has to reach the batch folder before the row can point at it,
            // and the browser cannot put it there - it is not in the FileList the uploader indexes
            // into. Assigning _pendingImageBatchId is what puts the blob under the existing sweep, so
            // abandoning the batch removes it rather than orphaning it until the lifecycle rule runs.
            var batchId = _pendingImageBatchId ??= Guid.NewGuid();

            await using var content = new FileStream(
                tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

            _stagedImagePaths[fileName] = await UploadStagingSasService.StageMatchImageAsync(
                batchId,
                _nextStagedImageIndex++,
                Path.GetExtension(fileName),
                content,
                _uploadCts.Token);
        }
        else
        {
            _coverArtTempPaths[fileName] = tempPath;
        }

        _coverArtFileSizes[fileName] = file.Size;

        var thumbnail = BuildCoverArtThumbnail(tempPath);
        if (thumbnail is not null)
        {
            _coverArtPreviewDataUrls[fileName] = thumbnail;
        }

        // The same displaced-artwork rule as AssignHeldCoverArt, and for the same reason: a creator
        // replacing a wrongly matched image is very often about to put the old one on another song,
        // so it goes back to the pool rather than out of reach. RemoveFromPool first, in case this
        // name is somehow already pooled - the invariant is that an image is assigned to exactly one
        // song or sitting in the pool, never both.
        var displaced = item.CoverArtFileName;

        RemoveFromPool(fileName);
        SetCoverArt(item, fileName);

        if (!string.IsNullOrEmpty(displaced))
        {
            ReturnToPool(displaced);
        }
    }

    /// <summary>
    /// A cover-art filename this batch is not already using, preserving the extension - which decides
    /// the staged blob's name and content type, so it cannot be part of what gets changed.
    /// </summary>
    private string UniqueCoverArtFileName(string fileName)
    {
        if (!CoverArtFileNameInUse(fileName))
        {
            return fileName;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        // Bounded rather than open-ended. A creator who has genuinely browsed a thousand images
        // called the same thing is past the point where a nicer name helps, and an unbounded search
        // here would be an unbounded loop on the render path.
        for (var suffix = 2; suffix <= 1000; suffix++)
        {
            var candidate = $"{stem} ({suffix}){extension}";
            if (!CoverArtFileNameInUse(candidate))
            {
                return candidate;
            }
        }

        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }

    /// <summary>
    /// Whether any part of this batch already means something by that filename.
    ///
    /// <para>
    /// Deliberately asks every store rather than just the one about to be written. An image can be
    /// known to the size table and the pool while belonging to neither path's dictionary, and a name
    /// that is free in one and taken in another is still taken.
    /// </para>
    /// </summary>
    private bool CoverArtFileNameInUse(string fileName)
        => _coverArtFileSizes.ContainsKey(fileName)
            || _stagedImagePaths.ContainsKey(fileName)
            || _coverArtTempPaths.ContainsKey(fileName)
            || _browserFileIndexes.ContainsKey(fileName)
            || _unmatchedCoverArtFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase)
            || _uploadItems.Any(row =>
                row.HasCoverArt
                && string.Equals(row.CoverArtFileName, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A small inline copy of an image, or null if it could not be rendered.
    ///
    /// <para>
    /// Best-effort by design. The thumbnail is an aid to pairing and the filename beside it is what
    /// the creator is really working from, so an image Skia cannot resample is still perfectly
    /// uploadable - it just arrives in the table without a picture.
    /// </para>
    /// </summary>
    private string BuildCoverArtThumbnail(string tempPath)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(tempPath);
            if (bitmap is null || bitmap.Width <= 0)
            {
                return null;
            }

            // Clamped, because the encoder resamples to whatever width it is given and will happily
            // upscale - which would turn a 64px image into a 128px blurry one and make the data URL
            // four times the size for no extra detail.
            using var encoded = ImageVariantEncoder.EncodeWebp(
                bitmap, Math.Min(BrowsedCoverArtThumbnailWidth, bitmap.Width));

            return encoded is null
                ? null
                : "data:image/webp;base64," + Convert.ToBase64String(encoded.ToArray());
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not render a thumbnail for browsed cover art.");
            return null;
        }
    }

    /// <summary>
    /// Loads the genre and persona lists, and the display name a song falls back to.
    ///
    /// <para>
    /// Failure is not fatal to the page: the creator can still fix titles and re-pair artwork.
    /// An empty genre list does block the upload, because a song genuinely cannot publish without
    /// one - but it says so rather than presenting an empty picker.
    /// </para>
    /// </summary>
    private async Task LoadReviewChoicesAsync()
    {
        try
        {
            _genres = await GenreService.GetActiveGenresAsync() ?? new();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: Failed to load genres.");
            _genres = new();
        }

        if (!_currentCreatorId.HasValue)
        {
            return;
        }

        try
        {
            var personas = await CreatorPersonaService.GetPersonasByCreatorIdAsync(_currentCreatorId.Value);

            // Disabled personas are hidden here even though the management page shows them: this is
            // a choice about what to publish under, and a disabled persona is not publishable.
            _personas = personas.Where(p => p.IsEnabled).OrderBy(p => p.Name).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: Failed to load personas.");
            _personas = new();
        }

        try
        {
            var creator = await CreatorService.GetCreatorByIdAsync(_currentCreatorId.Value);
            _creatorDisplayName = creator?.DisplayName ?? string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: Failed to load creator display name.");
        }
    }

    /// <summary>Editing a title is how a creator says they have looked at it.</summary>
    protected void OnTitleChanged(UploadPairItem item, string value)
    {
        item.SongTitle = value;
        item.TitleConfirmed = true;
        item.TitleError = null;
    }

    protected void ConfirmTitle(UploadPairItem item, bool confirmed) => item.TitleConfirmed = confirmed;

    protected void ConfirmAllTitles()
    {
        // For creators who name their files properly. Making them tick fifty boxes to prove it
        // would be a tax on the good behaviour this step is trying to encourage.
        foreach (var item in _uploadItems)
        {
            item.TitleConfirmed = true;
        }
    }

    /// <summary>
    /// The AI disclosure is two groups that exclude each other: one or more of the three AI
    /// answers, or "all original". Ticking either side clears the other.
    ///
    /// <para>
    /// It is a required choice rather than three optional boxes because three unticked boxes said
    /// two different things at once - "none of this is AI" and "nobody answered" - and only one of
    /// those is a disclosure.
    /// </para>
    /// </summary>
    internal void SetAllOriginal(UploadPairItem item, bool allOriginal)
    {
        item.AllOriginal = allOriginal;

        if (allOriginal)
        {
            item.IsAiGenerated = false;
            item.IsAiVocals = false;
            item.IsAiLyrics = false;
        }

        item.AiError = null;
    }

    internal void SetAiMusic(UploadPairItem item, bool value) => SetAiFlag(item, () => item.IsAiGenerated = value, value);
    internal void SetAiVocals(UploadPairItem item, bool value) => SetAiFlag(item, () => item.IsAiVocals = value, value);
    internal void SetAiLyrics(UploadPairItem item, bool value) => SetAiFlag(item, () => item.IsAiLyrics = value, value);

    private static void SetAiFlag(UploadPairItem item, Action assign, bool value)
    {
        assign();

        if (value)
        {
            item.AllOriginal = false;
        }

        item.AiError = null;
    }

    /// <summary>
    /// The bulk half of the disclosure. Holds the same exclusivity as a row - ticking either
    /// side clears the other - and then writes the whole answer onto every song.
    /// </summary>
    internal void ApplyAllOriginal(bool value)
    {
        _applyAllOriginal = value;

        if (value)
        {
            _applyAllAiMusic = false;
            _applyAllAiVocals = false;
            _applyAllAiLyrics = false;
        }

        PushAiAnswerToEveryRow();
    }

    internal void ApplyAllAiMusic(bool value)
    {
        _applyAllAiMusic = value;
        ClearApplyAllOriginalIf(value);
        PushAiAnswerToEveryRow();
    }

    internal void ApplyAllAiVocals(bool value)
    {
        _applyAllAiVocals = value;
        ClearApplyAllOriginalIf(value);
        PushAiAnswerToEveryRow();
    }

    internal void ApplyAllAiLyrics(bool value)
    {
        _applyAllAiLyrics = value;
        ClearApplyAllOriginalIf(value);
        PushAiAnswerToEveryRow();
    }

    private void ClearApplyAllOriginalIf(bool value)
    {
        if (value)
        {
            _applyAllOriginal = false;
        }
    }

    /// <summary>
    /// Writes the bulk answer over every row, including rows already answered - the same rule
    /// the genre and persona controls follow, and for the same reason: a control that behaves
    /// differently depending on state you cannot see is worse than one that plainly overwrites.
    ///
    /// <para>
    /// Unticking the last bulk answer leaves every row undeclared rather than silently original,
    /// which matches what unticking the last answer on a row does.
    /// </para>
    /// </summary>
    private void PushAiAnswerToEveryRow()
    {
        foreach (var item in _uploadItems)
        {
            item.AllOriginal = _applyAllOriginal;
            item.IsAiGenerated = _applyAllAiMusic;
            item.IsAiVocals = _applyAllAiVocals;
            item.IsAiLyrics = _applyAllAiLyrics;
            item.AiError = null;
        }
    }

    protected void SetRowGenre(UploadPairItem item, string genre)
    {
        item.Genre = genre ?? string.Empty;
        item.GenreError = null;
    }

    protected void ApplyGenreToAll()
    {
        if (string.IsNullOrWhiteSpace(_applyAllGenre))
        {
            return;
        }

        foreach (var item in _uploadItems)
        {
            SetRowGenre(item, _applyAllGenre);
        }
    }

    protected void ApplyPersonaToAll()
    {
        foreach (var item in _uploadItems)
        {
            item.PersonaId = _applyAllPersonaId;
        }
    }

    // ---------------------------------------------------------- inline create

    protected void BeginAddGenre(UploadPairItem row)
    {
        _addGenreForRow = row;
        _newGenreName = string.Empty;
        _addGenreError = string.Empty;
        _showAddGenre = true;
    }

    protected void CancelAddGenre()
    {
        _showAddGenre = false;
        _addGenreForRow = null;
        _newGenreName = string.Empty;
        _addGenreError = string.Empty;
    }

    /// <summary>
    /// Creates a genre without leaving the page, and selects it where the creator was working.
    ///
    /// <para>
    /// AddGenreAsync returns null when the name already exists, case-insensitively. That is not an
    /// error worth stopping for: the thing the creator wanted is there, so say so and select it.
    /// </para>
    /// </summary>
    protected async Task ConfirmAddGenreAsync()
    {
        var name = (_newGenreName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            _addGenreError = "Enter a name for the new genre.";
            return;
        }

        _addingGenre = true;
        _addGenreError = string.Empty;

        try
        {
            var existing = _genres.FirstOrDefault(g =>
                string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                var created = await GenreService.AddGenreAsync(name, _currentUserEmail);
                if (created == null)
                {
                    // Already in the table but not in the list we loaded - a disabled genre, or one
                    // added by someone else since this page opened. Reload rather than insist.
                    _genres = await GenreService.GetActiveGenresAsync() ?? _genres;
                    existing = _genres.FirstOrDefault(g =>
                        string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

                    if (existing == null)
                    {
                        _addGenreError = $"\u2018{name}\u2019 already exists but is not available to choose.";
                        return;
                    }
                }
                else
                {
                    _genres.Add(created);
                    _genres = _genres.OrderBy(g => g.Name).ToList();
                    existing = created;
                }
            }

            if (_addGenreForRow != null)
            {
                SetRowGenre(_addGenreForRow, existing.Name);
            }

            // Offered for the whole batch, because a batch is usually one release.
            _applyAllGenre = existing.Name;
            CancelAddGenre();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: Failed to add genre {GenreName}.", name);
            _addGenreError = "The genre could not be added. Please try again.";
        }
        finally
        {
            _addingGenre = false;
        }
    }

    protected void BeginAddPersona(UploadPairItem row)
    {
        _addPersonaForRow = row;
        _newPersonaName = string.Empty;
        _addPersonaError = string.Empty;
        _showAddPersona = true;
    }

    protected void CancelAddPersona()
    {
        _showAddPersona = false;
        _addPersonaForRow = null;
        _newPersonaName = string.Empty;
        _addPersonaError = string.Empty;
    }

    /// <summary>
    /// Creates a persona from a name alone. Image, bio and website come later on Manage personas.
    ///
    /// <para>
    /// Unlike genres, a duplicate here is refused rather than reused: two personas with the same
    /// name are indistinguishable on a song card, so CreatePersonaAsync throws and the message it
    /// throws with is the one worth showing.
    /// </para>
    /// </summary>
    protected async Task ConfirmAddPersonaAsync()
    {
        var name = (_newPersonaName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            _addPersonaError = "Enter a name for the new persona.";
            return;
        }

        if (!_currentCreatorId.HasValue)
        {
            _addPersonaError = "Your creator account could not be found.";
            return;
        }

        _addingPersona = true;
        _addPersonaError = string.Empty;

        try
        {
            var created = await CreatorPersonaService.CreatePersonaAsync(
                _currentCreatorId.Value, name, null, null);

            _personas.Add(created);
            _personas = _personas.OrderBy(x => x.Name).ToList();

            if (_addPersonaForRow != null)
            {
                _addPersonaForRow.PersonaId = created.Id;
            }

            _applyAllPersonaId = created.Id;
            CancelAddPersona();
        }
        catch (InvalidOperationException ex)
        {
            _addPersonaError = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: Failed to add persona {PersonaName}.", name);
            _addPersonaError = "The persona could not be added. Please try again.";
        }
        finally
        {
            _addingPersona = false;
        }
    }

    /// <summary>
    /// Checks everything the review step is responsible for, and marks what the creator has to
    /// fix. Returns true when the batch cannot upload as-is.
    ///
    /// <para>
    /// Three separate things, reported together because a creator would rather see all of them
    /// at once than be sent back three times: the title has to be valid, it has to have been
    /// looked at, and the song has to have a genre.
    /// </para>
    /// </summary>
    /// <summary>
    /// Marks every title that is genuinely unusable - empty, too long, or colliding - and
    /// returns whether any were.
    ///
    /// <para>
    /// Deliberately says nothing about whether a title has been LOOKED at. This runs before the
    /// review step is shown, to decide whether it needs to be, and reporting "nothing has been
    /// checked" at that point would scold the creator for not having seen a screen they have
    /// not been given yet.
    /// </para>
    /// </summary>
    private async Task<bool> MarkInvalidPendingTitlesAsync()
    {
        foreach (var pending in _pendingUploads)
        {
            pending.Item.SongTitle = (pending.Item.SongTitle ?? string.Empty).Trim();
            pending.Item.TitleError = null;
        }

        foreach (var pending in _pendingUploads)
        {
            var error = SongTitleHelper.GetTitleValidationErrors(pending.Item.SongTitle).FirstOrDefault();
            if (error != null)
            {
                pending.Item.TitleError = error;
            }
        }

        foreach (var collision in _pendingUploads
            .Where(pending => pending.Item.TitleError == null)
            .GroupBy(pending => pending.Item.SongTitle, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group))
        {
            collision.Item.TitleError = "Another file in this batch has the same title.";
        }

        try
        {
            var existingTitles = await SongMetadataService.FindExistingSongTitlesAsync(
                _pendingUploads
                    .Where(pending => pending.Item.TitleError == null)
                    .Select(pending => pending.Item.SongTitle)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());

            foreach (var duplicate in _pendingUploads
                .Where(pending => pending.Item.TitleError == null
                    && existingTitles.Contains(pending.Item.SongTitle)))
            {
                duplicate.Item.TitleError = "You already have a song with this title.";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: Failed to check for duplicate song titles.");
            _validationErrorMessage =
                "No files were uploaded because existing song titles could not be checked. Please try again.";
            return true;
        }

        return _pendingUploads.Any(pending => pending.Item.TitleError != null);
    }

    /// <summary>
    /// Checks everything the review step is responsible for, and marks what the creator has to
    /// fix. Returns true when the batch cannot upload as-is.
    ///
    /// <para>
    /// Three separate things, reported together because a creator would rather see all of them
    /// at once than be sent back three times: the title has to be valid, it has to have been
    /// looked at, and the song has to have a genre.
    /// </para>
    /// </summary>
    private async Task<bool> PendingBatchNeedsAttentionAsync()
    {
        await MarkInvalidPendingTitlesAsync();

        // A genre is required for a song to publish at all, so this is not a preference the
        // batch can proceed without.
        foreach (var pending in _pendingUploads)
        {
            pending.Item.GenreError = string.IsNullOrWhiteSpace(pending.Item.Genre)
                ? "Choose a genre."
                : null;
        }

        // A disclosure nobody made is not a disclosure. Leaving it optional made silence mean
        // "no AI", which is the one reading a creator never actually chose.
        foreach (var pending in _pendingUploads)
        {
            pending.Item.AiError = pending.Item.AiDeclared
                ? null
                : "Say whether any of this is AI.";
        }

        var badTitles = _pendingUploads.Count(pending => pending.Item.TitleError != null);
        var unchecked_ = _pendingUploads.Count(pending => pending.Item.TitleError == null
            && !pending.Item.TitleConfirmed);
        var missingGenres = _pendingUploads.Count(pending => pending.Item.GenreError != null);
        var missingAi = _pendingUploads.Count(pending => pending.Item.AiError != null);

        if (badTitles == 0 && unchecked_ == 0 && missingGenres == 0 && missingAi == 0)
            return false;

        var problems = new List<string>();
        if (badTitles == 1)
        {
            problems.Add("one song needs a different title");
        }
        else if (badTitles > 1)
        {
            problems.Add($"{badTitles} songs need different titles");
        }

        if (unchecked_ == 1)
        {
            problems.Add("one title has not been checked");
        }
        else if (unchecked_ > 1)
        {
            problems.Add($"{unchecked_} titles have not been checked");
        }

        if (missingGenres == 1)
        {
            problems.Add("one song needs a genre");
        }
        else if (missingGenres > 1)
        {
            problems.Add($"{missingGenres} songs need a genre");
        }

        if (missingAi == 1)
        {
            problems.Add("one song needs an AI answer");
        }
        else if (missingAi > 1)
        {
            problems.Add($"{missingAi} songs need an AI answer");
        }

        _validationErrorMessage =
            $"Nothing was uploaded: {string.Join(", and ", problems)}. They are highlighted below.";
        return true;
    }

    /// <summary>
    /// Uploads a batch the creator had to correct first. Only reachable from the review step.
    /// </summary>
    protected async Task StartUploadAsync()
    {
        if (!_awaitingTitleConfirmation || _isUploading)
            return;

        ClearValidationError();

        if (await PendingBatchNeedsAttentionAsync())
        {
            await InvokeAsync(StateHasChanged);
            return;
        }

        _awaitingTitleConfirmation = false;
        await RunPendingUploadsAsync();
    }

    /// <summary>
    /// Runs the buffered batch. Titles have already been checked by this point.
    /// </summary>
    private async Task RunPendingUploadsAsync()
    {
        _isUploading = true;
        ResetUploadCancellation();
        _batchNotificationSent = false;

        var beforeUnloadEnabled = false;
        try
        {
            try
            {
                await EnableBeforeUnloadAsync();
                beforeUnloadEnabled = true;
            }
            catch (JSDisconnectedException) { }

            var uploads = _pendingUploads.ToList();
            if (!uploads.Any())
                return;

            await ProcessUploadsInChunksAsync(uploads);

            // The batch notification is NOT sent here any more. Reaching this line means the files
            // have been staged and queued - the songs do not exist yet, so a lookup by blob path
            // would match nothing and the email would list an empty batch. It is sent instead from
            // MaybeNotifyBatchCompletedAsync, once the pipeline reports the last song assembled.
            await MaybeNotifyBatchCompletedAsync();
        }
        finally
        {
            _isUploading = false;
            CleanupPendingTempFiles();

            // In the finally, and only here. Every song that matched an image has by now had it
            // copied into its own folder, so the batch folder is a spent staging area - but an empty
            // batch returns early above and a cancelled one throws past it, and either leaving the
            // folder behind for the lifecycle rule or sweeping it twice beats the one ordering that
            // is actually wrong: sweeping it while a song still needs to copy from it.
            await SweepPendingImageBatchAsync();

            if (beforeUnloadEnabled)
            {
                await DisableBeforeUnloadAsync();
            }

            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Deletes the match batch's staged images, once nothing still needs them.
    ///
    /// <para>
    /// Only reaches storage on the direct path — on the server path the folder was swept as soon as
    /// the pairing arrived, because the images themselves were never in it in any sense that
    /// mattered. Idempotent, and called from every exit including cancel and disposal.
    /// </para>
    /// </summary>
    /// <summary>
    /// Releases the thumbnails' object URLs.
    ///
    /// <para>
    /// Revocation used to happen only at the <em>start</em> of the next preview call, so leaving the
    /// review step by any route - cancel, upload, unticking the checkbox - pinned every image's blob
    /// in memory for the life of the page.
    /// </para>
    /// </summary>
    private async Task ReleaseCoverArtPreviewsAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("uploadFilesHelper.revokeCoverArtPreviews");
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not release cover-art thumbnails.");
        }
    }

    /// <summary>
    /// Replaces the upload cancellation source, disposing the one it supersedes.
    ///
    /// <para>
    /// Each selection and each run used to allocate a fresh source and drop the previous reference,
    /// so a creator who selected, cancelled and re-selected leaked one per cycle - each still holding
    /// the registrations taken in <see cref="UploadDirectAsync"/>.
    /// </para>
    /// </summary>
    private void ResetUploadCancellation()
    {
        var previous = _uploadCts;
        _uploadCts = new CancellationTokenSource();

        try
        {
            previous?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not dispose the previous upload cancellation source.");
        }
    }

    private async Task SweepPendingImageBatchAsync()
    {
        var batchId = _pendingImageBatchId;
        _pendingImageBatchId = null;
        _stagedImagePaths.Clear();

        // Dropped with the blobs they depict. These are plain strings rather than object URLs, so
        // there is nothing for ReleaseCoverArtPreviewsAsync to revoke - but leaving them would keep
        // a discarded batch's thumbnails resident for the life of the circuit.
        _coverArtPreviewDataUrls.Clear();

        await ReleaseCoverArtPreviewsAsync();

        if (batchId is null)
        {
            return;
        }

        try
        {
            await CoverArtMatchService.DeleteBatchAsync(batchId.Value, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The lifecycle rule on the staging container sweeps this within the week regardless, so
            // a failure here costs storage rather than correctness.
            Logger.LogWarning(ex, "Could not delete staged cover art for batch {BatchId}", batchId);
        }
    }

    /// <summary>Discards a reviewed-but-not-yet-uploaded batch.</summary>
    /// <summary>
    /// Drops one song from the batch, leaving the rest to upload.
    ///
    /// <para>
    /// The case this exists for is a duplicate title. Before it, a creator who dropped in three
    /// songs and found one already in their catalogue had two options - rename a song they did not
    /// want to rename, or cancel all three and start again with a different selection. Neither is
    /// what they wanted, which was to upload the other two.
    /// </para>
    ///
    /// <para>
    /// Its artwork goes back to the pool rather than being discarded: the image may well belong to
    /// one of the songs that is staying, and this is exactly the moment a creator discovers that.
    /// The audio temp file is deleted here rather than left to the batch sweep, because the batch
    /// may now run for several more minutes and nothing else will come back for it.
    /// </para>
    /// </summary>
    protected async Task RemoveFromBatchAsync(UploadPairItem item)
    {
        if (item is null || !_awaitingTitleConfirmation)
        {
            return;
        }

        ClearCoverArt(item);

        var pending = _pendingUploads.FirstOrDefault(p => ReferenceEquals(p.Item, item));
        if (pending != null)
        {
            _pendingUploads.Remove(pending);

            if (!string.IsNullOrEmpty(pending.AudioTempPath))
            {
                TempFileHelper.TryDelete(pending.AudioTempPath, Logger);
                _pendingTempFiles.Remove(pending.AudioTempPath);
            }
        }

        _uploadItems.Remove(item);

        // Removing the last one is a cancel by another route, and has to tear down the same way -
        // otherwise the page sits on an empty review step with an upload button that does nothing.
        if (_uploadItems.Count == 0)
        {
            await CancelPendingBatchAsync();
            return;
        }

        // The summary was counting a row that no longer exists. Re-running the check rather than
        // decrementing keeps one description of what is outstanding instead of two.
        ClearValidationError();
        if (_uploadItems.Any(row => row.TitleError != null || row.GenreError != null))
        {
            await PendingBatchNeedsAttentionAsync();
        }

        await InvokeAsync(StateHasChanged);
    }

    protected async Task CancelPendingBatchAsync()
    {
        if (!_awaitingTitleConfirmation)
            return;

        _awaitingTitleConfirmation = false;
        CleanupPendingTempFiles();
        await SweepPendingImageBatchAsync();
        _uploadItems.Clear();
        _unmatchedCoverArtFiles.Clear();
        _skippedFiles.Clear();
        ClearValidationError();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Deletes the buffered files held for the review step. Safe to call repeatedly, and reached
    /// from every exit: upload completion, cancel, navigation away, and disposal.
    /// </summary>
    private void CleanupPendingTempFiles()
    {
        foreach (var path in _pendingTempFiles)
            TempFileHelper.TryDelete(path, Logger);

        _pendingTempFiles.Clear();
        _pendingUploads.Clear();
    }

    /// <summary>
    /// Names the selected files that exceed their type's cap, from the sizes the browser already
    /// reported.
    ///
    /// <para>
    /// Deliberately reads <see cref="IBrowserFile.Size"/> rather than letting
    /// <c>OpenReadStream(maxAllowedSize)</c> enforce the limit. That method throws
    /// <see cref="IOException"/>, and an IOException escaping the file-selection event handler takes
    /// the circuit down — so a creator who picked one file that was 2 MB too big lost the entire
    /// batch to a crash, with nothing telling them which file or why.
    /// </para>
    ///
    /// <para>
    /// Static and parameterised so the rule is directly testable; the caps are runtime admin
    /// settings, so the interesting cases are all boundary conditions rather than fixed numbers.
    /// </para>
    /// </summary>
    internal static List<string> FindOversizedFiles(
        IEnumerable<IBrowserFile> audioFiles,
        IEnumerable<IBrowserFile> coverArtFiles,
        long maxAudioBytes,
        long maxImageBytes)
    {
        var oversized = new List<string>();

        foreach (var file in audioFiles ?? [])
        {
            if (maxAudioBytes > 0 && file.Size > maxAudioBytes)
            {
                oversized.Add(file.Name);
            }
        }

        foreach (var file in coverArtFiles ?? [])
        {
            if (maxImageBytes > 0 && file.Size > maxImageBytes)
            {
                oversized.Add(file.Name);
            }
        }

        return oversized;
    }

    private List<InitialUploadProgressItem> BuildInitialUploadProgressFiles(
        IEnumerable<IBrowserFile> audioFiles,
        IEnumerable<IBrowserFile> coverArtFiles)
    {
        var files = new List<InitialUploadProgressItem>();
        var index = 0;

        foreach (var file in audioFiles)
        {
            files.Add(new InitialUploadProgressItem
            {
                Index = index++,
                Name = file.Name,
                FileType = "Audio",
                Size = file.Size,
                FormattedSize = FormatFileSize(file.Size),
                Progress = 0,
                StatusMessage = "Waiting..."
            });
        }

        foreach (var file in coverArtFiles)
        {
            files.Add(new InitialUploadProgressItem
            {
                Index = index++,
                Name = file.Name,
                FileType = "Cover Art",
                Size = file.Size,
                FormattedSize = FormatFileSize(file.Size),
                Progress = 0,
                StatusMessage = "Waiting..."
            });
        }

        return files;
    }

    private async Task BufferBrowserFileToTempFileAsync(
        IBrowserFile browserFile,
        string tempPath,
        long maxAllowedSize,
        InitialUploadProgressItem progressItem,
        InitialUploadProgressState progressState)
    {
        const int bufferSize = 81920;
        var buffer = new byte[bufferSize];
        long fileBytesReceived = 0;

        await ReportInitialUploadProgressAsync(progressItem, fileBytesReceived, browserFile.Size, progressState, "Receiving...", force: true);

        await using var browserStream = browserFile.OpenReadStream(maxAllowedSize);
        await using var tempFileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

        while (true)
        {
            var bytesRead = await browserStream.ReadAsync(buffer.AsMemory(0, buffer.Length), _uploadCts.Token);
            if (bytesRead == 0)
            {
                break;
            }

            await tempFileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _uploadCts.Token);
            fileBytesReceived += bytesRead;
            progressState.BytesReceived += bytesRead;

            await ReportInitialUploadProgressAsync(progressItem, fileBytesReceived, browserFile.Size, progressState, "Receiving...");
        }

        await ReportInitialUploadProgressAsync(progressItem, fileBytesReceived, browserFile.Size, progressState, "Received", force: true);
        MediaTransferValidator.RequireComplete(browserFile.Name, browserFile.Size, fileBytesReceived);
    }

    private async Task ReportInitialUploadProgressAsync(
        InitialUploadProgressItem progressItem,
        long fileBytesReceived,
        long fileSize,
        InitialUploadProgressState progressState,
        string statusText,
        bool force = false)
    {
        if (progressItem == null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && now - progressState.LastReportedAtUtc < InitialUploadProgressUpdateInterval)
        {
            return;
        }

        progressState.LastReportedAtUtc = now;
        var filePercent = CalculatePercent(fileBytesReceived, fileSize);
        var batchPercent = CalculatePercent(progressState.BytesReceived, progressState.TotalBytes);

        progressItem.Progress = filePercent;
        progressItem.StatusMessage = statusText;

        // Mapped onto the receiving phase's first band rather than assigned raw. Receiving is no
        // longer the whole of this phase - staging the images and matching them follow it - so a raw
        // 0-100 here reached 100 and then snapped back to 0 when the next stage set its own band.
        _initialUploadBatchProgress = (int)CoverArtMatchProgressCalculator.ToReceivingPercent(batchPercent);

        _initialUploadStatusMessage = statusText == "Received"
            ? $"Received {progressItem.Name}"
            : $"Receiving {progressItem.Name}...";

        await InvokeAsync(StateHasChanged);
    }

    private static int CalculatePercent(long current, long total)
    {
        if (total <= 0)
        {
            return 100;
        }

        return (int)Math.Clamp(Math.Round(current * 100d / total), 0, 100);
    }

    private async Task EnableBeforeUnloadAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("uploadFilesHelper.enableBeforeUnload");
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }

    private async Task DisableBeforeUnloadAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("uploadFilesHelper.disableBeforeUnload");
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Stages every upload to Azure, keeping a bounded number of <em>files</em> - not pairs - in
    /// flight at once. On the server path a pair counts as two, so a song and its cover art are never
    /// split across the boundary; on the direct path a song counts as one, because its cover art went
    /// up during the image phase and is copied within the storage account rather than transferred.
    ///
    /// <para>
    /// A sliding window rather than fixed batches. This used to await <c>Task.WhenAll</c> over a
    /// chunk before starting the next one, which made every chunk boundary a barrier: one slow song
    /// held up the seven finished beside it, and the window sat mostly idle waiting for it. Here a
    /// slot is reused the moment its upload finishes, so the concurrency limit is a real limit
    /// rather than an average.
    /// </para>
    ///
    /// <para>
    /// The limit exists to bound concurrent network transfers, not CPU: staging is a streamed PUT
    /// per file, and the decode/transcode/encode work that used to run here now runs in the Azure
    /// Function.
    /// </para>
    /// </summary>
    private async Task ProcessUploadsInChunksAsync(IEnumerable<PendingUpload> uploadItemsWithFiles)
    {
        var itemsList = uploadItemsWithFiles.ToList();
        if (itemsList.Count == 0)
        {
            return;
        }

        // Whose bandwidth the window is protecting decides how wide it is. On the direct path these
        // transfers leave the creator's connection, not this server's, so the narrower browser limit
        // applies - and each item costs one, because its cover art was uploaded during the image
        // phase and is copied server-side within the storage account rather than transferred again.
        var direct = itemsList.Any(pending => pending.BrowserAudioIndex is not null);
        var window = direct ? BrowserUploadConcurrency : ChunkSize;

        // Counts files rather than items, so the window means the same thing it always did.
        using var slots = new SemaphoreSlim(window, window);
        var running = new List<Task>(itemsList.Count);

        try
        {
            foreach (var pending in itemsList)
            {
                // An item wider than the whole window would wait forever for slots that can never
                // all be free at once, so clamp it. Only reachable if the window is ever set below 2.
                var cost = pending.BrowserAudioIndex is not null
                    ? 1
                    : Math.Min(ResolveCoverArtTempPath(pending.Item) != null ? 2 : 1, window);

                // Acquired one at a time, which is safe only because this loop is the sole acquirer:
                // nothing else can interleave and hold the slots this call is waiting for, and the
                // uploads already running are guaranteed to release.
                for (var i = 0; i < cost; i++)
                {
                    await slots.WaitAsync(_uploadCts.Token);
                }

                running.Add(RunAsync(pending, cost));
            }
        }
        finally
        {
            // In a finally because cancellation aborts the loop above with uploads already in
            // flight, and both cancellation paths - the navigate-away prompt and DisposeAsync -
            // delete the staged temp files immediately afterwards. Returning before these settle
            // would pull those files out from under a stream still reading them.
            await Task.WhenAll(running);
        }

        async Task RunAsync(PendingUpload pending, int cost)
        {
            try
            {
                // All three already record their own failures against the row rather than throwing,
                // so one bad song cannot tear down the rest of the batch.
                await (pending.BrowserAudioIndex is not null
                    ? UploadFromBrowserAsync(pending)
                    : ResolveCoverArtTempPath(pending.Item) != null
                        ? UploadFilePairAsync(pending)
                        : UploadAudioOnlyAsync(pending));
            }
            finally
            {
                slots.Release(cost);
            }
        }
    }

    /// <summary>
    /// Maps the browser-to-Azure staging upload onto the first band of the overall bar, so it
    /// starts moving immediately rather than sitting at zero until the queue picks the song up.
    /// </summary>
    private IProgress<double> BuildStagingProgress(UploadPairItem uploadItem)
        => new Progress<double>(percent =>
        {
            uploadItem.Progress = (int)AudioProcessingProgressCalculator.ToOverallPercent(
                AudioProcessingStep.Staging,
                percent);
            _ = InvokeAsync(StateHasChanged);
        });

    /// <summary>
    /// Marks a row as handed off to the pipeline.
    ///
    /// <para>
    /// Note what this deliberately does <em>not</em> do: it no longer records blob paths for
    /// cleanup-on-abandon. Once a job is queued the song completes whether or not the creator stays
    /// on the page, which is the point of moving the work off the request thread - deleting its
    /// blobs on navigation would sabotage a transcode already in flight.
    /// </para>
    /// </summary>
    private void TrackQueuedJob(UploadPairItem uploadItem, Models.SongUploadJob job)
    {
        uploadItem.MediaGuid = job.MediaGuid;
        uploadItem.Step = AudioProcessingStep.Queued;
        uploadItem.Status = UploadStatus.Processing;
        uploadItem.Progress = (int)AudioProcessingProgressCalculator.ToOverallPercent(AudioProcessingStep.Queued);
        uploadItem.StatusMessage = DescribeStep(AudioProcessingStep.Queued);
        uploadItem.ErrorMessage = null;

        lock (_jobRowsLock)
        {
            _jobRows[job.MediaGuid] = uploadItem;
        }

        StartJobStatePolling();
    }

    /// <summary>
    /// Starts the fallback poller if it is not already running. Idempotent; stops on its own once
    /// every tracked row reaches a terminal step.
    /// </summary>
    private void StartJobStatePolling()
    {
        if (_jobPollCts is not null || _currentCreatorId is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _jobPollCts = cts;
        _ = PollJobStatesAsync(cts);
    }

    private async Task PollJobStatesAsync(CancellationTokenSource ownCts)
    {
        var cancellationToken = ownCts.Token;

        try
        {
            using var timer = new PeriodicTimer(JobStatePollInterval);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Guid[] pending;
                lock (_jobRowsLock)
                {
                    pending = _jobRows
                        .Where(entry => !AudioProcessingProgressCalculator.IsTerminal(entry.Value.Step))
                        .Select(entry => entry.Key)
                        .ToArray();
                }

                if (pending.Length == 0)
                {
                    return;
                }

                var jobs = await SongUploadJobService.GetRecentJobsAsync(
                    _currentCreatorId!.Value,
                    cancellationToken: cancellationToken);

                var changed = false;
                foreach (var job in jobs)
                {
                    UploadPairItem row;
                    lock (_jobRowsLock)
                    {
                        if (!_jobRows.TryGetValue(job.MediaGuid, out row))
                        {
                            continue;
                        }
                    }

                    // Only ever move forwards, same rule the hub path follows - a poll that raced a
                    // live update must not drag the bar back.
                    if (!AudioProcessingProgressCalculator.IsAdvance(row.Step, job.Step))
                    {
                        continue;
                    }

                    row.Step = job.Step;
                    row.Progress = (int)AudioProcessingProgressCalculator.ToOverallPercent(job.Step);
                    row.Status = job.Status switch
                    {
                        Models.SongUploadJobStatus.Completed => UploadStatus.Completed,
                        Models.SongUploadJobStatus.Failed => UploadStatus.Failed,
                        _ => UploadStatus.Processing
                    };
                    row.StatusMessage = DescribeStep(job.Step);
                    row.ErrorMessage = job.FailureMessage;
                    changed = true;
                }

                if (changed)
                {
                    await InvokeAsync(StateHasChanged);
                    await MaybeNotifyBatchCompletedAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Page went away.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Upload job state polling stopped unexpectedly");
        }
        finally
        {
            // Release the field so a second batch can start a fresh poller. The loop returns of its
            // own accord once every row is terminal, and leaving a spent source parked here meant
            // StartJobStatePolling saw "already running" forever after - so a second batch uploaded
            // in the same visit had no fallback if the hub was down.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _jobPollCts, null, ownCts), ownCts))
            {
                ownCts.Dispose();
            }
        }
    }

    /// <summary>Applies one progress update pushed from the hub to the row it belongs to.</summary>
    private async Task ApplyProgressAsync(AudioProcessingProgress progress)
    {
        UploadPairItem row;
        lock (_jobRowsLock)
        {
            if (!_jobRows.TryGetValue(progress.JobId, out row))
            {
                return;
            }
        }

        // The server already drops out-of-order updates, but a second guard here is cheap and this
        // is the surface the creator actually looks at.
        if (progress.Step < row.Step)
        {
            return;
        }

        row.Step = progress.Step;
        row.Progress = (int)progress.OverallPercent;

        switch (progress.Step)
        {
            case AudioProcessingStep.Completed:
                row.Status = UploadStatus.Completed;
                row.StatusMessage = string.IsNullOrWhiteSpace(progress.Detail)
                    ? "Published"
                    : $"Published '{progress.Detail}'";
                row.ErrorMessage = null;
                break;

            case AudioProcessingStep.Failed:
                row.Status = UploadStatus.Failed;
                row.StatusMessage = "Processing failed";
                row.ErrorMessage = progress.Detail;
                break;

            default:
                row.Status = UploadStatus.Processing;
                row.StatusMessage = DescribeStep(progress.Step);
                break;
        }

        await InvokeAsync(StateHasChanged);
        await MaybeNotifyBatchCompletedAsync();
    }

    /// <summary>
    /// Sends the batch upload notification once the whole batch has finished processing.
    ///
    /// <para>
    /// This used to fire at the end of the upload loop, which was the right moment when the request
    /// thread did the transcoding and wrote the songs itself. It is not any more: staging only hands
    /// the work to the queue, so at that point no <c>SongMetadata</c> row exists for the notification
    /// to describe. The batch is finished when every tracked row is terminal, which arrives from the
    /// hub or the fallback poller - hence the guard, since both can deliver the same last transition.
    /// </para>
    /// </summary>
    private async Task MaybeNotifyBatchCompletedAsync()
    {
        List<string> publishedPaths;
        lock (_jobRowsLock)
        {
            if (_batchNotificationSent || _jobRows.Count == 0)
            {
                return;
            }

            if (_jobRows.Values.Any(row => !AudioProcessingProgressCalculator.IsTerminal(row.Step)))
            {
                return;
            }

            // Failed songs are excluded rather than reported as uploads: nothing was published, and
            // the creator has already been told about the failure on the row itself.
            publishedPaths = _jobRows.Values
                .Where(row => row.Step == AudioProcessingStep.Completed && row.MediaGuid != Guid.Empty)
                .Select(row => SongMediaPaths.Playback(row.MediaGuid))
                .ToList();

            _batchNotificationSent = true;
        }

        if (publishedPaths.Count == 0
            || string.IsNullOrEmpty(_currentUserEmail)
            || !_currentCreatorId.HasValue)
        {
            return;
        }

        try
        {
            // Assembly writes Mp3BlobPath as exactly SongMediaPaths.Playback(mediaGuid), so deriving
            // the path from the GUID here matches the same rows the old tracked-path list did.
            await AdminNotificationService.NotifyUploadBatchCompletedAsync(
                _currentUserEmail,
                _currentCreatorId.Value,
                publishedPaths);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send batch upload notification");
        }
    }

    /// <summary>Creator-facing wording for each pipeline stage.</summary>
    private static string DescribeStep(AudioProcessingStep step) => step switch
    {
        AudioProcessingStep.Staging => "Uploading...",
        AudioProcessingStep.Queued => "Waiting to be processed...",
        AudioProcessingStep.Downloading => "Preparing your audio...",
        AudioProcessingStep.Analyzing => "Checking that your audio plays...",
        AudioProcessingStep.Transcoding => "Converting to MP3...",
        AudioProcessingStep.Verifying => "Verifying the converted audio...",
        AudioProcessingStep.Uploading => "Saving the converted audio...",
        AudioProcessingStep.RenderingArtwork => "Preparing artwork sizes...",
        AudioProcessingStep.Copying => "Storing your song...",
        AudioProcessingStep.SavingMetadata => "Adding it to your library...",
        AudioProcessingStep.GeneratingArtwork => "Preparing artwork...",
        AudioProcessingStep.Completed => "Published",
        AudioProcessingStep.Failed => "Processing failed",
        _ => "Working..."
    };

    private async Task UploadAudioOnlyAsync(PendingUpload pending)
    {
        const int bufferSize = 81920;
        var uploadItem = pending.Item;
        var audioTempPath = pending.AudioTempPath;

        try
        {
            // Validate CreatorId before uploading - songs without a creator cannot be tracked or paid
            if (_currentCreatorId == null)
            {
                Logger.LogError("UploadFiles: CreatorId is null for user {Email}. Cannot upload {FileName} without a creator association.", _currentUserEmail, uploadItem.AudioFileName);
                await SendCreatorIdFailureEmailAsync(uploadItem.AudioFileName);
                uploadItem.Status = UploadStatus.Failed;
                uploadItem.Progress = 0;
                uploadItem.StatusMessage = "Upload failed";
                uploadItem.ErrorMessage = UploadFailedUserMessage;
                await InvokeAsync(StateHasChanged);
                return;
            }

            uploadItem.Status = UploadStatus.Uploading;
            uploadItem.StatusMessage = "Uploading...";
            // The staging band's start, not a made-up 25. StagingProgress reports real bytes-sent
            // percentages inside that band moments later, so anything higher here shows as a jump
            // forward followed by a slide back.
            uploadItem.Progress = (int)AudioProcessingProgressCalculator.ToOverallPercent(
                AudioProcessingStep.Staging);
            await InvokeAsync(StateHasChanged);

            await using var audioFileStream = new FileStream(audioTempPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            var job = await SongUploadJobService.CreateAsync(
                new SongUploadJobRequest
                {
                    AudioStream = audioFileStream,
                    AudioFileName = uploadItem.AudioFileName,
                    SongTitle = uploadItem.SongTitle,
                    Metadata = uploadItem.ToPublishMetadata(),
                    CreatorId = _currentCreatorId.Value,
                    StagingProgress = BuildStagingProgress(uploadItem)
                },
                _uploadCts.Token);

            // Staged and queued, not published. The song does not exist yet - the Function
            // transcodes it and the API assembles it, and this row keeps moving from the
            // upload-progress hub until it does.
            TrackQueuedJob(uploadItem, job);
        }
        catch (InvalidDataException ex)
        {
            Logger.LogWarning(ex, "Upload validation rejected audio file {AudioFileName}", uploadItem.AudioFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Invalid file";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogError(ex, "Upload validation failed for audio file {AudioFileName}", uploadItem.AudioFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Validation failed";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Upload failed for audio file {AudioFileName}", uploadItem.AudioFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Upload failed";
            uploadItem.ErrorMessage = FileSizeHelper.FormatFileSizeExceptionMessage(ex.Message);
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task UploadFilePairAsync(PendingUpload pending)
    {
        const int bufferSize = 81920;
        var uploadItem = pending.Item;
        var audioTempPath = pending.AudioTempPath;

        // Resolved now, not when the batch was built: the review step may have moved this song's
        // artwork, or given it artwork it did not originally match.
        var coverArtTempPath = ResolveCoverArtTempPath(uploadItem);

        try
        {
            // Validate CreatorId before uploading - songs without a creator cannot be tracked or paid
            if (_currentCreatorId == null)
            {
                Logger.LogError("UploadFiles: CreatorId is null for user {Email}. Cannot upload audio '{AudioFile}' + cover art '{CoverArtFile}' without a creator association.", _currentUserEmail, uploadItem.AudioFileName, uploadItem.CoverArtFileName);
                await SendCreatorIdFailureEmailAsync($"{uploadItem.AudioFileName}, {uploadItem.CoverArtFileName}");
                uploadItem.Status = UploadStatus.Failed;
                uploadItem.Progress = 0;
                uploadItem.StatusMessage = "Upload failed";
                uploadItem.ErrorMessage = UploadFailedUserMessage;
                await InvokeAsync(StateHasChanged);
                return;
            }

            uploadItem.Status = UploadStatus.Uploading;
            uploadItem.StatusMessage = "Uploading...";
            // The staging band's start, not a made-up 25. StagingProgress reports real bytes-sent
            // percentages inside that band moments later, so anything higher here shows as a jump
            // forward followed by a slide back.
            uploadItem.Progress = (int)AudioProcessingProgressCalculator.ToOverallPercent(
                AudioProcessingStep.Staging);
            await InvokeAsync(StateHasChanged);

            await using var audioFileStream = new FileStream(audioTempPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            await using var coverArtFileStream = new FileStream(coverArtTempPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            var job = await SongUploadJobService.CreateAsync(
                new SongUploadJobRequest
                {
                    AudioStream = audioFileStream,
                    AudioFileName = uploadItem.AudioFileName,
                    CoverArtStream = coverArtFileStream,
                    CoverArtFileName = uploadItem.CoverArtFileName,
                    SongTitle = uploadItem.SongTitle,
                    Metadata = uploadItem.ToPublishMetadata(),
                    CreatorId = _currentCreatorId.Value,
                    StagingProgress = BuildStagingProgress(uploadItem)
                },
                _uploadCts.Token);

            TrackQueuedJob(uploadItem, job);
        }
        catch (InvalidDataException ex)
        {
            Logger.LogWarning(
                ex,
                "Upload validation rejected audio file {AudioFileName} with cover art {CoverArtFileName}",
                uploadItem.AudioFileName,
                uploadItem.CoverArtFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Invalid file";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogError(
                ex,
                "Upload validation failed for audio file {AudioFileName} with cover art {CoverArtFileName}",
                uploadItem.AudioFileName,
                uploadItem.CoverArtFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Validation failed";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Upload failed for audio file {AudioFileName} with cover art {CoverArtFileName}",
                uploadItem.AudioFileName,
                uploadItem.CoverArtFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Upload failed";
            uploadItem.ErrorMessage = FileSizeHelper.FormatFileSizeExceptionMessage(ex.Message);
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Stages one song's audio straight from the browser into Azure, then creates its job.
    ///
    /// <para>
    /// The web server is not in the transfer at all. It mints one write token scoped to a single
    /// blob, hands the browser a URL, and waits; the 57 MB never touches its disk, its memory or its
    /// circuit. What it does afterwards is what it could not do before the bytes existed in storage:
    /// measure the blob, sniff 64 bytes of it, and only then write a row.
    /// </para>
    ///
    /// <para>
    /// The cover art is not uploaded here. It went up during the image phase, before the pairing was
    /// known, and <c>CreateFromStagedAsync</c> copies it within the storage account.
    /// </para>
    /// </summary>
    private async Task UploadFromBrowserAsync(PendingUpload pending)
    {
        var uploadItem = pending.Item;

        // Minted here rather than by the service, because the browser has to be given somewhere to
        // write before anything knows whether the write will succeed. That is the one real
        // difference between the two paths: the GUID now precedes the row instead of following it.
        var mediaGuid = Guid.NewGuid();
        var queued = false;

        try
        {
            // Validate CreatorId before uploading - songs without a creator cannot be tracked or paid
            if (_currentCreatorId == null)
            {
                Logger.LogError("UploadFiles: CreatorId is null for user {Email}. Cannot upload {FileName} without a creator association.", _currentUserEmail, uploadItem.AudioFileName);
                await SendCreatorIdFailureEmailAsync(uploadItem.AudioFileName);
                uploadItem.Status = UploadStatus.Failed;
                uploadItem.Progress = 0;
                uploadItem.StatusMessage = "Upload failed";
                uploadItem.ErrorMessage = UploadFailedUserMessage;
                await InvokeAsync(StateHasChanged);
                return;
            }

            uploadItem.Status = UploadStatus.Uploading;
            uploadItem.StatusMessage = "Uploading...";
            // The staging band's start, not a made-up 25. The browser reports real bytes-sent
            // percentages inside that band moments later, so anything higher here shows as a jump
            // forward followed by a slide back.
            uploadItem.Progress = (int)AudioProcessingProgressCalculator.ToOverallPercent(
                AudioProcessingStep.Staging);
            await InvokeAsync(StateHasChanged);

            var audioExtension = Path.GetExtension(uploadItem.AudioFileName);
            var target = await UploadStagingSasService.CreateAudioTargetAsync(
                mediaGuid, audioExtension, _uploadCts.Token);

            // Recorded so a token that expires part-way through a 150 MB transfer can be re-minted
            // from the same inputs rather than costing the creator the whole upload.
            lock (_uploadPhasesLock)
            {
                _mintedUploadSlots[pending.BrowserAudioIndex.Value] = new MintedUploadSlot(
                    IsCoverArt: false, MediaGuid: mediaGuid, BatchId: Guid.Empty,
                    ImageIndex: 0, Extension: audioExtension);
            }

            // One phase per song, not one per batch. The window that decides how many run at once is
            // the semaphore in ProcessUploadsInChunksAsync, so a song reaching CreateFromStagedAsync
            // does so the moment its own bytes land rather than waiting for its slowest sibling -
            // the same pipelining the server path gets from that loop.
            // Resolved now, not when the batch was built: the review step may have moved this song's
            // artwork, or given it artwork it did not originally match.
            var coverArtStagedPath = ResolveCoverArtStagedPath(uploadItem);

            var browserIndex = pending.BrowserAudioIndex.Value;
            var results = await UploadDirectAsync(
                $"audio-{mediaGuid:N}",
                [new
                {
                    index = browserIndex,
                    target = new { sasUri = target.SasUri.ToString(), contentType = target.ContentType }
                }],
                _uploadCts.Token,
                (indexes, percents) =>
                {
                    if (percents.Length == 0)
                    {
                        return;
                    }

                    // One file per phase, so the last entry in the flush is this song's position.
                    // Mapped onto the staging band exactly as BuildStagingProgress does on the server
                    // path, so the bar means the same thing whichever route the bytes took.
                    uploadItem.Progress = (int)AudioProcessingProgressCalculator.ToOverallPercent(
                        AudioProcessingStep.Staging,
                        Math.Clamp(percents[^1], 0, 100));
                    _ = InvokeAsync(StateHasChanged);
                });

            var failure = results.FirstOrDefault(result => !result.Ok);
            if (failure is not null)
            {
                // Surfaced rather than swallowed, for the same reason as the image phase: a 403 means
                // the token expired and a null status with no code almost always means storage CORS
                // is not allowing this site, and neither appears in any log this server owns.
                throw new InvalidOperationException(
                    $"{uploadItem.AudioFileName} could not be uploaded. "
                    + $"{failure.ErrorCode} {failure.ErrorMessage}");
            }

            var job = await SongUploadJobService.CreateFromStagedAsync(
                new StagedSongUploadRequest
                {
                    MediaGuid = mediaGuid,
                    AudioFileName = uploadItem.AudioFileName,
                    SongTitle = uploadItem.SongTitle,
                    Metadata = uploadItem.ToPublishMetadata(),
                    CreatorId = _currentCreatorId.Value,
                    CoverArtFileName = coverArtStagedPath is null ? null : uploadItem.CoverArtFileName,
                    CoverArtStagedPath = coverArtStagedPath
                },
                _uploadCts.Token);

            queued = true;
            TrackQueuedJob(uploadItem, job);
        }
        catch (InvalidDataException ex)
        {
            // The size cap and the header sniff both land here, and both now fire *after* the upload
            // rather than before it. The creator has already spent the bandwidth - all this can do is
            // say which file and why, and make sure the bytes do not sit in staging forever.
            Logger.LogWarning(ex, "Upload validation rejected staged audio file {AudioFileName}", uploadItem.AudioFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Invalid file";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogError(ex, "Direct upload failed for audio file {AudioFileName}", uploadItem.AudioFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Upload failed";
            uploadItem.ErrorMessage = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Navigating away or closing the tab. Not a failure worth an error row - the creator
            // asked for it - but the partial blob still has to go.
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Cancelled";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Direct upload failed for audio file {AudioFileName}", uploadItem.AudioFileName);
            uploadItem.Status = UploadStatus.Failed;
            uploadItem.Progress = 0;
            uploadItem.StatusMessage = "Upload failed";
            uploadItem.ErrorMessage = FileSizeHelper.FormatFileSizeExceptionMessage(ex.Message);
        }
        finally
        {
            // Anything not queued leaves a folder nothing will ever come back for: no row references
            // it, so the reconciler cannot see it and the Function was never told about it. On the
            // server path this could not happen - the upload and the row were one operation.
            //
            // Note what this cannot reach: a block upload that was abandoned before its block list
            // was committed leaves *uncommitted* blocks, which do not appear in a blob listing at
            // all. Azure garbage-collects those on its own after a week.
            if (!queued)
            {
                try
                {
                    await SongUploadJobService.DeleteStagedBlobsAsync(mediaGuid, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not clean up staged blobs for abandoned job {MediaGuid}", mediaGuid);
                }
            }

            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Imports the upload module, if direct-to-storage is switched on for this environment.
    ///
    /// <para>
    /// Best effort by design. A failed import leaves <see cref="_uploadModule"/> null and every
    /// caller falls back to uploading through the circuit, which is exactly what this page did
    /// before - so a browser that cannot load the module is slower, not broken.
    /// </para>
    /// </summary>
    /// <summary>
    /// Tells the browser to stop uploading, and releases the module and callback reference.
    ///
    /// <para>
    /// Every failure here is swallowed: this runs on the way out, usually because the circuit is
    /// already going away, and the JS call is expected to fail in exactly that case.
    /// </para>
    /// </summary>
    private async Task AbortDirectUploadsAsync()
    {
        try
        {
            if (_uploadModule is not null)
            {
                await _uploadModule.InvokeVoidAsync("abortAll");
                await _uploadModule.DisposeAsync();
            }
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not abort in-flight browser uploads.");
        }
        finally
        {
            _uploadModule = null;

            lock (_uploadPhasesLock)
            {
                foreach (var waiter in _uploadPhases.Values)
                {
                    waiter.TrySetCanceled();
                }

                _uploadPhases.Clear();
            }

            _uploadCallbackRef?.Dispose();
            _uploadCallbackRef = null;
        }
    }

    private async Task InitialiseDirectUploadAsync()
    {
        try
        {
            if (!await AppSettingsService.IsDirectToStorageUploadEnabledAsync())
            {
                return;
            }

            if (!UploadStagingSasService.IsAvailable)
            {
                Logger.LogWarning(
                    "Direct-to-storage upload is enabled but staging is not configured; "
                    + "falling back to uploading through the web server.");
                return;
            }

            _uploadCallbackRef = DotNetObjectReference.Create(this);
            _uploadModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/direct-upload.js");
        }
        catch (JSDisconnectedException) { }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load the direct-upload module; uploads will go through the server.");
            _uploadModule = null;
        }
    }

    /// <summary>True when the browser can upload straight to Azure for this batch.</summary>
    private bool DirectUploadAvailable => _uploadModule is not null && _uploadCallbackRef is not null;

    /// <summary>
    /// Hands a set of files to the browser to upload, and waits for it to finish.
    ///
    /// <para>
    /// The JS call itself returns immediately. It has to: Blazor's JSInteropDefaultCallTimeout is one
    /// minute, so awaiting a 57 MB upload from C# would abandon a perfectly healthy transfer. The
    /// long wait is on a <see cref="TaskCompletionSource"/> that the browser resolves through
    /// <see cref="PhaseCompleted"/>.
    /// </para>
    /// </summary>
    /// <param name="onProgress">
    /// Where this phase's progress goes, as one batched flush of (browser file indexes, percents).
    /// Supplied per phase because several run at once and they do not share a bar: the image phase
    /// drives the single batch figure, each audio phase drives one song's row.
    /// </param>
    private async Task<IReadOnlyList<DirectUploadResult>> UploadDirectAsync(
        string phaseId,
        IReadOnlyList<object> items,
        CancellationToken cancellationToken,
        Action<int[], int[]> onProgress)
    {
        var waiter = new TaskCompletionSource<IReadOnlyList<DirectUploadResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_uploadPhasesLock)
        {
            _uploadPhases[phaseId] = waiter;
            _uploadProgressRouters[phaseId] = onProgress;
        }

        try
        {
            await _uploadModule!.InvokeVoidAsync(
                "uploadFiles", cancellationToken, phaseId, items, BrowserUploadConcurrency, _uploadCallbackRef);

            using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            return await waiter.Task;
        }
        finally
        {
            lock (_uploadPhasesLock)
            {
                _uploadPhases.Remove(phaseId);
                _uploadProgressRouters.Remove(phaseId);
            }
        }
    }

    /// <summary>Batched upload progress from the browser. Fire-and-forget on both sides.</summary>
    [JSInvokable]
    public void ReportUploadProgress(string phaseId, int[] indexes, int[] percents)
    {
        if (indexes is null || percents is null)
        {
            return;
        }

        var count = Math.Min(indexes.Length, percents.Length);
        if (count == 0)
        {
            return;
        }

        // Routed by phase rather than applied to the batch bar unconditionally. Several phases are in
        // flight at once during the audio stage, and every one of them reports through this method -
        // without the lookup, each song's percentages would overwrite a batch bar belonging to an
        // image phase that finished minutes earlier.
        //
        // The whole flush goes to the router in one piece, not file by file, so a phase still renders
        // once per batched call. Splitting it here would undo the coalescing the module does.
        Action<int[], int[]> route;
        lock (_uploadPhasesLock)
        {
            if (!_uploadProgressRouters.TryGetValue(phaseId, out route))
            {
                return;
            }
        }

        route(indexes, percents);
    }

    /// <summary>
    /// Drives the single batch bar from the average across every image in the phase. Used only by the
    /// image phase, where the creator is watching one figure rather than a row per file.
    ///
    /// <para>
    /// Averaged over the running totals rather than over one flush's contents. A flush carries only
    /// the files that moved since the last one, so averaging it directly makes the bar lurch: three
    /// finished images and one at 4% reports 4%.
    /// </para>
    /// </summary>
    private void ReportImagePhaseProgress(Dictionary<int, int> percentByIndex, int[] indexes, int[] percents)
    {
        _initialUploadBatchProgress = (int)CoverArtMatchProgressCalculator.ToStagingImagesPercent(
            ApplyImagePhaseFlush(percentByIndex, indexes, percents));

        // Not awaited, and synchronous by design - this is the high-frequency callback, and making
        // the browser wait a circuit round trip per tick is what the throttling in the module exists
        // to avoid in the first place.
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Folds one flush into the phase's running totals and returns the average across every file in
    /// it, 0-100.
    ///
    /// <para>
    /// Separated out and made static because the interesting behaviour is entirely in which files it
    /// averages over: a flush carries only what moved since the last one, so averaging a flush
    /// directly makes the bar lurch backwards - three finished images and one at 4% reports 4%.
    /// </para>
    /// </summary>
    internal static double ApplyImagePhaseFlush(
        Dictionary<int, int> percentByIndex,
        int[] indexes,
        int[] percents)
    {
        var count = Math.Min(indexes?.Length ?? 0, percents?.Length ?? 0);
        for (var i = 0; i < count; i++)
        {
            percentByIndex[indexes[i]] = Math.Clamp(percents[i], 0, 100);
        }

        if (percentByIndex.Count == 0)
        {
            return 0d;
        }

        var total = 0d;
        foreach (var percent in percentByIndex.Values)
        {
            total += percent;
        }

        return total / percentByIndex.Count;
    }

    /// <summary>
    /// Mints a replacement write token for one in-flight file.
    ///
    /// <para>
    /// Called by the browser on a 403, which after a transfer has been running for a while means the
    /// token expired rather than that anything is wrong. The audio cap is 150 MB and a token lives 30
    /// minutes, so on a slow uplink this is reachable - and without it the file is discarded at the
    /// moment it fails, after the creator has already spent the upload.
    /// </para>
    ///
    /// <para>
    /// Keyed by slot, never by path, exactly as the original mint is. The browser can say "the file
    /// at index N" and nothing else; the path is re-derived here from what this page minted, so a
    /// tampered page cannot ask for a token to somewhere of its choosing.
    /// </para>
    /// </summary>
    [JSInvokable]
    public async Task<RenewedUploadTarget> RenewUploadTarget(int index)
    {
        MintedUploadSlot slot;
        lock (_uploadPhasesLock)
        {
            if (!_mintedUploadSlots.TryGetValue(index, out slot))
            {
                return null;
            }
        }

        try
        {
            var target = slot.IsCoverArt
                ? await UploadStagingSasService.CreateMatchImageTargetAsync(
                    slot.BatchId, slot.ImageIndex, slot.Extension, _uploadCts.Token)
                : await UploadStagingSasService.CreateAudioTargetAsync(
                    slot.MediaGuid, slot.Extension, _uploadCts.Token);

            Logger.LogInformation(
                "Renewed the upload token for slot {Index}; the first one expired mid-transfer.", index);

            return new RenewedUploadTarget(target.SasUri.ToString(), target.ContentType);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not renew the upload token for slot {Index}.", index);
            return null;
        }
    }

    /// <summary>What the browser needs to resume: a new URL for the same blob.</summary>
    public sealed record RenewedUploadTarget(string SasUri, string ContentType);

    /// <summary>
    /// Everything needed to re-derive one file's destination, held server-side.
    ///
    /// <para>
    /// The point of keeping this rather than the minted URL is that a renewal must reproduce the
    /// path from inputs this page chose, not echo back something the browser handed it.
    /// </para>
    /// </summary>
    private readonly record struct MintedUploadSlot(
        bool IsCoverArt,
        Guid MediaGuid,
        Guid BatchId,
        int ImageIndex,
        string Extension);

    // Browser FileList position -> what was minted for it. Cleared per phase.
    private readonly Dictionary<int, MintedUploadSlot> _mintedUploadSlots = new();


    /// <summary>
    /// Releases the batch waiting on this phase.
    ///
    /// <para>
    /// <c>TrySetResult</c>, not <c>SetResult</c>: a cancelled phase may already have been abandoned
    /// by its waiter, and a late completion must be a no-op rather than an exception on the circuit.
    /// </para>
    /// </summary>
    [JSInvokable]
    public Task PhaseCompleted(string phaseId, DirectUploadResult[] results)
    {
        lock (_uploadPhasesLock)
        {
            if (_uploadPhases.TryGetValue(phaseId, out var waiter))
            {
                waiter.TrySetResult(results ?? []);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stages the dropped cover art and asks the Azure Function to pair it with the audio.
    ///
    /// <para>
    /// This used to run here, on the circuit: a vision call per image, serially, holding up to four
    /// image byte arrays in memory on a shared-hosting web server, behind a static message with no
    /// progress and no deadline. Chunking it in fours also capped how well it could match, because
    /// the pairing call only ever saw four images at a time while its own one-image-per-song rules
    /// only make sense across the whole batch.
    /// </para>
    ///
    /// <para>
    /// <b>Never throws, and never hangs.</b> A missing queue, a failed staging upload, an expired
    /// deadline or a dead Function all end the same way — matching on filenames here — which is what
    /// this page did before any of it existed. The batch always proceeds.
    /// </para>
    /// </summary>
    /// <summary>
    /// Pairs the only song with the only image, without asking anything to match them.
    ///
    /// <para>
    /// The image still has to <em>reach storage</em> on the direct path, which is the whole subtlety
    /// here: those staged blobs are the cover art itself, not merely evidence for a matcher, so
    /// skipping the round trip must not also skip the upload. On the server path the artwork lives in
    /// a temp file and the staged copy existed only for OCR, so nothing is uploaded at all.
    /// </para>
    ///
    /// <para>
    /// Never throws. A staging failure leaves the image unreachable, which the caller already handles
    /// by offering it in the pool rather than losing the song.
    /// </para>
    /// </summary>
    private async Task<FileMatchingResult> PairTheOnlyCandidatesAsync(string audioFileName, string imageFileName)
    {
        if (DirectUploadAvailable && _currentCreatorId is { } creatorId)
        {
            var batchId = Guid.NewGuid();

            try
            {
                _initialUploadStatusMessage = "Preparing cover art...";
                await InvokeAsync(StateHasChanged);

                await StageImagesFromBrowserAsync(
                    batchId, creatorId, [audioFileName], [imageFileName], requestMatching: false);

                _pendingImageBatchId = batchId;
            }
            catch (Exception ex)
            {
                // Same outcome as a failed match: the song still uploads, and the image is offered
                // in the pool instead of silently deciding the creator gets no artwork.
                Logger.LogWarning(
                    ex, "UploadFiles: could not stage the only cover image for batch {BatchId}.", batchId);

                if (_stagedImagePaths.Count > 0)
                {
                    _pendingImageBatchId = batchId;
                }
                else
                {
                    _ = CoverArtMatchService.DeleteBatchAsync(batchId, CancellationToken.None);
                }
            }
        }

        // Deferred to the matcher rather than hand-built here, so the one-and-one rule lives in one
        // place. This method's job is the routing decision - not asking the Function - and the
        // pairing itself is the matcher's.
        return await FileMatchingService.MatchFilesAsync([audioFileName], [imageFileName]);
    }

    private async Task<FileMatchingResult> MatchCoverArtAsync(
        List<string> audioFileNames,
        List<string> imageFileNames,
        Dictionary<string, string> coverArtTempPaths)
    {
        // One song and one image: they are a pair, and there is no evidence that could make that
        // more or less true. Asking the model would spend an OCR pass and about twenty-five seconds
        // to answer a question with one possible answer - and it could answer it *wrongly*, which is
        // how a photo named "david.JPG" got refused a song it plainly belonged to once the prompt was
        // told to stop guessing. Filenames are not consulted either: a creator who uploads exactly
        // two files has already said what they mean.
        //
        // Ahead of the availability check on purpose, so this holds whether or not the Function is
        // reachable. The creator still gets the review step if they asked for it - this decides the
        // pairing, not whether they get to change it.
        if (audioFileNames.Count == 1 && imageFileNames.Count == 1)
        {
            return await PairTheOnlyCandidatesAsync(audioFileNames[0], imageFileNames[0]);
        }

        // Nothing to pair, or nowhere to pair it. Both take the local path immediately rather than
        // paying a round trip to learn the same answer.
        if (imageFileNames.Count == 0
            || audioFileNames.Count == 0
            || _currentCreatorId is not { } creatorId
            || !CoverArtMatchService.IsAvailable)
        {
            return await FileMatchingService.MatchFilesAsync(audioFileNames, imageFileNames);
        }

        var batchId = Guid.NewGuid();
        var waiter = new TaskCompletionSource<CoverArtMatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_matchWaitersLock)
        {
            _matchWaiters[batchId] = waiter;
        }

        try
        {
            var candidates = imageFileNames
                .Where(coverArtTempPaths.ContainsKey)
                .Select(name => new CoverArtMatchCandidateSource(name, coverArtTempPaths[name]))
                .ToList();

            if (candidates.Count == 0)
            {
                return await FileMatchingService.MatchFilesAsync(audioFileNames, imageFileNames);
            }

            if (DirectUploadAvailable)
            {
                await StageImagesFromBrowserAsync(batchId, creatorId, audioFileNames, imageFileNames);
            }
            else
            {
                await CoverArtMatchService.StageAndEnqueueAsync(
                    batchId,
                    creatorId,
                    audioFileNames,
                    candidates,
                    BuildMatchStagingProgress(),
                    _uploadCts.Token);
            }

            _initialUploadStatusMessage = "Matching cover art...";
            _initialUploadBatchProgress = (int)CoverArtMatchProgressCalculator.ToMatchingPercent(
                CoverArtMatchProgressCalculator.ToOverallPercent(CoverArtMatchStep.Queued));
            await InvokeAsync(StateHasChanged);

            var result = await AwaitMatchResultAsync(waiter.Task);

            // On the server path the pairing is all these images were for - the cover art itself is
            // still on this machine as a temp file - so the folder goes now. On the direct path those
            // blobs ARE the cover art, and deleting them here would publish every song in the batch
            // with none: the copy into each song's folder does not happen until it is created, which
            // is after the review pause and after its audio has uploaded.
            if (DirectUploadAvailable)
            {
                _pendingImageBatchId = batchId;
            }
            else
            {
                // Fire and forget: the pairing is already in hand, and the lifecycle rule on the
                // staging container is the real backstop. Blocking the creator on a cleanup delete
                // would be paying latency for something that cannot fail visibly.
                _ = CoverArtMatchService.DeleteBatchAsync(batchId, CancellationToken.None);
            }

            if (result is null)
            {
                Logger.LogWarning(
                    "UploadFiles: no cover-art pairing arrived for batch {BatchId} within {Timeout}; "
                    + "matching on filenames instead.",
                    batchId,
                    MediaProcessingTimeouts.CoverArtMatch);
                return await FileMatchingService.MatchFilesAsync(audioFileNames, imageFileNames);
            }

            if (result.UsedFallback)
            {
                Logger.LogInformation(
                    "UploadFiles: batch {BatchId} was paired on filenames alone. {Diagnostic}",
                    batchId,
                    result.Diagnostic);
            }

            return ToFileMatchingResult(result, audioFileNames, imageFileNames);
        }
        catch (Exception ex)
        {
            // Includes cancellation of the staging upload. Falling back is right even then: the
            // batch may still be uploaded, and this only decides which image goes with which song.
            Logger.LogWarning(
                ex, "UploadFiles: cover-art matching failed for batch {BatchId}; matching on filenames.", batchId);

            // Same rule as the success path, asked of the images rather than of the flag: anything
            // recorded here uploaded successfully and is about to be somebody's cover art, whatever
            // went wrong afterwards. Nothing recorded means nothing to keep.
            if (_stagedImagePaths.Count > 0)
            {
                _pendingImageBatchId = batchId;
            }
            else
            {
                _ = CoverArtMatchService.DeleteBatchAsync(batchId, CancellationToken.None);
            }

            return await FileMatchingService.MatchFilesAsync(audioFileNames, imageFileNames);
        }
        finally
        {
            lock (_matchWaitersLock)
            {
                _matchWaiters.Remove(batchId);
            }
        }
    }

    /// <summary>
    /// Uploads the cover images from the browser straight into the batch folder, then enqueues the
    /// pairing request.
    ///
    /// <para>
    /// The destination is never chosen here or in the browser: the server mints one write token per
    /// image, each scoped to a single blob whose path it derived itself. The browser is handed URLs,
    /// not paths.
    /// </para>
    ///
    /// <para>
    /// Throws on failure, deliberately. The caller already treats any exception as "match on
    /// filenames instead", so a storage problem degrades the pairing rather than failing the batch —
    /// and the creator still gets their songs.
    /// </para>
    /// </summary>
    /// <param name="requestMatching">
    /// False when the pairing is already known and only the upload is wanted - a single song with a
    /// single image. The blobs are still needed, because on this path they <em>are</em> the cover
    /// art; only the queue message asking something to match them is skipped.
    /// </param>
    private async Task StageImagesFromBrowserAsync(
        Guid batchId,
        int creatorId,
        List<string> audioFileNames,
        List<string> imageFileNames,
        bool requestMatching = true)
    {
        var targets = new List<CoverArtMatchCandidate>(imageFileNames.Count);
        var items = new List<object>(imageFileNames.Count);

        // Seeded at zero for every image up front, so the average the bar shows is over the whole
        // phase from the first flush rather than over however many happen to have started. Keyed by
        // the browser's FileList position, which is what the progress callback echoes back - not by
        // the candidate index, which counts images only and would silently mismatch.
        var imageProgress = new Dictionary<int, int>(imageFileNames.Count);

        for (var index = 0; index < imageFileNames.Count; index++)
        {
            var fileName = imageFileNames[index];
            if (!_browserFileIndexes.TryGetValue(fileName, out var browserIndex))
            {
                continue;
            }

            imageProgress[browserIndex] = 0;

            var extension = Path.GetExtension(fileName);
            var target = await UploadStagingSasService.CreateMatchImageTargetAsync(
                batchId, index, extension, _uploadCts.Token);

            // Recorded so a token that expires mid-transfer can be re-minted from the same inputs.
            lock (_uploadPhasesLock)
            {
                _mintedUploadSlots[browserIndex] = new MintedUploadSlot(
                    IsCoverArt: true, MediaGuid: Guid.Empty, BatchId: batchId,
                    ImageIndex: index, Extension: extension);
            }

            targets.Add(new CoverArtMatchCandidate
            {
                Index = index,
                FileName = fileName,
                BlobPath = target.BlobPath,
                ContentType = target.ContentType
            });

            items.Add(new
            {
                index = browserIndex,
                target = new { sasUri = target.SasUri.ToString(), contentType = target.ContentType }
            });
        }

        if (items.Count == 0)
        {
            throw new InvalidOperationException("None of the selected cover art is still available in the browser.");
        }

        _initialUploadStatusMessage = "Preparing cover art...";
        await InvokeAsync(StateHasChanged);

        var results = await UploadDirectAsync(
            $"images-{batchId:N}",
            items,
            _uploadCts.Token,
            (indexes, percents) => ReportImagePhaseProgress(imageProgress, indexes, percents));

        var failed = results.Where(result => !result.Ok).ToList();
        if (failed.Count > 0)
        {
            // Surfaced rather than swallowed. A 403 here means the token expired; a null status with
            // no error code almost always means storage CORS is not allowing this site - and neither
            // appears in Serilog, Application Insights or Azure, because the request never reached
            // anything of ours.
            var first = failed[0];
            throw new InvalidOperationException(
                $"{failed.Count} cover image(s) could not be uploaded. "
                + $"First failure: {first.ErrorCode} {first.ErrorMessage}");
        }

        // Recorded only once every image is known to be in Azure. These paths are what each song's
        // cover art is later copied from, so an entry for a blob that does not exist would turn a
        // failed image upload into a failed song rather than a song without art.
        foreach (var target in targets)
        {
            _stagedImagePaths[target.FileName] = target.BlobPath;
        }

        if (requestMatching)
        {
            await CoverArtMatchService.EnqueueAsync(batchId, creatorId, audioFileNames, targets, _uploadCts.Token);
        }
    }

    /// <summary>
    /// Waits for the pairing, or gives up. Returns null on a deadline the caller turns into a local
    /// match — an expiry is a slower answer, never a failed upload.
    /// </summary>
    private static async Task<CoverArtMatchResult> AwaitMatchResultAsync(Task<CoverArtMatchResult> waiter)
    {
        var completed = await Task.WhenAny(
            waiter,
            Task.Delay(MediaProcessingTimeouts.CoverArtMatch));

        return completed == waiter ? await waiter : null;
    }

    /// <summary>
    /// Maps the Function's index-based pairing onto the filename-based shape the rest of this page
    /// already works in.
    ///
    /// <para>
    /// Indices are bounds-checked and claimed one-to-one on the way through. The Function does this
    /// too, but the values crossed a process boundary to get here and this page feeds them straight
    /// into which file gets uploaded as which song's art.
    /// </para>
    /// </summary>
    private static FileMatchingResult ToFileMatchingResult(
        CoverArtMatchResult result,
        List<string> audioFileNames,
        List<string> imageFileNames)
    {
        var mapped = new FileMatchingResult();
        var claimedAudio = new HashSet<int>();
        var claimedImages = new HashSet<int>();

        foreach (var pair in result.Pairs ?? [])
        {
            if (pair.AudioIndex < 0 || pair.AudioIndex >= audioFileNames.Count)
                continue;
            if (!claimedAudio.Add(pair.AudioIndex))
                continue;

            string imageFileName = null;
            if (pair.ImageIndex is { } imageIndex
                && imageIndex >= 0
                && imageIndex < imageFileNames.Count
                && claimedImages.Add(imageIndex))
            {
                imageFileName = imageFileNames[imageIndex];
            }

            mapped.Pairs.Add(new FilePair
            {
                AudioFileName = audioFileNames[pair.AudioIndex],
                ImageFileName = imageFileName,
                NormalizedName = string.IsNullOrWhiteSpace(pair.NormalizedName)
                    ? FileNameMatching.ToNormalizedName(audioFileNames[pair.AudioIndex])
                    : pair.NormalizedName
            });
        }

        // Any audio file the pairing left out still needs a row, or the creator would silently lose
        // a song they selected.
        for (var i = 0; i < audioFileNames.Count; i++)
        {
            if (claimedAudio.Contains(i))
                continue;

            mapped.Pairs.Add(new FilePair
            {
                AudioFileName = audioFileNames[i],
                ImageFileName = null,
                NormalizedName = FileNameMatching.ToNormalizedName(audioFileNames[i])
            });
        }

        for (var i = 0; i < imageFileNames.Count; i++)
        {
            if (!claimedImages.Contains(i))
                mapped.UnmatchedImageFiles.Add(imageFileNames[i]);
        }

        return mapped;
    }

    /// <summary>Feeds the image-staging upload into the receiving phase's middle band.</summary>
    private IProgress<double> BuildMatchStagingProgress()
        => new Progress<double>(percent =>
        {
            _initialUploadStatusMessage = "Preparing cover art...";
            _initialUploadBatchProgress = (int)CoverArtMatchProgressCalculator.ToStagingImagesPercent(percent);
            _ = InvokeAsync(StateHasChanged);
        });

    /// <summary>
    /// Renders live matching progress. Arrives over the same hub and creator group as song progress.
    /// </summary>
    private async Task ApplyMatchProgressAsync(CoverArtMatchProgress progress)
    {
        lock (_matchWaitersLock)
        {
            // Not ours - another tab of the same creator's account is matching its own batch.
            if (!_matchWaiters.ContainsKey(progress.BatchId))
                return;
        }

        _initialUploadStatusMessage = progress.Step switch
        {
            CoverArtMatchStep.ReadingText when progress.ImagesTotal > 0
                => $"Matching cover art — read {progress.ImagesProcessed} of {progress.ImagesTotal} images",
            CoverArtMatchStep.Pairing => "Matching cover art to your songs...",
            _ => "Matching cover art..."
        };

        _initialUploadBatchProgress =
            (int)CoverArtMatchProgressCalculator.ToMatchingPercent(progress.OverallPercent);

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Releases the batch waiting on this pairing.
    ///
    /// <para>
    /// <c>TrySetResult</c> rather than <c>SetResult</c>: the match callback throws on a non-2xx so
    /// its queue redelivers, and with no database row behind a batch there is no free idempotency,
    /// so a redelivery really can broadcast the same pairing twice. The second one must be a no-op
    /// rather than an <see cref="InvalidOperationException"/> on the circuit.
    /// </para>
    /// </summary>
    private Task ApplyMatchResultAsync(CoverArtMatchResult result)
    {
        lock (_matchWaitersLock)
        {
            if (_matchWaiters.TryGetValue(result.BatchId, out var waiter))
            {
                waiter.TrySetResult(result);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Dismisses the validation banner and the file lists shown beneath it.
    ///
    /// <para>
    /// <see cref="_unmatchedCoverArtFiles"/> is <em>not</em> among them any more. It stopped being a
    /// post-hoc report when re-pairing arrived: during the review step it is the live pool of images
    /// the creator can still place, and clearing it discards them for good. See
    /// <see cref="ClearUnusedCoverArtNotice"/> for the case where it really is just a notice.
    /// </para>
    /// </summary>
    protected void ClearValidationError()
    {
        _validationErrorMessage = string.Empty;
        _unmatchedMp3Files.Clear();
        _duplicateSongFiles.Clear();

        // Only once the batch is past the point of being re-paired. While the review step is open
        // these are assignable images, not leftovers.
        if (!_awaitingTitleConfirmation)
        {
            _unmatchedCoverArtFiles.Clear();
        }
    }

    /// <summary>Drops the "left unused" list once the creator has seen it. Never during review.</summary>
    protected void ClearUnusedCoverArtNotice()
    {
        if (!_awaitingTitleConfirmation)
        {
            _unmatchedCoverArtFiles.Clear();
        }
    }

    protected string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    protected string GetProgressBarClass(UploadStatus status)
    {
        return status switch
        {
            UploadStatus.Completed => "bg-success",
            UploadStatus.Failed => "bg-danger",
            UploadStatus.Processing => "bg-info progress-bar-striped progress-bar-animated",
            UploadStatus.Uploading => "bg-primary progress-bar-striped progress-bar-animated",
            _ => "bg-secondary"
        };
    }

    /// <summary>
    /// One row of the review/progress table.
    ///
    /// <para>
    /// Public rather than protected because it is the unit the cover-art re-pairing commands operate
    /// on, and those are worth testing directly - the invariant they maintain (every image assigned
    /// exactly once, or pooled) is not observable from the rendered markup.
    /// </para>
    /// </summary>
    public class UploadPairItem
    {
        /// <summary>
        /// The title to store. Seeded from the filename and editable in the review step before
        /// anything is uploaded.
        /// </summary>
        public string SongTitle { get; set; } = string.Empty;

        /// <summary>
        /// Why this title cannot be uploaded as-is, or null when it is fine. Set only for the
        /// rows the creator has to correct.
        /// </summary>
        public string TitleError { get; set; }

        /// <summary>
        /// Whether the creator has actually looked at this title.
        ///
        /// <para>
        /// A title is never empty - SongTitleHelper.FromFileName always produces something -
        /// so a required field would catch nothing. "track 03 final FINAL" passes every
        /// validation rule there is and publishes exactly as written. What catches it is
        /// having to confirm, and editing a title counts as confirming it.
        /// </para>
        /// </summary>
        public bool TitleConfirmed { get; set; }

        /// <summary>The filename the title was derived from, so a row can say where a bad one
        /// came from rather than just objecting to it.</summary>
        public string TitleSourceFileName { get; set; } = string.Empty;

        /// <summary>Required before a song can publish. Empty until the creator picks one.</summary>
        public string Genre { get; set; } = string.Empty;

        /// <summary>Why this row cannot upload as-is, or null. Separate from TitleError because
        /// they are fixed in different columns.</summary>
        public string GenreError { get; set; }

        /// <summary>Null is a first-class choice, not an empty state: most creators have no
        /// persona and their songs carry the display name instead.</summary>
        public int? PersonaId { get; set; }

        public bool IsAiGenerated { get; set; }
        public bool IsAiVocals { get; set; }
        public bool IsAiLyrics { get; set; }

        /// <summary>
        /// The creator has said none of it is AI.
        ///
        /// <para>
        /// Not persisted, and it does not need to be - what reaches the song is the three flags
        /// above, all false. It exists so the review step can tell "declared original" apart
        /// from "has not answered yet", which three unticked boxes cannot.
        /// </para>
        /// </summary>
        public bool AllOriginal { get; set; }

        /// <summary>Whether the disclosure has been answered at all, either way.</summary>
        public bool AiDeclared => AllOriginal || IsAiGenerated || IsAiVocals || IsAiLyrics;

        /// <summary>Why this row cannot upload as-is, or null.</summary>
        public string AiError { get; set; }

        /// <summary>Everything the creator set here, in the shape the job carries it.</summary>
        public SongPublishMetadata ToPublishMetadata() => new()
        {
            Genre = string.IsNullOrWhiteSpace(Genre) ? null : Genre.Trim(),
            PersonaId = PersonaId,
            IsAiGenerated = IsAiGenerated,
            IsAiVocals = IsAiVocals,
            IsAiLyrics = IsAiLyrics,
        };

        public string AudioFileName { get; set; } = string.Empty;
        public long AudioFileSize { get; set; }
        public string CoverArtFileName { get; set; } = string.Empty;
        public long CoverArtFileSize { get; set; }
        public bool HasCoverArt { get; set; }

        /// <summary>
        /// Why the image the creator just browsed for was not accepted, or null.
        ///
        /// <para>
        /// Unlike <see cref="TitleError"/>, <see cref="GenreError"/> and <see cref="AiError"/>, this
        /// never blocks the upload and <c>PendingBatchNeedsAttentionAsync</c> does not read it.
        /// Artwork is optional - a song publishes happily without any - so a rejected image is a
        /// failed attempt to add something, not a song that cannot be published. Blocking on it
        /// would leave a creator unable to submit a batch that was already valid before they
        /// picked the wrong file.
        /// </para>
        /// </summary>
        public string CoverArtError { get; set; }
        public UploadStatus Status { get; set; }
        public string StatusMessage { get; set; } = string.Empty;

        /// <summary>
        /// One bar for the whole lifecycle, 0-100. It starts filling as the browser's bytes reach
        /// the staging container, holds while the song waits for a Function instance, and keeps
        /// climbing through transcoding and then the API's assembly. It is never hidden and never
        /// runs backwards.
        /// </summary>
        public int Progress { get; set; }

        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>
        /// The job's media GUID once it has been staged, which is how progress pushed over SignalR
        /// finds the right row. Empty until then.
        /// </summary>
        public Guid MediaGuid { get; set; }

        /// <summary>Where the pipeline has got to, for the label beside the bar.</summary>
        public AudioProcessingStep Step { get; set; } = AudioProcessingStep.Staging;
    }

    /// <summary>Public for the same reason as <see cref="UploadPairItem"/>, which exposes it.</summary>
    public enum UploadStatus
    {
        Pending,
        Uploading,

        /// <summary>Staged and queued; the Azure Function has it now.</summary>
        Processing,
        Completed,
        Failed
    }

    /// <summary>
    /// Intercepts in-app navigation while files are still being sent.
    ///
    /// <para>
    /// What leaving costs has changed: a song that has been staged and queued is finished by the
    /// Function and the API whether or not this page is open, so only the files still being sent are
    /// lost. The wording says exactly that rather than the old promise to remove everything uploaded
    /// in the session, which would now be a lie in both directions - nothing is removed, and nothing
    /// queued needs to be.
    /// </para>
    /// </summary>
    /// <summary>
    /// Installs the anchor-click guard, so a link cannot carry a part-finished batch away silently.
    /// </summary>
    /// <remarks>
    /// <b>Necessary because <see cref="OnBeforeInternalNavigation"/> never runs for a link.</b> This
    /// app routes with a static SSR <c>&lt;Routes /&gt;</c> and per-page interactive islands, so an
    /// <c>&lt;a href&gt;</c> is enhanced navigation and the circuit's NavigationManager is told only
    /// afterwards. The symptom was exact and misleading: refreshing warned - that is
    /// <c>ConfirmExternalNavigation</c>, a beforeunload path that still works - while clicking a nav
    /// link discarded a reviewed batch without a word. See <c>wwwroot/js/leave-guard.js</c>.
    /// </remarks>
    private async Task ArmLeaveGuardAsync()
    {
        try
        {
            _leaveGuardRef = DotNetObjectReference.Create(this);
            _leaveGuardModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/leave-guard.js");
            await _leaveGuardModule.InvokeVoidAsync("arm", _leaveGuardRef);
        }
        catch (JSDisconnectedException) { }
        catch (Exception ex)
        {
            // A page that warns is better than a page that will not load, and the beforeunload half
            // of the lock is unaffected by this - a refresh still asks.
            Logger.LogWarning(ex, "Could not arm the leave guard on the upload page.");
        }
    }

    /// <summary>Whether there is anything on this page that leaving would throw away.</summary>
    private bool HasWorkToLose => _isUploading || _isProcessingFiles || _awaitingTitleConfirmation;

    /// <summary>Whether the "leave this page?" dialog is on screen.</summary>
    protected bool _showLeavePrompt;

    /// <summary>
    /// The creator's answer to that dialog, awaited by whichever path raised it.
    /// </summary>
    private TaskCompletionSource<bool> _leaveDecision;

    /// <summary>
    /// What leaving would actually cost, which is two different things.
    /// </summary>
    /// <remarks>
    /// The distinction is worth the branch. A batch waiting for titles has sent nothing, so leaving
    /// discards all of it. A batch mid-flight has usually queued some songs already, and those are
    /// finished by the Function and the API whether or not this page is open - so promising to
    /// remove everything would be a lie, and so would implying nothing is lost.
    /// </remarks>
    protected string LeavePromptMessage => _awaitingTitleConfirmation && !_isUploading
        ? "Your files are ready to upload but have not been uploaded yet. If you leave now they "
          + "will be discarded. Are you sure you want to leave?"
        : "Files are still being sent. If you leave now, anything not yet sent will be cancelled. "
          + "Songs already queued will finish processing without you. Are you sure you want to "
          + "leave?";

    /// <summary>Leave, discarding whatever has not been sent.</summary>
    protected void LeaveAndDiscard() => AnswerLeavePrompt(true);

    /// <summary>Stay, keeping the batch as it is.</summary>
    protected void StayOnThisPage() => AnswerLeavePrompt(false);

    private void AnswerLeavePrompt(bool mayLeave)
    {
        _showLeavePrompt = false;

        // TrySet rather than Set: the circuit can be torn down between the prompt appearing and an
        // answer arriving, and a second answer to an already-settled decision must not throw.
        _leaveDecision?.TrySetResult(mayLeave);
    }

    /// <summary>
    /// Asks whether to leave, and clears up if the answer is yes.
    /// </summary>
    /// <remarks>
    /// The single decision point for both ways off this page: a link click, held by JavaScript and
    /// arriving at <see cref="RequestLeave"/>, and a <c>NavigateTo</c> from inside the circuit
    /// arriving at <see cref="OnBeforeInternalNavigation"/>. Returns whether the navigation may
    /// proceed.
    /// </remarks>
    private async Task<bool> MayLeaveAsync()
    {
        if (!HasWorkToLose)
            return true;

        _leaveDecision = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _showLeavePrompt = true;
        StateHasChanged();

        if (!await _leaveDecision.Task)
        {
            // User chose to stay.
            return false;
        }

        // Cancels only what is still being sent. Queued songs are left alone deliberately.
        _uploadCts.Cancel();

        // The token reaches nothing the browser is doing. On the direct path the bytes are moving
        // from the creator's machine to Azure with this server not in the transfer at all, so
        // without telling the browser to stop it would finish uploading 1.4 GB for a page that is
        // no longer on screen.
        await AbortDirectUploadsAsync();

        _awaitingTitleConfirmation = false;
        CleanupPendingTempFiles();
        await SweepPendingImageBatchAsync();

        return true;
    }

    /// <summary>A link was clicked; JavaScript is holding the navigation until we answer.</summary>
    /// <remarks>The path that actually fires for a link - see <see cref="ArmLeaveGuardAsync"/>.</remarks>
    [JSInvokable]
    public Task<bool> RequestLeave() => MayLeaveAsync();

    /// <summary>Intercepts a <c>NavigateTo</c> made from inside this circuit.</summary>
    protected async Task OnBeforeInternalNavigation(LocationChangingContext context)
    {
        if (!await MayLeaveAsync())
        {
            context.PreventNavigation();
        }
    }

    protected async Task TriggerFileDialog()
    {
        await JS.InvokeVoidAsync("triggerClick", FileInput.Element);
    }

    /// <summary>
    /// Handles circuit disconnection (e.g., browser tab closed during upload). Cancels files still
    /// being sent; queued songs are left to the pipeline.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        UploadProgressHubClient.OnProgress -= ApplyProgressAsync;
        UploadProgressHubClient.OnMatchProgress -= ApplyMatchProgressAsync;
        UploadProgressHubClient.OnMatchResult -= ApplyMatchResultAsync;

        // Cancelling the token no longer stops an upload: the bytes are moving from the browser to
        // Azure and the server is not in that path at all. The browser has to be told, or it happily
        // finishes transferring for a page that no longer exists.
        await AbortDirectUploadsAsync();

        // A batch mid-match dies with the circuit, so release anything still awaiting a pairing
        // rather than leaving the task to sit until its deadline against a page that is gone.
        lock (_matchWaitersLock)
        {
            foreach (var waiter in _matchWaiters.Values)
            {
                waiter.TrySetCanceled();
            }

            _matchWaiters.Clear();
        }

        // Exchanged rather than read-then-null: the poller nulls this field itself when it finishes,
        // so whichever of the two gets here first owns the disposal and the other sees null.
        var poll = Interlocked.Exchange(ref _jobPollCts, null);
        if (poll is not null)
        {
            await poll.CancelAsync();
            poll.Dispose();
        }

        if (_isUploading)
        {
            // Cancels staging that is still in flight. Anything already queued is deliberately left
            // alone: the Function and the API finish it whether or not this page is open, which is
            // the whole point of moving the work off the request thread.
            _uploadCts.Cancel();
        }

        // Backstop for a batch buffered for review that was never uploaded or cancelled,
        // e.g. the circuit dropped while the creator was still editing titles.
        CleanupPendingTempFiles();

        // The click listener is on the document rather than on anything this page owns, so leaving
        // it behind would hold every later link click hostage to a component that is gone.
        if (_leaveGuardModule is not null)
        {
            try
            {
                await _leaveGuardModule.InvokeVoidAsync("disarm");
                await _leaveGuardModule.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
        }

        _leaveGuardRef?.Dispose();

        _uploadCts.Dispose();
    }

    /// <summary>
    /// Sends an email to admin when a creator upload fails due to null CreatorId.
    /// </summary>
    private async Task SendCreatorIdFailureEmailAsync(string fileNames)
    {
        try
        {
            var adminEmail = Configuration["EmailSettings:AdminEmail"] ?? "admin@streamtunes.net";
            var logoHtml = EmailService.GetEmailLogoHtml();
            var subject = "Upload Failure: Creator ID Not Found";
            var body = $@"
                {logoHtml}
                <h2 style='color: #dc3545;'>Upload Failure: Creator ID Not Found</h2>
                <p>A creator attempted to upload files but their Creator ID could not be determined.</p>
                <table style='border-collapse: collapse; width: 100%; margin: 15px 0;'>
                    <tr>
                        <td style='padding: 8px; border: 1px solid #ddd; font-weight: bold;'>Creator Email</td>
                        <td style='padding: 8px; border: 1px solid #ddd;'>{System.Net.WebUtility.HtmlEncode(_currentUserEmail ?? "Unknown")}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px; border: 1px solid #ddd; font-weight: bold;'>Files</td>
                        <td style='padding: 8px; border: 1px solid #ddd;'>{System.Net.WebUtility.HtmlEncode(fileNames)}</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px; border: 1px solid #ddd; font-weight: bold;'>Error</td>
                        <td style='padding: 8px; border: 1px solid #ddd;'>CreatorId is null - the user may not have an active Creator record in the database.</td>
                    </tr>
                    <tr>
                        <td style='padding: 8px; border: 1px solid #ddd; font-weight: bold;'>Timestamp (UTC)</td>
                        <td style='padding: 8px; border: 1px solid #ddd;'>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}</td>
                    </tr>
                </table>
                <p>Please investigate why this creator does not have a Creator record associated with their account.</p>";

            await EmailService.SendEmailAsync(adminEmail, subject, body);
            Logger.LogInformation("UploadFiles: Sent admin notification email about null CreatorId for user {Email}", _currentUserEmail);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "UploadFiles: Failed to send admin notification email about null CreatorId for user {Email}", _currentUserEmail);
        }
    }
}
