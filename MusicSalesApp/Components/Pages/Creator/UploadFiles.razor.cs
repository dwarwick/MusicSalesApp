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

    private sealed record PendingUpload(
        UploadPairItem Item,
        string AudioTempPath,
        string CoverArtTempPath);


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

    // Configuration for chunked uploads
    private const int MaxFilesAllowed = 50;
    private const int ChunkSize = 8;
    private const int ImageOcrChunkSize = 4; // Max images buffered at once during the OCR matching phase
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
            await UploadProgressHubClient.StartAsync();

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

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.Name).ToLowerInvariant();
            if (ValidAudioExtensions.Contains(extension))
                audioFilesByName[file.Name] = file;
            else if (ValidCoverArtExtensions.Contains(extension))
                coverArtFilesByName[file.Name] = file;
        }

        _initialUploadItems = BuildInitialUploadProgressFiles(audioFilesByName.Values, coverArtFilesByName.Values);
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
            foreach (var kvp in audioFilesByName)
            {
                var tempPath = Path.GetTempFileName();
                audioTempPaths[kvp.Key] = tempPath;
                initialUploadItemsByName.TryGetValue(kvp.Key, out var initialUploadItem);
                await BufferBrowserFileToTempFileAsync(kvp.Value, tempPath, _maxAudioFileSize, initialUploadItem, initialUploadProgress);
            }

            foreach (var kvp in coverArtFilesByName)
            {
                var tempPath = Path.GetTempFileName();
                coverArtTempPaths[kvp.Key] = tempPath;
                initialUploadItemsByName.TryGetValue(kvp.Key, out var initialUploadItem);
                await BufferBrowserFileToTempFileAsync(kvp.Value, tempPath, _maxImageFileSize, initialUploadItem, initialUploadProgress);
            }
        }
        catch (InvalidDataException ex)
        {
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
        var invalidContentFiles = new List<string>();
        foreach (var file in audioFilesByName)
        {
            await using var stream = File.OpenRead(audioTempPaths[file.Key]);
            if (!AudioContainerSniffer.ContentMatchesExtension(stream, file.Key, out _))
            {
                invalidContentFiles.Add(file.Key);
            }
        }
        foreach (var file in coverArtFilesByName)
        {
            await using var stream = File.OpenRead(coverArtTempPaths[file.Key]);
            if (!MediaFileContentValidator.ImageContentMatchesExtension(stream, file.Key, out _))
            {
                invalidContentFiles.Add(file.Key);
            }
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

        _initialUploadStatusMessage = "Files received. Matching cover art...";
        _initialUploadBatchProgress = 100;
        await InvokeAsync(StateHasChanged);

        // Process all image files in chunks of ImageOcrChunkSize (4).
        // Each chunk: filename-only match first (no bytes), then OCR for any still-unmatched
        // images in that chunk. At most ImageOcrChunkSize image bytes are in memory at once.
        var matchingResult = await PerformChunkedMatchingAsync(
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
            if (!audioTempPaths.TryGetValue(pair.AudioFileName, out var audioTempPath))
                continue;

            audioFilesByName.TryGetValue(pair.AudioFileName, out var audioFileMeta);
            if (audioFileMeta == null)
                continue;

            string coverArtTempPath = null;
            string coverArtFileName = "(No cover art)";
            long coverArtFileSize = 0;

            if (!string.IsNullOrEmpty(pair.ImageFileName))
            {
                coverArtTempPaths.TryGetValue(pair.ImageFileName, out coverArtTempPath);

                // Metadata for display in the progress table
                if (coverArtFilesByName.TryGetValue(pair.ImageFileName, out var metaFile))
                {
                    coverArtFileName = metaFile.Name;
                    coverArtFileSize = metaFile.Size;
                }
            }

            // The filename no longer determines storage. It only seeds the title, which the
            // creator can edit in the review step before anything is uploaded.
            var uploadItem = new UploadPairItem
            {
                SongTitle = SongTitleHelper.FromFileName(pair.AudioFileName),
                AudioFileName = audioFileMeta.Name,
                AudioFileSize = audioFileMeta.Size,
                CoverArtFileName = coverArtFileName,
                CoverArtFileSize = coverArtFileSize,
                HasCoverArt = coverArtTempPath != null,
                Status = UploadStatus.Pending,
                Progress = 0,
                StatusMessage = "Pending"
            };

            _uploadItems.Add(uploadItem);
            uploadItemsWithFiles.Add(new PendingUpload(
                uploadItem,
                audioTempPath,
                coverArtTempPath));
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

            if (beforeUnloadEnabled)
            {
                await DisableBeforeUnloadAsync();
            }

            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>Discards a reviewed-but-not-yet-uploaded batch.</summary>
    protected async Task CancelPendingBatchAsync()
    {
        if (!_awaitingTitleConfirmation)
            return;

        _awaitingTitleConfirmation = false;
        CleanupPendingTempFiles();
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
        _initialUploadBatchProgress = batchPercent;
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
    /// Stages every upload to Azure, keeping at most <see cref="ChunkSize"/> <em>files</em> - not
    /// pairs - in flight at once. A pair counts as two, so a song and its cover art are never split
    /// across the boundary.
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

        // Counts files rather than items, so the window means the same thing it always did.
        using var slots = new SemaphoreSlim(ChunkSize, ChunkSize);
        var running = new List<Task>(itemsList.Count);

        try
        {
            foreach (var pending in itemsList)
            {
                // An item wider than the whole window would wait forever for slots that can never
                // all be free at once, so clamp it. Only reachable if ChunkSize is ever set below 2.
                var cost = Math.Min(pending.CoverArtTempPath != null ? 2 : 1, ChunkSize);

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
                // Both of these already record their own failures against the row rather than
                // throwing, so one bad song cannot tear down the rest of the batch.
                await (pending.CoverArtTempPath != null
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
        var (uploadItem, audioTempPath, _) = pending;

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
        var (uploadItem, audioTempPath, coverArtTempPath) = pending;

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
    /// Processes ALL image files in chunks of <see cref="ImageOcrChunkSize"/> (4).
    /// For each chunk: (a) filename-only matching — no bytes read; then (b) for images still
    /// unmatched in that chunk, read their bytes from temp files and attempt OCR matching.
    /// At most <see cref="ImageOcrChunkSize"/> image byte arrays are in memory at any one time.
    /// Bytes for unmatched images in each chunk become GC-eligible immediately after the chunk.
    /// </summary>
    private async Task<FileMatchingResult> PerformChunkedMatchingAsync(
            List<string> audioFileNames,
            List<string> allImageFileNames,
            Dictionary<string, string> coverArtTempPaths)
    {
        var allPairs = new List<FilePair>();
        var unmatchedImages = new List<string>();
        var remainingAudioNames = audioFileNames.ToList();

        for (int i = 0; i < allImageFileNames.Count; i += ImageOcrChunkSize)
        {
            if (!remainingAudioNames.Any())
            {
                // All audio already matched — remaining images are unmatched
                unmatchedImages.AddRange(allImageFileNames.Skip(i));
                break;
            }

            var imageChunk = allImageFileNames.Skip(i).Take(ImageOcrChunkSize).ToList();

            // Step A: filename-only match for this chunk (no bytes read)
            FileMatchingResult chunkResult;
            try
            {
                chunkResult = await FileMatchingService.MatchFilesAsync(remainingAudioNames, imageChunk);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "UploadFiles: Filename matching failed for image chunk {ChunkIndex}.", i / ImageOcrChunkSize);
                chunkResult = new FileMatchingResult
                {
                    Pairs = remainingAudioNames.Select(a => new FilePair
                    {
                        AudioFileName = a,
                        ImageFileName = null,
                        NormalizedName = MusicUploadService.GetNormalizedBaseName(a)
                    }).ToList(),
                    UnmatchedImageFiles = imageChunk
                };
            }

            // Collect filename-matched pairs; remove matched audio from the remaining pool
            foreach (var pair in chunkResult.Pairs.Where(p => p.ImageFileName != null))
            {
                allPairs.Add(pair);
                remainingAudioNames.Remove(pair.AudioFileName);
            }

            // Step B: OCR for images still unmatched in this chunk (up to ImageOcrChunkSize bytes at once)
            var stillUnmatched = chunkResult.UnmatchedImageFiles.ToList();
            if (stillUnmatched.Any() && remainingAudioNames.Any())
            {
                var chunkTempPaths = stillUnmatched
                    .Where(coverArtTempPaths.ContainsKey)
                    .ToDictionary(f => f, f => coverArtTempPaths[f]);

                if (chunkTempPaths.Any())
                {
                    var chunkData = await BufferCoverArtChunkAsync(chunkTempPaths);
                    try
                    {
                        var ocrResult = await FileMatchingService.MatchFilesAsync(
                            remainingAudioNames, stillUnmatched, chunkData);

                        foreach (var pair in ocrResult.Pairs.Where(p => p.ImageFileName != null))
                        {
                            allPairs.Add(pair);
                            remainingAudioNames.Remove(pair.AudioFileName);
                        }
                        unmatchedImages.AddRange(ocrResult.UnmatchedImageFiles);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "UploadFiles: OCR matching failed for image chunk {ChunkIndex}; images will remain unmatched.", i / ImageOcrChunkSize);
                        unmatchedImages.AddRange(stillUnmatched);
                    }
                    // Bytes for images in chunkData go out of scope → GC-eligible
                }
            }
            else
            {
                unmatchedImages.AddRange(stillUnmatched);
            }
        }

        // Audio files with no image match → audio-only pairs
        foreach (var audio in remainingAudioNames)
        {
            allPairs.Add(new FilePair
            {
                AudioFileName = audio,
                ImageFileName = null,
                NormalizedName = MusicUploadService.GetNormalizedBaseName(audio)
            });
        }

        return new FileMatchingResult { Pairs = allPairs, UnmatchedImageFiles = unmatchedImages };
    }

    /// <summary>
    /// Reads a subset of cover art temp files into memory for OCR matching.
    /// Called once per OCR chunk — only <see cref="ImageOcrChunkSize"/> images at a time.
    /// </summary>
    private async Task<Dictionary<string, (byte[] Data, string ContentType)>> BufferCoverArtChunkAsync(
        Dictionary<string, string> coverArtTempPaths)
    {
        var result = new Dictionary<string, (byte[] Data, string ContentType)>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in coverArtTempPaths)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(kvp.Value, _uploadCts.Token);
                var extension = Path.GetExtension(kvp.Key).ToLowerInvariant();
                var contentType = extension switch
                {
                    ".png" => "image/png",
                    _ => "image/jpeg"
                };
                result[kvp.Key] = (bytes, contentType);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "UploadFiles: Failed to read temp file for image '{FileName}' for OCR; it will be unmatched.", kvp.Key);
            }
        }

        return result;
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
            _awaitingTitleConfirmation = false;
            CleanupPendingTempFiles();
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
