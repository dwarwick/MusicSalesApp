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

    /// <summary>
    /// How often the fallback poll asks whether the attempt is still running.
    ///
    /// <para>
    /// Chosen to match the reasoning behind the Function's own timer intervals: this is a backstop
    /// for a dropped push, not the mechanism, so it trades a slower worst case for far fewer queries
    /// than a bar that visibly ticks would need.
    /// </para>
    /// </summary>
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(30);

    private PeriodicTimer? _statusPoller;

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

    /// <summary>Whether there is something to open the timing editor on.</summary>
    protected bool _hasTimings =>
        _status?.TimingsBlobPath is not null
        && _status.Status is SongLyricsStatus.Published or SongLyricsStatus.NeedsReview;

    /// <summary>
    /// Send the creator to the timing editor for this song.
    /// </summary>
    protected async Task OpenTimingEditorAsync()
    {
        if (SongMetadataId is null)
        {
            return;
        }

        await CloseAsync();

        // The shared route helper rather than the path, for the reason it documents: the completion
        // email links here too, and the two must not drift apart.
        NavigationManager.NavigateTo(AppPageRoutes.CreatorSongLyrics(SongMetadataId.Value));
    }

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

        // Editing is offered first on purpose. Machine alignment lands 150-300 ms out on a good day,
        // so a re-run of the same text usually returns something just as approximate after another
        // several minutes of compute - while tapping the few lines that drifted is quick and exact.
        // The re-run is still named, because it is the only thing that helps when the pasted text was
        // wrong rather than the timing.
        // States the score without passing a verdict on it. NeedsReview is where EVERY successful
        // alignment lands now, at any confidence, so copy that reads "we aren't confident in this"
        // would tell a creator with a 95% result that their song went badly.
        SongLyricsStatus.NeedsReview =>
            $"Timed with {_status.Confidence ?? 0d:P0} confidence. Nothing is shown to listeners "
            + "until you press Publish. Have a listen - fixing the few lines that drifted is "
            + "usually quicker than timing it again. If the words themselves are wrong, edit them "
            + "and re-run instead.",

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

                // An attempt that was already in flight when this dialog opened needs the fallback
                // just as much as one started here - more, in fact, since a reopened dialog is
                // usually a reconnected circuit, which is exactly when a push goes missing.
                StartStatusPoller();
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

            StartStatusPoller();
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

        StopStatusPoller();
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
            await ApplyTerminalStateAsync();
            return;
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

    private void StartStatusPoller()
    {
        StopStatusPoller();

        // The timer is passed to the loop rather than read from the field, because the field is
        // nulled the moment anything stops the poller - and reading it from the loop condition after
        // that would dereference null on a fire-and-forget task, where nobody would ever see it.
        var poller = new PeriodicTimer(StatusPollInterval);
        _statusPoller = poller;
        _ = PollStatusAsync(poller);
    }

    private void StopStatusPoller()
    {
        _statusPoller?.Dispose();
        _statusPoller = null;
    }

    /// <summary>
    /// The fallback that notices an attempt finished when the terminal SignalR push did not arrive.
    ///
    /// <para>
    /// Deliberately only a fallback. It reads the job row, which records the step but not the
    /// progress within it, so it can only ever move the bar to a band start - the live pushes carry
    /// the fractional progress and stay authoritative while they are arriving.
    /// </para>
    /// </summary>
    private async Task PollStatusAsync(PeriodicTimer poller)
    {
        try
        {
            while (await poller.WaitForNextTickAsync())
            {
                var watchedJobId = _activeJob?.JobId;

                if (!_isRunning || watchedJobId is null || SongMetadataId is null)
                {
                    break;
                }

                try
                {
                    var updated = await LyricsService.GetActiveJobAsync(SongMetadataId.Value);

                    // GetActiveJobAsync only ever returns Queued or Processing rows, so an attempt
                    // that has finished - however it finished - stops being returned at all. That
                    // disappearance IS the terminal signal here; there is no terminal row to read.
                    if (updated is null || updated.JobId != watchedJobId)
                    {
                        await ApplyTerminalStateAsync();
                        break;
                    }

                    if (updated.Step > _activeJob!.Step)
                    {
                        _activeJob = updated;
                        _progressPercent = LyricsAlignmentProgressCalculator.ToOverallPercent(updated.Step);
                        _progressDetail = DescribeStep(updated.Step);
                        await InvokeAsync(StateHasChanged);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Warning rather than Debug: this is the safety net for a lost terminal push, so
                    // it failing silently leaves the creator watching a bar that will never move and
                    // leaves nothing in the logs to explain why.
                    Logger.LogWarning(
                        ex, "Lyrics status poll failed for job {JobId}.", watchedJobId);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Stopped while waiting for the next tick. Expected on completion and on dispose.
        }
        finally
        {
            poller.Dispose();
        }
    }

    /// <summary>
    /// Closes the attempt out in the UI, from whichever path noticed first.
    ///
    /// <para>
    /// <b>The repaint is in a finally, and the parent refresh happens after it.</b> Both matter.
    /// <c>_isRunning</c> gates the progress bar, so clearing it without repainting leaves the bar
    /// frozen at whatever it last showed - and because the poller stops once the attempt is no longer
    /// running, nothing would ever come back to correct it. Refreshing the parent grid is a much
    /// larger operation than this dialog, and it must not be able to strand this dialog by throwing.
    /// </para>
    /// </summary>
    private async Task ApplyTerminalStateAsync()
    {
        StopStatusPoller();

        _isRunning = false;
        _activeJob = null;

        try
        {
            if (SongMetadataId is not null)
            {
                _status = await LyricsService.GetForSongAsync(SongMetadataId.Value);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex, "Could not refresh lyrics status for song {SongId}.", SongMetadataId);
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }

        try
        {
            await OnCompleted.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex, "Could not refresh the song list after lyrics job for song {SongId} finished.",
                SongMetadataId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopStatusPoller();

        if (_subscribed)
        {
            UploadProgressHubClient.OnLyricsProgress -= HandleProgressAsync;
            _subscribed = false;
        }

        await ValueTask.CompletedTask;
    }
}
