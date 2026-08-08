using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
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
    protected bool _awaitingTitleConfirmation = false;
    private readonly List<PendingUpload> _pendingUploads = new();
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
    /// The last three are two alternatives, not a set: on the server path the bytes are on this
    /// machine and named by <see cref="AudioTempPath"/>/<see cref="CoverArtTempPath"/>; on the direct
    /// path they never touch it, and what is carried instead is where the browser can find the audio
    /// (<see cref="BrowserAudioIndex"/>) and where its already-uploaded cover art is sitting in
    /// staging (<see cref="CoverArtStagedPath"/>).
    /// </para>
    /// </summary>
    private sealed record PendingUpload(
        UploadPairItem Item,
        string AudioTempPath,
        string CoverArtTempPath,
        int? BrowserAudioIndex = null,
        string CoverArtStagedPath = null);


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

    // The match batch whose staged images are still needed. On the direct path those images ARE the
    // cover art - nothing else holds a copy - so the folder cannot be swept until every song that
    // matched one has been created.
    private Guid? _pendingImageBatchId;

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
    /// inspected, and now for the whole of processing. It comes back once every song is terminal,
    /// which is the same moment the batch bar reaches 100%.
    /// </summary>
    protected bool HideUploadBox => _isUploading || _isProcessingFiles || IsBatchProcessing;

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
                return "Waiting for you to fix the highlighted titles.";
            }

            return $"Processing songs ({finished} of {_uploadItems.Count} finished)...";
        }
    }

    protected InputFile FileInput { get; set;}

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedCreatorId)
        {
            _hasLoadedCreatorId = true;
            await LoadCreatorIdAsync();
            await LoadMaxAudioFileSizeAsync();

            UploadProgressHubClient.OnProgress += ApplyProgressAsync;
            UploadProgressHubClient.OnMatchProgress += ApplyMatchProgressAsync;
            UploadProgressHubClient.OnMatchResult += ApplyMatchResultAsync;
            await UploadProgressHubClient.StartAsync();

            await InitialiseDirectUploadAsync();

            // Songs queued on a previous visit are still being processed, so rebuild their rows
            // rather than showing an empty page while work is in flight.
            await RestoreInFlightJobsAsync();
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
        _awaitingTitleConfirmation = false;
        CleanupPendingTempFiles();

        // Clear previous validation errors
        ClearValidationError();
        _filesReceived = false;
        _uploadItems.Clear();
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
        _uploadCts = new CancellationTokenSource();
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

        for (var browserIndex = 0; browserIndex < files.Count; browserIndex++)
        {
            var file = files[browserIndex];
            var extension = Path.GetExtension(file.Name).ToLowerInvariant();

            if (ValidAudioExtensions.Contains(extension))
                audioFilesByName[file.Name] = file;
            else if (ValidCoverArtExtensions.Contains(extension))
                coverArtFilesByName[file.Name] = file;
            else
                continue;

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
            _browserFileIndexes.TryGetValue(pair.AudioFileName, out var browserAudioIndex);

            int? directAudioIndex = receivesAudio ? null : browserAudioIndex;
            if (audioTempPath == null && directAudioIndex == null)
                continue;

            audioFilesByName.TryGetValue(pair.AudioFileName, out var audioFileMeta);
            if (audioFileMeta == null)
                continue;

            string coverArtTempPath = null;
            string coverArtStagedPath = null;
            string coverArtFileName = "(No cover art)";
            long coverArtFileSize = 0;

            if (!string.IsNullOrEmpty(pair.ImageFileName))
            {
                coverArtTempPaths.TryGetValue(pair.ImageFileName, out coverArtTempPath);
                _stagedImagePaths.TryGetValue(pair.ImageFileName, out coverArtStagedPath);

                // Metadata for display in the progress table
                if (coverArtFilesByName.TryGetValue(pair.ImageFileName, out var metaFile))
                {
                    coverArtFileName = metaFile.Name;
                    coverArtFileSize = metaFile.Size;
                }
            }

            // On the direct path the cover art is already in Azure and only its blob path matters, so
            // "has cover art" has to ask about that rather than about a temp file that was never the
            // thing being uploaded.
            var hasCoverArt = directAudioIndex is not null
                ? coverArtStagedPath != null
                : coverArtTempPath != null;

            // The filename no longer determines storage. It only seeds the title, which the
            // creator can edit in the review step before anything is uploaded.
            var uploadItem = new UploadPairItem
            {
                SongTitle = SongTitleHelper.FromFileName(pair.AudioFileName),
                AudioFileName = audioFileMeta.Name,
                AudioFileSize = audioFileMeta.Size,
                CoverArtFileName = coverArtFileName,
                CoverArtFileSize = coverArtFileSize,
                HasCoverArt = hasCoverArt,
                Status = UploadStatus.Pending,
                Progress = 0,
                StatusMessage = "Pending"
            };

            _uploadItems.Add(uploadItem);
            uploadItemsWithFiles.Add(new PendingUpload(
                uploadItem,
                audioTempPath,
                coverArtTempPath,
                directAudioIndex,
                coverArtStagedPath));
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
        // `finally` must no longer delete them.
        _pendingUploads.AddRange(uploadItemsWithFiles);
        _pendingTempFiles.AddRange(audioTempPaths.Values);
        _pendingTempFiles.AddRange(coverArtTempPaths.Values);
        audioTempPaths.Clear();
        coverArtTempPaths.Clear();

        // Upload without asking when the titles taken from the filenames are all usable. The
        // creator only has to intervene when one is blank, too long, or collides with another
        // song - the cases where uploading first and fixing later would mean a failed upload.
        if (await PendingTitlesNeedAttentionAsync())
        {
            _awaitingTitleConfirmation = true;
            _isUploading = false;
            await DisableBeforeUnloadAsync();
            beforeUnloadEnabled = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        await InvokeAsync(StateHasChanged);
        await RunPendingUploadsAsync();
        beforeUnloadEnabled = false;
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

    /// <summary>
    /// Checks the titles taken from the filenames and marks any that the creator has to fix.
    /// Returns true when the batch cannot upload as-is.
    /// </summary>
    private async Task<bool> PendingTitlesNeedAttentionAsync()
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

        var problems = _pendingUploads.Count(pending => pending.Item.TitleError != null);
        if (problems == 0)
            return false;

        _validationErrorMessage = problems == 1
            ? "One song needs a different title before this batch can upload. It is highlighted below."
            : $"{problems} songs need different titles before this batch can upload. They are highlighted below.";
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

        if (await PendingTitlesNeedAttentionAsync())
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
        _uploadCts = new CancellationTokenSource();
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
    private async Task SweepPendingImageBatchAsync()
    {
        var batchId = _pendingImageBatchId;
        _pendingImageBatchId = null;
        _stagedImagePaths.Clear();

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
                    : Math.Min(pending.CoverArtTempPath != null ? 2 : 1, window);

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
                    : pending.CoverArtTempPath != null
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
        var coverArtTempPath = pending.CoverArtTempPath;

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

            var target = await UploadStagingSasService.CreateAudioTargetAsync(
                mediaGuid, Path.GetExtension(uploadItem.AudioFileName), _uploadCts.Token);

            // One phase per song, not one per batch. The window that decides how many run at once is
            // the semaphore in ProcessUploadsInChunksAsync, so a song reaching CreateFromStagedAsync
            // does so the moment its own bytes land rather than waiting for its slowest sibling -
            // the same pipelining the server path gets from that loop.
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
                    CreatorId = _currentCreatorId.Value,
                    CoverArtFileName = pending.CoverArtStagedPath is null ? null : uploadItem.CoverArtFileName,
                    CoverArtStagedPath = pending.CoverArtStagedPath
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

    /// <summary>One file finished, so the next stage for it can start without waiting for the rest.</summary>
    [JSInvokable]
    public Task FileUploaded(string phaseId, int index) => Task.CompletedTask;

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
    private async Task<FileMatchingResult> MatchCoverArtAsync(
        List<string> audioFileNames,
        List<string> imageFileNames,
        Dictionary<string, string> coverArtTempPaths)
    {
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
    private async Task StageImagesFromBrowserAsync(
        Guid batchId,
        int creatorId,
        List<string> audioFileNames,
        List<string> imageFileNames)
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

            var target = await UploadStagingSasService.CreateMatchImageTargetAsync(
                batchId, index, Path.GetExtension(fileName), _uploadCts.Token);

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

        await CoverArtMatchService.EnqueueAsync(batchId, creatorId, audioFileNames, targets, _uploadCts.Token);
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

    protected void ClearValidationError()
    {
        _validationErrorMessage = string.Empty;
        _unmatchedMp3Files.Clear();
        _unmatchedCoverArtFiles.Clear();
        _duplicateSongFiles.Clear();
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

    protected class UploadPairItem
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

        public string AudioFileName { get; set; } = string.Empty;
        public long AudioFileSize { get; set; }
        public string CoverArtFileName { get; set; } = string.Empty;
        public long CoverArtFileSize { get; set; }
        public bool HasCoverArt { get; set; }
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

    protected enum UploadStatus
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
    protected async Task OnBeforeInternalNavigation(LocationChangingContext context)
    {
        if (!_isUploading && !_isProcessingFiles && !_awaitingTitleConfirmation)
            return;

        var isConfirmed = await JS.InvokeAsync<bool>("confirm",
            _awaitingTitleConfirmation && !_isUploading
                ? "Your files are ready to upload but have not been uploaded yet. If you leave now they will be discarded. Are you sure you want to leave?"
                : "Files are still being sent. If you leave now, anything not yet sent will be cancelled. Songs already queued will finish processing without you. Are you sure you want to leave?");

        if (isConfirmed)
        {
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
        }
        else
        {
            // User chose to stay — prevent navigation
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
