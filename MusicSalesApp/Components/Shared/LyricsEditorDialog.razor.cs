#nullable enable
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Notifications;

namespace MusicSalesApp.Components.Shared;

/// <summary>
/// Where a creator pastes a song's lyrics and watches them being timed.
///
/// <para>
/// A component of its own rather than more code in <c>CreatorSongManagement.razor.cs</c>, which is
/// already 886 lines and owns a different job entirely.
/// </para>
///
/// <para>
/// The state it shows is assembled from two rows, and the distinction matters: <c>SongLyrics</c> is
/// what a listener would get - one row per song, the published state - while
/// <c>LyricsAlignmentJob</c> is one row per attempt, which is what the progress bar follows. A song
/// can have published timings and a re-run in flight at the same time, and this shows both.
/// </para>
/// </summary>
public partial class LyricsEditorDialogModel : BlazorBase, IAsyncDisposable
{
    [Parameter]
    public SongAdminViewModel? Song { get; set; }

    [Parameter]
    public int CreatorId { get; set; }

    [Parameter]
    public bool IsVisible { get; set; }

    [Parameter]
    public EventCallback<bool> IsVisibleChanged { get; set; }

    /// <summary>Raised once an attempt reaches a terminal state, so the grid can refresh.</summary>
    [Parameter]
    public EventCallback OnCompleted { get; set; }

    protected string _lyricsText = string.Empty;
    protected readonly List<string> _validationErrors = [];

    protected SongLyrics? _status;
    protected LyricsAlignmentJob? _activeJob;

    protected bool _isSaving;
    protected bool _isRunning;
    protected double _progressPercent;
    protected string _progressDetail = "Starting…";

    private int? _loadedSongId;

    /// <summary>
    /// The song's <c>SongMetadata.Id</c>.
    ///
    /// <para>
    /// Parsed rather than read, because <see cref="SongAdminViewModel.Id"/> is a string - it
    /// predates the GUID scheme and still carries the identifier as text. Every other caller on this
    /// page does the same <c>int.TryParse</c>, so this follows suit rather than changing a view
    /// model half the creator and admin screens share.
    /// </para>
    /// </summary>
    private int? SongMetadataId =>
        int.TryParse(Song?.Id, out var parsed) ? parsed : null;
    private bool _subscribed;

    protected static int MaxCharacters => LyricsTextLimits.MaxCharacters;

    protected static int MaxLines => LyricsTextLimits.MaxLines;

    protected int CharacterCount => _lyricsText?.Length ?? 0;

    protected int LineCount =>
        string.IsNullOrEmpty(_lyricsText) ? 0 : _lyricsText.Count(c => c == '\n') + 1;

    protected bool IsOverLimit => CharacterCount > MaxCharacters || LineCount > MaxLines;

    /// <summary>
    /// "Time lyrics" the first time, "Re-run timing" afterwards.
    ///
    /// <para>
    /// Worth being explicit about, because a re-run is the intended fix for a low-confidence result
    /// and the button being labelled the same either way makes it look like a no-op.
    /// </para>
    /// </summary>
    protected string SubmitLabel =>
        _status is null || _status.Status == SongLyricsStatus.Pending ? "Time lyrics" : "Re-run timing";

    protected MessageSeverity _statusSeverity => _status?.Status switch
    {
        SongLyricsStatus.Published => MessageSeverity.Success,
        SongLyricsStatus.NeedsReview => MessageSeverity.Warning,
        SongLyricsStatus.Failed => MessageSeverity.Error,
        _ => MessageSeverity.Info
    };

    protected string _statusMessage => _status?.Status switch
    {
        SongLyricsStatus.Published =>
            $"Lyrics timed ({_status.Confidence ?? 0d:P0} confidence). Listeners will see them in time with the song.",

        // The single most useful thing to say here. A large share of low-confidence results are
        // caused by something in the pasted text the singer never sings, and pointing at that is
        // worth more to the creator than the number is.
        SongLyricsStatus.NeedsReview =>
            $"We timed these lyrics but aren't confident in the result ({_status.Confidence ?? 0d:P0}), "
            + "so they won't be shown to listeners yet. Re-running after removing anything that isn't "
            + "sung - spoken asides, credits, repeated section notes - often fixes it.",

        SongLyricsStatus.Failed => "We couldn't time these lyrics. You can edit them and try again.",
        SongLyricsStatus.Pending => "These lyrics are queued for timing.",
        _ => string.Empty
    };

    protected override async Task OnParametersSetAsync()
    {
        // Only flags and a load trigger here - never a database call on every parameter change. The
        // load itself is guarded on the song actually changing.
        if (!IsVisible || SongMetadataId is null || _loadedSongId == SongMetadataId)
        {
            return;
        }

        _loadedSongId = SongMetadataId;
        await LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _subscribed)
        {
            return;
        }

        _subscribed = true;
        UploadProgressHubClient.OnLyricsProgress += HandleProgressAsync;

