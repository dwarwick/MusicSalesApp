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

    /// <summary>
    /// Whether this was opened to replace a song's words rather than to time them for the first time.
    /// </summary>
    /// <remarks>
    /// The submit button is normally hidden once a song has timings, because re-running the SAME
    /// words is the thing this feature stopped offering: it costs another separation pass and comes
    /// back with the same result. Replacing words that were wrong in the first place is a different
    /// request and the only way back from a faithful alignment of the wrong lyrics, so the host says
    /// which of the two it is opening this for.
    /// </remarks>
    [Parameter]
    public bool IsReplacing { get; set; }

    [Parameter]
    public EventCallback<bool> IsVisibleChanged { get; set; }

    /// <summary>Raised once an attempt reaches a terminal state, so the grid can refresh.</summary>
    [Parameter]
    public EventCallback OnCompleted { get; set; }

    /// <summary>
    /// Raised with the song's id when an attempt produced usable timings.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OnCompleted"/>, which fires however an attempt ended and means only
    /// "your data is stale". This one is the handoff: it fires solely on success, and it is the host's
    /// cue to take the creator to Preview Results. Kept as a callback rather than a navigation of our
    /// own so that the host - which knows what page it is and whether the creator is still there -
    /// decides, instead of this dialog inferring it from its own visibility.
    /// </remarks>
    [Parameter]
    public EventCallback<int> OnTimingCompleted { get; set; }

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
    /// "Time lyrics" normally, "Try again" after a run that produced nothing usable.
    ///
    /// <para>
    /// There is no longer a re-run label, because there is no longer a re-run: this button is hidden
    /// entirely once timings exist. "Try again" is reserved for the one case where running the
    /// pipeline a second time is genuinely the fix - a failure, where the pasted words themselves
    /// were most likely wrong.
    /// </para>
    /// </summary>
    protected string SubmitLabel => _status?.Status switch
    {
        SongLyricsStatus.Failed => "Try again",
        _ when IsReplacing && _hasTimings => "Replace and time",
        _ => "Time lyrics"
    };

    /// <summary>Whether to offer to run the pipeline at all.</summary>
    /// <remarks>
    /// Two ways to qualify, and they are different questions. No timings yet - never run, still
    /// running, or a run that failed - means there is nothing to lose by running. Timings that exist
    /// but describe the wrong words mean running is the only repair there is.
    /// </remarks>
    protected bool _canSubmit => LyricsService.IsAvailable && (!_hasTimings || IsReplacing);

    /// <summary>Whether there is something to open the timing editor on.</summary>
    protected bool _hasTimings =>
        _status?.TimingsBlobPath is not null
        && _status.Status is SongLyricsStatus.Published or SongLyricsStatus.NeedsReview;


    protected MessageSeverity _statusSeverity => IsReplacing && _hasTimings
        ? MessageSeverity.Warning
        : _status?.Status switch
    {
        SongLyricsStatus.Published => MessageSeverity.Success,
        SongLyricsStatus.NeedsReview => MessageSeverity.Warning,
        SongLyricsStatus.Failed => MessageSeverity.Error,
        _ => MessageSeverity.Info
    };

    // NO CONFIDENCE FIGURE IN ANY OF THESE. The aligner's composite score is systematically
    // pessimistic - timings a creator would call perfect routinely score in the fifties - so quoting
    // it talked people out of results that were fine and into a re-run that costs another separation
    // pass and comes back no better. The score is still computed and stored for diagnosing the
    // aligner; it is simply not evidence a creator can act on.
    // Warned about rather than done quietly, because a replacement is the one path here that
    // DESTROYS work: the completion service discards the draft, since a set of edits made against
    // the old timings would silently reapply to words that are no longer there. A creator who has
    // spent twenty minutes tapping a chorus is entitled to know that before they paste.
    protected string _statusMessage => IsReplacing && _hasTimings
        ? "Paste the corrected words below. Timing runs again from scratch and replaces this song's "
          + "current timings - including anything you have tapped, saved or published."
        : _status?.Status switch
    {
        SongLyricsStatus.Published =>
            "Lyrics timed. Listeners will see them in time with the song.",

        SongLyricsStatus.NeedsReview =>
            "These lyrics are timed. Nothing is shown to listeners until you press Publish.",

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

        // Information, not Debug: this is the hinge of the whole finish-and-preview flow and it runs
        // on a server nobody can attach a debugger to. When a creator reports being left in the paste
        // box, this line is the difference between knowing the signal arrived and guessing.
        Logger.LogInformation(
            "Lyrics timing reached a terminal state for song {SongId}: status={Status}, hasTimings={HasTimings}.",
            SongMetadataId,
            _status?.Status,
            _hasTimings);

        // Timing landed, so this dialog has nothing left to say - close it and hand the song back to
        // whoever is hosting us. A failure deliberately does NOT: it stays open, where the message
        // and "Try again" are.
        //
        // THE DECISION OF WHERE TO GO NEXT IS DELIBERATELY NOT MADE HERE. It used to be, gated on
        // IsVisible, and a creator watching the bar to the end was still left sitting in the paste
        // box - IsVisible is a parameter SfDialog also writes to through @bind-Visible, so it is not
        // a trustworthy answer to "is anyone still looking at this". The host knows better than we
        // do: CreatorSongManagement only exists while the creator is on the songs grid, so raising
        // this there sends them onward exactly when that is welcome, and does nothing at all once
        // they have navigated away - without this component having to guess.
        // ON THE DISPATCHER, because this method is reached from a SignalR callback and from a timer
        // tick, neither of which runs on it. Everything below touches component state the renderer
        // owns - closing this dialog, and whatever the host does with the handoff, which is a
        // navigation. Off the dispatcher that throws, into a fire-and-forget hub handler where
        // nothing reports it; the symptom is a dialog that has set itself closed and cannot repaint
        // to show it, with the creator still sitting in front of it.
        if (_hasTimings && SongMetadataId is not null)
        {
            var songId = SongMetadataId.Value;

            await InvokeAsync(async () =>
            {
                await CloseAsync();
                await OnTimingCompleted.InvokeAsync(songId);
            });
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
