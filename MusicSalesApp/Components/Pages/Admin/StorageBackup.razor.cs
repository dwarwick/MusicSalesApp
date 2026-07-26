using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Pages.Admin;

public partial class StorageBackupModel : BlazorBase, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const string RestoreConfirmationPhrase = "RESTORE";

    protected List<StorageBackupRun> _runs = new();
    protected StorageBackupRun _activeRun;
    /// <summary>Restore-dialog container checkboxes, bound directly by the markup.</summary>
    public List<ContainerSelection> ContainerSelections { get; private set; } = new();

    /// <summary>Typed confirmation phrase guarding the destructive restore action.</summary>
    public string RestoreConfirmation { get; set; } = string.Empty;

    protected List<StorageBackupItemFailure> _failures = new();
    protected int _failuresRunId;

    protected StorageRestoreScope _restoreScope = StorageRestoreScope.MissingAndDiffering;

    /// <summary>
    /// Inverted for the UI: the checkbox reads "do not overwrite newer", while the service takes
    /// <c>overwriteNewerLive</c>. Default off, matching the chosen restore behaviour.
    /// </summary>
    protected bool _preserveNewerLive;

    protected bool _showRestoreDialog;
    protected bool _showFailuresDialog;
    protected string _message;
    protected bool _isError;
    protected bool _busy;

    private readonly CancellationTokenSource _pollCancellation = new();
    private bool _disposed;
    private bool _hasLoadedData;
    private string _lastRenderedSignature;

    protected readonly RestoreScopeOption[] _restoreScopeOptions =
    {
        new() { Scope = StorageRestoreScope.MissingAndDiffering, Name = "Missing and differing files" },
        new() { Scope = StorageRestoreScope.MissingOnly, Name = "Missing files only" }
    };

    protected bool ActionsDisabled => _busy || _activeRun != null;

    public bool CanConfirmRestore
        => ContainerSelections.Any(selection => selection.Selected)
           && string.Equals(RestoreConfirmation?.Trim(), RestoreConfirmationPhrase, StringComparison.Ordinal);

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
            _runs = await StorageBackupService.GetRunsAsync();
            _activeRun = await StorageBackupService.GetActiveRunAsync();
            BuildContainerSelections();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to load storage backup runs");
            SetMessage("Unable to load storage backup history.", isError: true);
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Progress is read from the database rather than pushed. The run state has to be persisted
    /// anyway so a page opened mid-run renders correctly, which leaves a push channel adding
    /// nothing for an audience of one or two admins.
    /// </summary>
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_pollCancellation.Token))
            {
                var active = await StorageBackupService.GetActiveRunAsync();
                var runFinished = _activeRun != null && active == null;
                _activeRun = active;

                if (runFinished)
                {
                    _runs = await StorageBackupService.GetRunsAsync();
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
            // Disposed between the cancellation check and InvokeAsync — expected race; ignore.
            Logger.LogDebug(ex, "Storage backup poll ran after component disposal (expected race condition)");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Storage backup progress polling stopped unexpectedly");
        }
    }

    private static string BuildSignature(StorageBackupRun run)
    {
        if (run == null)
        {
            return "none";
        }

        var containers = string.Join(
            "|",
            run.Containers
                .OrderBy(container => container.Id)
                .Select(container =>
                    $"{container.Id}:{container.Status}:{container.ProcessedCount}:{container.TotalBlobCount}:{container.FailedCount}"));

        return $"{run.Id}:{run.Status}:{containers}";
    }

    protected async Task StartBackupAsync(bool forceFullCopy)
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
            await StorageBackupService.StartBackupAsync(GetUserId(user), user.Identity?.Name, forceFullCopy);
            SetMessage(
                forceFullCopy
                    ? "A full re-copy of every container has been queued."
                    : "An incremental backup has been queued.",
                isError: false);
        }
        catch (InvalidOperationException ex)
        {
            SetMessage(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to queue a storage backup");
            SetMessage("The backup could not be queued.", isError: true);
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync();
    }

    protected void OpenRestoreDialog()
    {
        if (ActionsDisabled)
        {
            return;
        }

        RestoreConfirmation = string.Empty;
        _restoreScope = StorageRestoreScope.MissingAndDiffering;
        _preserveNewerLive = false;
        BuildContainerSelections();
        _showRestoreDialog = true;
    }

    protected async Task StartRestoreAsync()
    {
        if (!CanConfirmRestore)
        {
            return;
        }

        _busy = true;
        try
        {
            var selected = ContainerSelections
                .Where(selection => selection.Selected)
                .Select(selection => selection.Name)
                .ToList();

            var authenticationState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var user = authenticationState.User;

            await StorageBackupService.StartRestoreAsync(
                GetUserId(user),
                user.Identity?.Name,
                selected,
                _restoreScope,
                overwriteNewerLive: !_preserveNewerLive);

            _showRestoreDialog = false;
            SetMessage(
                "A restore has been queued. It repairs files only — re-run the Media Integrity Audit afterwards "
                + "so that any quarantined songs are re-evaluated.",
                isError: false);
        }
        catch (InvalidOperationException ex)
        {
            SetMessage(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to queue a storage restore");
            SetMessage("The restore could not be queued.", isError: true);
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
            await StorageBackupService.RequestCancellationAsync(_activeRun.Id);
            SetMessage("Cancellation requested. Copies already in flight will finish first.", isError: false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to cancel storage backup run {RunId}", _activeRun.Id);
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
        _failures = new List<StorageBackupItemFailure>();
        try
        {
            var run = await StorageBackupService.GetRunAsync(runId);
            if (run?.Failures != null)
            {
                _failures = run.Failures.OrderBy(failure => failure.Id).ToList();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unable to load failures for storage backup run {RunId}", runId);
        }

        _showFailuresDialog = true;
        await InvokeAsync(StateHasChanged);
    }

    private void BuildContainerSelections()
    {
        var configured = StorageBackupService.GetConfiguredContainerNames();
        var previouslySelected = ContainerSelections
            .Where(selection => selection.Selected)
            .Select(selection => selection.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        ContainerSelections = configured
            .Select(name => new ContainerSelection
            {
                Name = name,
                // Nothing is pre-selected; restore is destructive and must be chosen deliberately.
                Selected = previouslySelected.Contains(name)
            })
            .ToList();
    }

    private void SetMessage(string message, bool isError)
    {
        _message = message;
        _isError = isError;
    }

    // ---------------- View helpers ----------------

    protected static IEnumerable<StorageBackupContainerProgress> OrderedContainers(StorageBackupRun run)
        => run.Containers.OrderBy(container => container.Id);

    protected static string ContainerLabel(StorageBackupContainerProgress container, StorageBackupDirection direction)
        => direction == StorageBackupDirection.Backup
            ? $"{container.SourceContainerName} → {container.DestinationContainerName}"
            : $"{container.DestinationContainerName} → {container.SourceContainerName}";

    protected static string ContainerCounts(StorageBackupContainerProgress container)
    {
        var counts = $"{container.ProcessedCount} / {container.TotalBlobCount}"
            + $" · copied {container.CopiedCount} · skipped {container.SkippedCount}";

        if (container.SkippedNewerLiveCount > 0)
        {
            counts += $" ({container.SkippedNewerLiveCount} newer)";
        }

        if (container.FailedCount > 0)
        {
            counts += $" · failed {container.FailedCount}";
        }

        return counts + $" · {FormatBytes(container.CopiedBytes)}";
    }

    /// <summary>
    /// The total is unknown until listing finishes, so the bar runs indeterminate until then
    /// rather than sitting at zero.
    /// </summary>
    protected static bool IsIndeterminate(StorageBackupContainerProgress container)
        => container.Status == StorageBackupContainerStatus.Listing
           || (container.Status == StorageBackupContainerStatus.Copying && container.TotalBlobCount == 0);

    protected static double PercentComplete(StorageBackupContainerProgress container)
    {
        if (container.Status is StorageBackupContainerStatus.Completed
            or StorageBackupContainerStatus.Skipped)
        {
            return 100;
        }

        if (container.TotalBlobCount <= 0)
        {
            return 0;
        }

        // The container can change between the counting pass and the copy pass, so clamp.
        var percent = container.ProcessedCount * 100d / container.TotalBlobCount;
        return Math.Clamp(percent, 0, 100);
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

    public sealed class ContainerSelection
    {
        public string Name { get; set; }
        public bool Selected { get; set; }
    }

    protected sealed class RestoreScopeOption
    {
        public StorageRestoreScope Scope { get; set; }
        public string Name { get; set; }
    }
}