        try
        {
            await UploadProgressHubClient.StartAsync();
        }
        catch (Exception ex)
        {
            // A bar that does not move is a much smaller problem than a dialog that will not open,
            // and the alignment carries on regardless - the creator can reopen this to see where it
            // got to.
            Logger.LogDebug(ex, "Could not connect to the upload progress hub for lyrics.");
        }
    }

    private async Task LoadAsync()
    {
        _validationErrors.Clear();

        try
        {
            _status = await LyricsService.GetForSongAsync(SongMetadataId!.Value);
            _activeJob = await LyricsService.GetActiveJobAsync(SongMetadataId!.Value);
            _isRunning = _activeJob is not null;

            if (_activeJob is not null)
            {
                _progressPercent = LyricsAlignmentProgressCalculator.ToOverallPercent(_activeJob.Step);
                _progressDetail = DescribeStep(_activeJob.Step);
            }

            // Deliberately not pre-filled from the stored blob. Reading it back would cost a blob
            // fetch every time the dialog opens, and the creator is here to paste - if they want the
            // previous text they have it in whatever they pasted from.
            _lyricsText = string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load lyrics state for song {SongId}.", SongMetadataId);
            _validationErrors.Add("We couldn't load this song's lyrics. Please try again.");
        }
    }

    protected async Task SubmitAsync()
    {
        if (SongMetadataId is null || _isSaving)
        {
            return;
        }

        _validationErrors.Clear();
        _isSaving = true;

        try
        {
            var result = await LyricsService.SubmitAsync(SongMetadataId.Value, CreatorId, _lyricsText);

            if (!result.Accepted)
            {
                _validationErrors.Add(result.Message);
                return;
            }

            _isRunning = true;
            _progressPercent = LyricsAlignmentProgressCalculator.ToOverallPercent(
                LyricsAlignmentStep.Submitted);
            _progressDetail = "Starting…";

            _status = await LyricsService.GetForSongAsync(SongMetadataId!.Value);
            _activeJob = await LyricsService.GetActiveJobAsync(SongMetadataId!.Value);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not submit lyrics for song {SongId}.", SongMetadataId);
            _validationErrors.Add("Something went wrong submitting these lyrics. Please try again.");
        }
        finally
        {
            _isSaving = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task CancelAsync()
    {
        if (SongMetadataId is null)
        {
            return;
        }

        _isSaving = true;

        try
        {
            await LyricsService.CancelAsync(SongMetadataId.Value, CreatorId);
            _isRunning = false;
            _activeJob = null;
            _status = await LyricsService.GetForSongAsync(SongMetadataId!.Value);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not cancel lyrics timing for song {SongId}.", SongMetadataId);
            _validationErrors.Add("We couldn't cancel that. Please try again.");
        }
        finally
        {
            _isSaving = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task DownloadLrcAsync()
    {
        if (_status?.LrcBlobPath is null)
        {
            return;
        }

        // A short-lived read SAS straight to storage rather than proxying the bytes through the
        // circuit. The lyrics blobs are deliberately not on MusicController's public whitelist, so
        // this is the only way to reach one - and it is scoped to this creator's own request.
        var uri = AzureStorageService.GetReadSasUri(_status.LrcBlobPath, TimeSpan.FromMinutes(10));
        await JS.InvokeVoidAsync("open", uri.ToString(), "_blank");
    }

    protected async Task CloseAsync()
    {
        IsVisible = false;
        _loadedSongId = null;
        await IsVisibleChanged.InvokeAsync(false);
    }

    private async Task HandleProgressAsync(LyricsAlignmentProgress progress)
    {
        // The hub carries every attempt for this creator, including other songs'. Filtering on the
        // attempt's own id is what stops one song's progress driving another song's bar.
        if (_activeJob is null || progress.JobId != _activeJob.JobId)
        {
            return;
        }

        _progressPercent = progress.OverallPercent;
        _progressDetail = string.IsNullOrWhiteSpace(progress.Detail)
            ? DescribeStep(progress.Step)
            : progress.Detail!;

        if (LyricsAlignmentProgressCalculator.IsTerminal(progress.Step))
        {
            _isRunning = false;
            _activeJob = null;

            if (SongMetadataId is not null)
            {
                _status = await LyricsService.GetForSongAsync(SongMetadataId.Value);
            }

            await OnCompleted.InvokeAsync();
        }

        await InvokeAsync(StateHasChanged);
    }

    private static string DescribeStep(LyricsAlignmentStep step) => step switch
    {
        LyricsAlignmentStep.Submitted => "Saving the lyrics…",

        // Named honestly. A cold Flex instance has to import torch and pull model weights off a file
        // share before the first activity runs, so this can genuinely sit here for a couple of
        // minutes, and "Waiting for a worker" reads far better than a bar that appears stuck.
        LyricsAlignmentStep.Queued => "Waiting for a worker…",

        LyricsAlignmentStep.Preparing => "Preparing the audio…",
        LyricsAlignmentStep.SeparatingVocals => "Isolating the vocal — this is the slow part…",
        LyricsAlignmentStep.Aligning => "Matching the words to the vocal…",
        LyricsAlignmentStep.Mapping => "Placing the words on the timeline…",
        LyricsAlignmentStep.WritingOutputs => "Saving the timings…",
        LyricsAlignmentStep.Copying => "Storing the timings…",
        LyricsAlignmentStep.Saving => "Almost done…",
        LyricsAlignmentStep.Completed => "Done.",
        LyricsAlignmentStep.Failed => "Timing failed.",
        _ => "Working…"
    };

    public ValueTask DisposeAsync()
    {
        if (_subscribed)
        {
            UploadProgressHubClient.OnLyricsProgress -= HandleProgressAsync;
            _subscribed = false;
        }

        return ValueTask.CompletedTask;
    }
}
