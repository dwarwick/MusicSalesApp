using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Components.Pages.Admin;

public class MediaIntegrityModel : ComponentBase
{
    [SupplyParameterFromQuery(Name = "runId")]
    public int? RequestedRunId { get; set; }
    [Inject] protected IMediaIntegrityAuditService AuditService { get; set; } = default!;
    [Inject] protected AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] protected UserManager<ApplicationUser> UserManager { get; set; } = default!;

    protected MediaAuditMode _selectedMode = MediaAuditMode.ReportOnly;
    protected int? _sourceRunId;
    protected bool _showConfirmation;
    protected bool _starting;
    protected string _message = string.Empty;
    protected bool _isError;
    protected List<MediaIntegrityAuditRun> _runs = [];
    protected MediaIntegrityAuditRun _selectedRun;
    protected List<AuditSourceOption> _sourceOptions = [];
    protected readonly List<AuditModeOption> _modeOptions =
    [
        new(MediaAuditMode.ReportOnly, "Report only"),
        new(MediaAuditMode.RepairSafeMetadata, "Repair safe metadata"),
        new(MediaAuditMode.QuarantineConfirmedFailures, "Quarantine confirmed failures")
    ];

    protected string ModeExplanation => _selectedMode switch
    {
        MediaAuditMode.ReportOnly => "Scans and records evidence. It does not change titles, durations, availability, blobs, or playlists.",
        MediaAuditMode.RepairSafeMetadata => "Scans and fills missing titles or durations only after the playback MP3 proves healthy. It never disables songs.",
        _ => "Uses a reviewed completed run, opens fresh Azure streams, and quarantines only failures confirmed again. No blobs or relationships are deleted."
    };

    protected override async Task OnParametersSetAsync()
    {
        await LoadAsync();
        if (RequestedRunId.HasValue)
        {
            _selectedRun = await AuditService.GetRunAsync(RequestedRunId.Value);
        }
    }

    protected async Task LoadAsync()
    {
        _runs = await AuditService.GetRunsAsync();
        _sourceOptions = _runs
            .Where(run => run.Status == MediaAuditRunStatus.Completed && run.ConfirmedUnplayableCount > 0)
            .Select(run => new AuditSourceOption(run.Id, $"Run {run.Id}: {run.ConfirmedUnplayableCount} confirmed failures"))
            .ToList();
        if (!_sourceRunId.HasValue) _sourceRunId = _sourceOptions.FirstOrDefault()?.Id;
    }

    protected void ShowConfirmation()
    {
        _isError = false;
        _message = string.Empty;
        if (_selectedMode == MediaAuditMode.QuarantineConfirmedFailures && !_sourceRunId.HasValue)
        {
            _isError = true;
            _message = "Select a completed source run with confirmed failures.";
            return;
        }
        _showConfirmation = true;
    }

    protected async Task StartAsync()
    {
        _starting = true;
        _showConfirmation = false;
        try
        {
            var principal = (await AuthenticationStateProvider.GetAuthenticationStateAsync()).User;
            var user = await UserManager.GetUserAsync(principal);
            var run = await AuditService.StartAsync(
                _selectedMode,
                user?.Id,
                user?.Email,
                _selectedMode == MediaAuditMode.QuarantineConfirmedFailures ? _sourceRunId : null);
            _isError = false;
            _message = $"Audit run {run.Id} was queued. Use this page for results and Hangfire for execution/retry details.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _isError = true;
            _message = ex.Message;
        }
        finally
        {
            _starting = false;
        }
    }

    protected async Task SelectRun(int runId) => _selectedRun = await AuditService.GetRunAsync(runId);

    protected sealed record AuditModeOption(MediaAuditMode Mode, string Name);
    protected sealed record AuditSourceOption(int Id, string Label);
}
