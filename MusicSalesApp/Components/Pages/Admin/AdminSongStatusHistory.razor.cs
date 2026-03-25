using Microsoft.AspNetCore.Components;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using MusicSalesApp.Common.Helpers;
using Syncfusion.Blazor.Grids;

namespace MusicSalesApp.Components.Pages.Admin;

/// <summary>
/// View model for song status history grid
/// </summary>
public class SongStatusHistoryViewModel
{
    public int Id { get; set; }
    public int SongMetadataId { get; set; }
    public string SongTitle { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public int? CreatorUserId { get; set; }
    public bool IsEnabled { get; set; }
    public string StatusText => IsEnabled ? "Enabled" : "Disabled";
    public string Reason { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; }
    public string ChangedByUserName { get; set; } = string.Empty;
}

public class AdminSongStatusHistoryModel : ComponentBase
{
    [Inject] protected ISongStatusService SongStatusService { get; set; }
    [Inject] protected NavigationManager NavigationManager { get; set; }

    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected List<SongStatusHistoryViewModel> _statusHistory = new();
    protected SfGrid<SongStatusHistoryViewModel> _grid;
    private bool _hasLoadedData = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await LoadStatusHistoryAsync();
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load status history: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task LoadStatusHistoryAsync()
    {
        var history = await SongStatusService.GetAllStatusHistoryAsync();
        
        _statusHistory = history.Select(h => new SongStatusHistoryViewModel
        {
            Id = h.Id,
            SongMetadataId = h.SongMetadataId,
            SongTitle = GetSongTitle(h.SongMetadata),
            CreatorName = GetCreatorName(h.SongMetadata),
            CreatorUserId = h.SongMetadata?.Creator?.UserId,
            IsEnabled = h.IsEnabled,
            Reason = h.Reason ?? string.Empty,
            ChangedAt = h.ChangedAt,
            ChangedByUserName = h.ChangedByUser?.UserName ?? "System"
        }).OrderByDescending(h => h.ChangedAt).ToList();
    }

    private static string GetSongTitle(SongMetadata metadata)
    {
        if (metadata == null)
            return "Unknown Song";
            
        if (!string.IsNullOrEmpty(metadata.SongTitle))
            return metadata.SongTitle;
            
        return Path.GetFileNameWithoutExtension(metadata.Mp3BlobPath ?? metadata.BlobPath ?? "Unknown");
    }

    private static string GetCreatorName(SongMetadata metadata)
    {
        if (metadata?.Creator?.User == null)
            return "Platform Admin";
            
        return metadata.Creator.User.UserName ?? metadata.Creator.User.Email ?? "Unknown Creator";
    }

    protected void NavigateToSongManagement(SongStatusHistoryViewModel item)
    {
        // Navigate to admin song management - the page will show all songs
        // User can search/filter for the specific song
        NavigationManager.NavigateTo($"/admin/songs");
    }

    protected void NavigateToUserManagement(SongStatusHistoryViewModel item)
    {
        if (item.CreatorUserId.HasValue)
        {
            // Navigate to admin user management page
            NavigationManager.NavigateTo($"/admin/users");
        }
    }

    protected async Task ExportToPdf()
    {
        if (_grid == null) return;

        try
        {
            // Configure PDF export to include all filtered data (not just current page)
            var pdfExportProperties = new PdfExportProperties
            {
                FileName = $"SongStatusHistory_{DateTime.Now:yyyyMMdd_HHmmss}.pdf",
                PageOrientation = Syncfusion.Blazor.Grids.PageOrientation.Landscape,
                IncludeTemplateColumn = false, // Exclude Action column with buttons
                ExportType = ExportType.AllPages // Export all pages, respecting filters
            };

            await _grid.ExportToPdfAsync(pdfExportProperties);
        }
        catch (Exception ex)
        {
            _errorMessage = $"Error exporting to PDF: {ex.Message}";
            StateHasChanged();
        }
    }

    protected async Task ExportToExcel()
    {
        if (_grid == null) return;

        try
        {
            // Configure Excel export to include all filtered data (not just current page)
            var excelExportProperties = new ExcelExportProperties
            {
                FileName = $"SongStatusHistory_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
                IncludeTemplateColumn = false, // Exclude Action column with buttons
                ExportType = ExportType.AllPages // Export all pages, respecting filters
            };

            await _grid.ExportToExcelAsync(excelExportProperties);
        }
        catch (Exception ex)
        {
            _errorMessage = $"Error exporting to Excel: {ex.Message}";
            StateHasChanged();
        }
    }
}
