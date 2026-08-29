using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using MusicSalesApp.Helpers;

namespace MusicSalesApp.Components.Pages.Admin;

public partial class HlsPackagingBackfillModel : BlazorBase, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    protected List<HlsPackagingBackfillRun> _runs = new();
    protected HlsPackagingBackfillRun _activeRun;
    protected HlsPackagingBackfillRun _selectedRun;
    protected IReadOnlyList<string> _targetContainers = Array.Empty<string>();

    protected HlsPackagingBackfillScope _scope = HlsPackagingBackfillScope.Missing;

    protected string _message;
    protected bool _isError;
    protected bool _busy;

    private readonly CancellationTokenSource _pollCancellation = new();
    private bool _disposed;
    private bool _hasLoadedData;
    private string _lastRenderedSignature;

    protected readonly ScopeOption[] _scopeOptions =
    {
        new() { Scope = HlsPackagingBackfillScope.Missing, Name = "Songs never packaged" },
        new() { Scope = HlsPackagingBackfillScope.RepairMissing, Name = "Repair packages missing from storage" },
        new() { Scope = HlsPackagingBackfillScope.RepackageAll, Name = "Repackage everything" }
    };

    protected bool ActionsDisabled => _busy || _activeRun != null;

    public class ScopeOption
    {
        public HlsPackagingBackfillScope Scope { get; set; }
        public string Name { get; set; }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _hasLoadedData)
        {
            return;
        }

        _hasLoadedData = true;
        await LoadAsync();
        _ = PollAsync();
    }

    protected async Task LoadAsync()
    {
        try
        {
            _runs = await HlsPackagingBackfillService.GetRunsAsync();
            _activeRun = await HlsPackagingBackfillService.GetActiveRunAsync();
            _targetContainers = HlsPackagingBackfillService.GetTargetContainerNames();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to load HLS packaging runs");
            SetMessage("Unable to load the packaging history.", isError: true);
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Progress is polled from the database rather than pushed, matching the image backfill and the
    /// storage backup pages.
    ///
    /// <para>
    /// A slightly slower interval than those two, because this run's progress does not come from
    /// this process at all: it advances as callbacks arrive from the Function app, which for a large
    /// catalogue is minutes of work per song. Polling faster would only re-read the same row.
    /// </para>
    /// </summary>
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_pollCancellation.Token))
            {
                var active = await HlsPackagingBackfillService.GetActiveRunAsync();
                var runFinished = _activeRun != null && active == null;
                _activeRun = active;

                if (runFinished)
                {
                    _runs = await HlsPackagingBackfillService.GetRunsAsync();
                }

                var signature = BuildSignature(active);
                if (!runFinished && signature == _lastRenderedSignature)
                {
                    continue;
                }

                _lastRenderedSignature = signature;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
            // The page was disposed; nothing to do.
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "HLS packaging poll ran after component disposal (expected race condition)");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "HLS packaging progress polling stopped unexpectedly");
        }
    }

    private static string BuildSignature(HlsPackagingBackfillRun run)
        => run == null
            ? "none"
            : $"{run.Id}:{run.Status}:{run.DispatchedCount}:{run.TotalItemCount}:{run.SucceededCount}:{run.FailedCount}";

    protected Task StartDryRunAsync() => StartAsync(dryRun: true);

    protected Task StartRunAsync() => StartAsync(dryRun: false);

    private async Task StartAsync(bool dryRun)
    {
        if (ActionsDisabled)
        {
            return;
        }

        _busy = true;
        try
        {
            var authenticationState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var user = authenticationState.User;

            await HlsPackagingBackfillService.StartAsync(
                _scope,
                dryRun,
                GetUserId(user),
                user.Identity?.Name);

            SetMessage(
                dryRun
                    ? "A dry run has been queued. It selects and counts the work but queues nothing, so it "
                      + "costs no Function time and tells you how many songs are outstanding."
                    : "Packaging has been queued. It runs in the Azure Function app, so this page will show "
                      + "\"Awaiting callbacks\" while results come back. When it finishes, run a Storage Backup "
                      + "so the packages have a recoverable copy.",
                isError: false);
        }
        catch (InvalidOperationException ex)
        {
            SetMessage(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to start an HLS packaging run");
            SetMessage("Unable to start the run.", isError: true);
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync();
    }

    protected async Task CancelRunAsync()
    {
        if (_activeRun == null || _busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await HlsPackagingBackfillService.RequestCancellationAsync(_activeRun.Id);
            SetMessage(
                "No further songs will be queued. Messages already on the queue will still be processed.",
                isError: false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to cancel HLS packaging run {RunId}", _activeRun.Id);
            SetMessage("Unable to cancel the run.", isError: true);
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync();
    }

    protected async Task ShowFailuresAsync(int runId)
    {
        try
        {
            _selectedRun = await HlsPackagingBackfillService.GetRunAsync(runId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to load failures for HLS packaging run {RunId}", runId);
            SetMessage("Unable to load that run's failures.", isError: true);
        }

        await InvokeAsync(StateHasChanged);
    }

    protected static string ModeLabel(HlsPackagingBackfillRun run)
        => run.DryRun ? "Dry run" : "Packaging";

    protected static string ScopeLabel(HlsPackagingBackfillScope scope) => scope switch
    {
        HlsPackagingBackfillScope.Missing => "Songs never packaged",
        HlsPackagingBackfillScope.RepairMissing => "Repair missing packages",
        HlsPackagingBackfillScope.RepackageAll => "Repackage everything",
        _ => scope.ToString()
    };

    protected static string ScopeHelp(HlsPackagingBackfillScope scope) => scope switch
    {
        HlsPackagingBackfillScope.Missing =>
            "The one-off pass. Selects songs with no package recorded, which is what makes it safe to "
            + "re-run after an interruption - it picks up only what is still outstanding.",

        HlsPackagingBackfillScope.RepairMissing =>
            "For after a restore. Selects songs whose row records a package that storage does not "
            + "actually hold - the state a blob restore leaves when the streaming container did not "
            + "come back, where the database looks healthy but nothing plays. Costs one storage check "
            + "per song, so it is a deliberate choice rather than the default.",

        HlsPackagingBackfillScope.RepackageAll =>
            "Repackages every playable song from scratch, for a codec or segment-length change. Each "
            + "song gets a new package folder, so the existing one keeps serving until the new one is "
            + "recorded and nothing goes off the air mid-run.",

        _ => string.Empty
    };

    protected static string StatusLabel(HlsPackagingBackfillStatus status) => status switch
    {
        HlsPackagingBackfillStatus.Queued => "Queued",
        HlsPackagingBackfillStatus.Dispatching => "Queueing songs",
        HlsPackagingBackfillStatus.AwaitingCallbacks => "Awaiting callbacks",
        HlsPackagingBackfillStatus.Completed => "Completed",
        HlsPackagingBackfillStatus.Failed => "Failed",
        HlsPackagingBackfillStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    protected static string RunCounts(HlsPackagingBackfillRun run)
    {
        if (run.DryRun)
        {
            return $"{run.TotalItemCount} song(s) would be packaged";
        }

        return $"{run.SucceededCount} packaged, {run.FailedCount} failed, "
            + $"{run.OutstandingCount} outstanding of {run.TotalItemCount} selected";
    }

    protected static double PercentComplete(HlsPackagingBackfillRun run)
    {
        if (run.TotalItemCount <= 0)
        {
            return 0;
        }

        var settled = run.SucceededCount + run.FailedCount;
        return ProgressBarValue.ForDisplay(settled * 100d / run.TotalItemCount);
    }

    /// <summary>
    /// True while there is no meaningful denominator yet — the selection query is still running, so a
    /// zero-width bar would read as "stuck" rather than "counting".
    /// </summary>
    protected static bool IsIndeterminate(HlsPackagingBackfillRun run)
        => run.TotalItemCount <= 0 && run.Status != HlsPackagingBackfillStatus.Completed;

    protected void SetMessage(string message, bool isError)
    {
        _message = message;
        _isError = isError;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _pollCancellation.CancelAsync();
        _pollCancellation.Dispose();
    }

    /// <summary>Wall-clock time for the run, or a dash while it is still going.</summary>
    protected static string FormatElapsed(HlsPackagingBackfillRun run)
    {
        if (run.DryRun)
        {
            return "-";
        }

        var elapsed = run.Elapsed;
        return elapsed.HasValue ? FormatDuration(elapsed.Value) : "-";
    }

    /// <summary>
    /// Mean Function time per packaged song.
    ///
    /// <para>
    /// This, not the elapsed column, is the number to project a larger run from. Elapsed depends on
    /// how many songs Azure happened to be running at once, which for a short run is mostly
    /// scale-out ramp.
    /// </para>
    /// </summary>
    protected static string FormatPerSong(HlsPackagingBackfillRun run)
        => run.AverageProcessingSeconds is { } seconds
            ? FormatDuration(TimeSpan.FromSeconds(seconds))
            : "-";

    /// <summary>How many songs were being packaged simultaneously, on average.</summary>
    protected static string FormatConcurrency(HlsPackagingBackfillRun run)
        => run.ObservedConcurrency is { } concurrency
            ? concurrency.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x"
            : "-";

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalSeconds < 60)
        {
            return value.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}h {value.Minutes}m"
            : $"{value.Minutes}m {value.Seconds}s";
    }

    /// <summary>
    /// Projects how long a run of <paramref name="songCount"/> songs would take, from what the most
    /// recent completed run actually measured.
    ///
    /// <para>
    /// Deliberately not <c>elapsed / songs * songCount</c>. A small run is dominated by Azure's
    /// scale-out ramp - the platform adds Consumption instances gradually, and each new one
    /// cold-starts a worker carrying a 144 MB package with ffmpeg in it - so a ten-song run can look
    /// almost serial while a five-hundred-song run reaches its steady-state concurrency early and
    /// keeps it. Projecting from wall clock would therefore overestimate badly.
    /// </para>
    ///
    /// <para>
    /// Instead it uses the per-song cost, which is a property of the work rather than of the
    /// scaling, and divides by the concurrency this environment actually achieved. Still an
    /// estimate: a longer run typically does better than a short one, so this errs high.
    /// </para>
    /// </summary>
    protected string ProjectFor(int songCount)
    {
        var reference = _runs?.FirstOrDefault(r =>
            !r.DryRun
            && r.Status == HlsPackagingBackfillStatus.Completed
            && r.AverageProcessingSeconds.HasValue
            && r.ObservedConcurrency.HasValue);

        if (reference?.AverageProcessingSeconds is not { } perSong
            || reference.ObservedConcurrency is not { } concurrency
            || concurrency <= 0)
        {
            return null;
        }

        return FormatDuration(TimeSpan.FromSeconds(songCount * perSong / concurrency));
    }
}
