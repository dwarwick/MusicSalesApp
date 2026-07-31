using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Pages.Admin;

public partial class ImageVariantBackfillModel : BlazorBase, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected List<ImageVariantBackfillRun> _runs = new();
    protected ImageVariantBackfillRun _activeRun;
    protected IReadOnlyList<string> _targetContainers = Array.Empty<string>();

    protected List<ImageVariantBackfillItemFailure> _failures = new();
    protected int _failuresRunId;

    protected ImageVariantBackfillScope _scope = ImageVariantBackfillScope.All;
    protected bool _removeLegacyPngSharingImages = true;

    /// <summary>Typed confirmation guarding the full-regeneration action.</summary>
    public string RegenerateConfirmation { get; set; } = string.Empty;

    protected bool _showRegenerateDialog;
    protected bool _showFailuresDialog;
    protected string _message;
    protected bool _isError;
    protected bool _busy;

    private readonly CancellationTokenSource _pollCancellation = new();
    private bool _disposed;
    private bool _hasLoadedData;
    private string _lastRenderedSignature;

    protected readonly ScopeOption[] _scopeOptions =
    {
        new() { Scope = ImageVariantBackfillScope.All, Name = "Cover art and persona images" },
        new() { Scope = ImageVariantBackfillScope.CoverArtOnly, Name = "Cover art only" },
        new() { Scope = ImageVariantBackfillScope.PersonaOnly, Name = "Persona images only" }
    };

    protected bool ActionsDisabled => _busy || _activeRun != null;

    public bool CanConfirmRegenerate
        => string.Equals(
            RegenerateConfirmation?.Trim(),
            ImageVariantBackfillConfirmation.RegeneratePhrase,
            StringComparison.Ordinal);

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
            _runs = await ImageVariantBackfillService.GetRunsAsync();
            _activeRun = await ImageVariantBackfillService.GetActiveRunAsync();
            _targetContainers = ImageVariantBackfillService.GetTargetContainerNames();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to load image-variant backfill runs");
            SetMessage("Unable to load the backfill history.", isError: true);
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Progress is polled from the database rather than pushed, for the same reason as the storage
    /// backup page: the run state has to be persisted anyway so a page opened mid-run renders
    /// correctly, which leaves a push channel adding nothing for an audience of one or two admins.
    /// </summary>
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_pollCancellation.Token))
            {
                var active = await ImageVariantBackfillService.GetActiveRunAsync();
                var runFinished = _activeRun != null && active == null;
                _activeRun = active;

                if (runFinished)
                {
                    _runs = await ImageVariantBackfillService.GetRunsAsync();
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
            Logger.LogDebug(ex, "Image-variant backfill poll ran after component disposal (expected race condition)");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Image-variant backfill progress polling stopped unexpectedly");
        }
    }

    private static string BuildSignature(ImageVariantBackfillRun run)
        => run == null
            ? "none"
            : $"{run.Id}:{run.Status}:{run.ProcessedCount}:{run.TotalItemCount}:{run.FailedCount}:{run.VariantBlobCount}";

    protected Task StartDryRunAsync() => StartAsync(dryRun: true, regenerateExisting: false);

    protected Task StartGenerateMissingAsync() => StartAsync(dryRun: false, regenerateExisting: false);

    protected void OpenRegenerateDialog()
    {
        if (ActionsDisabled)
        {
            return;
        }

        RegenerateConfirmation = string.Empty;
        _showRegenerateDialog = true;
    }

    protected async Task StartRegenerateAsync()
    {
        if (!CanConfirmRegenerate)
        {
            return;
        }

        _showRegenerateDialog = false;
        await StartAsync(dryRun: false, regenerateExisting: true);
    }

    private async Task StartAsync(bool dryRun, bool regenerateExisting)
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

            await ImageVariantBackfillService.StartAsync(
                _scope,
                dryRun,
                regenerateExisting,
                _removeLegacyPngSharingImages,
                GetUserId(user),
                user.Identity?.Name);

            SetMessage(
                dryRun
                    ? "A dry run has been queued. It decodes and re-encodes every image but writes nothing, "
                      + "so its duration and byte total are an honest rehearsal of the real run."
                    : "A backfill has been queued. When it finishes, run a Storage Backup so the new renditions "
                      + "reach the backup containers.",
                isError: false);
        }
        catch (InvalidOperationException ex)
        {
            SetMessage(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to queue an image-variant backfill");
            SetMessage("The backfill could not be queued.", isError: true);
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync();
    }

    protected async Task CancelRunAsync()
    {
        if (_activeRun == null)
        {
            return;
        }

        _busy = true;
        try
        {
            await ImageVariantBackfillService.RequestCancellationAsync(_activeRun.Id);
            SetMessage("Cancellation requested. The image being processed will finish first.", isError: false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to cancel image-variant backfill run {RunId}", _activeRun.Id);
            SetMessage("The run could not be cancelled.", isError: true);
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync();
    }

    protected async Task ViewFailuresAsync(int runId)
    {
        _failuresRunId = runId;
        _failures = new List<ImageVariantBackfillItemFailure>();
        try
        {
            var run = await ImageVariantBackfillService.GetRunAsync(runId);
            if (run?.Failures != null)
            {
                _failures = run.Failures.OrderBy(failure => failure.Id).ToList();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to load failures for image-variant backfill run {RunId}", runId);
        }

        _showFailuresDialog = true;
        await InvokeAsync(StateHasChanged);
    }

    private void SetMessage(string message, bool isError)
    {
        _message = message;
        _isError = isError;
    }

    // ---------------- View helpers ----------------

    /// <summary>
    /// The total is only known once the work list has been built, so the bar runs indeterminate until
    /// then rather than sitting at zero - as it does while the run is still waiting for Hangfire to
    /// pick it up, where a static 0% is indistinguishable from a stuck job.
    /// </summary>
    protected static bool IsIndeterminate(ImageVariantBackfillRun run)
        => run.Status == ImageVariantBackfillStatus.Queued
           || (run.Status == ImageVariantBackfillStatus.Running && run.TotalItemCount == 0);

    protected static double PercentComplete(ImageVariantBackfillRun run)
    {
        if (run.Status is ImageVariantBackfillStatus.Completed or ImageVariantBackfillStatus.CompletedWithErrors)
        {
            return 100;
        }

        if (run.TotalItemCount <= 0)
        {
            return 0;
        }

        return Math.Clamp(run.ProcessedCount * 100d / run.TotalItemCount, 0, 100);
    }

    protected static string RunCounts(ImageVariantBackfillRun run)
    {
        var counts = $"{run.ProcessedCount} / {run.TotalItemCount}"
            + $" · {run.GeneratedCount} images · {run.VariantBlobCount} renditions";

        if (run.FailedCount > 0)
        {
            counts += $" · failed {run.FailedCount}";
        }

        if (run.UndersizedSourceCount > 0)
        {
            counts += $" · {run.UndersizedSourceCount} undersized";
        }

        if (run.LegacySharingImagesRemoved > 0)
        {
            counts += $" · {run.LegacySharingImagesRemoved} stale share images removed";
        }

        return counts + $" · {FormatBytes(run.BytesWritten)}";
    }

    protected static string ScopeLabel(ImageVariantBackfillScope scope) => scope switch
    {
        ImageVariantBackfillScope.CoverArtOnly => "Cover art",
        ImageVariantBackfillScope.PersonaOnly => "Personas",
        _ => "All images"
    };

    protected static string ModeLabel(ImageVariantBackfillRun run)
    {
        if (run.DryRun) return "Dry run";
        return run.RegenerateExisting ? "Regenerate all" : "Generate missing";
    }

    protected static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        var megabytes = bytes / 1024d / 1024d;
        return megabytes >= 1024
            ? $"{megabytes / 1024d:0.0} GB"
            : $"{megabytes:0.0} MB";
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

    protected sealed class ScopeOption
    {
        public ImageVariantBackfillScope Scope { get; set; }
        public string Name { get; set; }
    }
}
