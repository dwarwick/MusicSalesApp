#nullable enable
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using Syncfusion.Blazor.Grids;

namespace MusicSalesApp.Components.Pages.Admin;

public partial class AdminReportedSongsModel : BlazorBase
{
    protected bool _loading = true;
    protected string? _error;
    protected List<ReportedSongViewModel> _reports = [];
    protected SfGrid<ReportedSongViewModel>? _grid;
    private bool _hasLoadedData;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await LoadReportsAsync();
            }
            catch (Exception ex)
            {
                _error = $"Error loading reports: {ex.Message}";
                Logger.LogError(ex, "Error loading reported songs");
            }
            finally
            {
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task LoadReportsAsync()
    {
        var reports = await ReportedSongService.GetAllReportsAsync();

        _reports = reports.Select(r => new ReportedSongViewModel
        {
            Id = r.Id,
            SongTitle = r.SongMetadata == null
                ? "Unknown"
                : SongTitleHelper.GetEffectiveTitle(
                    r.SongMetadata.SongTitle,
                    r.SongMetadata.Mp3BlobPath,
                    r.SongMetadata.BlobPath),
            CreatorEmail = GetCreatorEmail(r),
            ReporterEmail = r.ReportingUser?.Email ?? "Unknown",
            Reason = r.Reason,
            CreatedAt = r.CreatedAt,
            ResolutionDateTime = r.ResolutionDateTime,
            ResolutionStatus = r.ResolutionAccepted switch
            {
                true => "Accepted",
                false => "Rejected",
                null => "Pending"
            },
            SongStatus = (r.SongMetadata?.IsEnabled ?? false) ? "Active" : "Inactive"
        }).ToList();
    }

    private static string GetCreatorEmail(ReportedSong report)
    {
        // Creator navigation: SongMetadata -> Creator -> User
        // The Creator entity has a UserId FK, but the User nav property
        // may not be loaded. We need a different approach.
        // Since GetAllReportsAsync includes Creator, check if it has user info.
        return report.SongMetadata?.Creator?.User?.Email ?? "Unknown";
    }

    protected async Task ResolveReport(int reportId, bool accepted)
    {
        try
        {
            var success = await ReportedSongService.ResolveReportAsync(reportId, accepted);
            if (success)
            {
                await LoadReportsAsync();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error resolving report {ReportId}", reportId);
        }
    }

    protected class ReportedSongViewModel
    {
        public int Id { get; set; }
        public string SongTitle { get; set; } = string.Empty;
        public string CreatorEmail { get; set; } = string.Empty;
        public string ReporterEmail { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? ResolutionDateTime { get; set; }
        public string ResolutionStatus { get; set; } = "Pending";
        public string SongStatus { get; set; } = "Active";
    }
}
