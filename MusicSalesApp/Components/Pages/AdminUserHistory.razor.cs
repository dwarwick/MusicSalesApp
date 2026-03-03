using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using Syncfusion.Blazor.Grids;

#nullable enable

namespace MusicSalesApp.Components.Pages;

/// <summary>
/// View model for user history grid
/// </summary>
public class UserHistoryViewModel
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime OccurredAt { get; set; }
}

public class AdminUserHistoryModel : BlazorBase
{
    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected List<UserHistoryViewModel> _userHistory = new();
    protected SfGrid<UserHistoryViewModel> _grid = default!;
    private bool _hasLoadedData = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await LoadUserHistoryAsync();
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load user history: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task LoadUserHistoryAsync()
    {
        var history = await AdminNotificationService.GetAllUserHistoryAsync();
        
        _userHistory = history.Select(h => new UserHistoryViewModel
        {
            Id = h.Id,
            UserId = h.UserId,
            UserEmail = h.UserEmail,
            EventType = h.EventType,
            Description = h.Description,
            OldValue = h.OldValue,
            NewValue = h.NewValue,
            OccurredAt = h.OccurredAt
        }).ToList();
    }

    protected async Task ExportToExcel()
    {
        if (_grid != null)
        {
            var excelExportProperties = new ExcelExportProperties
            {
                FileName = $"UserHistory_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx",
                IncludeTemplateColumn = true
            };
            await _grid.ExportToExcelAsync(excelExportProperties);
        }
    }

    protected async Task ExportToPdf()
    {
        if (_grid != null)
        {
            var pdfExportProperties = new PdfExportProperties
            {
                FileName = $"UserHistory_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf",
                IncludeTemplateColumn = true
            };
            await _grid.ExportToPdfAsync(pdfExportProperties);
        }
    }
}
