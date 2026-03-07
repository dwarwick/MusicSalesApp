using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using Syncfusion.Blazor.Grids;

#nullable enable

namespace MusicSalesApp.Components.Pages;

/// <summary>
/// View model for tip payout rows in the admin grid
/// </summary>
public class TipPayoutViewModel
{
    public int Id { get; set; }
    public string Type { get; set; } = "Tip";
    public string TipperEmail { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string ArtistEmail { get; set; } = string.Empty;
    public string? SongTitle { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? PayPalTransactionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
}

/// <summary>
/// View model for stream payout rows in the admin grid
/// </summary>
public class StreamPayoutViewModel
{
    public int Id { get; set; }
    public string Type { get; set; } = "Stream";
    public string ArtistName { get; set; } = string.Empty;
    public string ArtistEmail { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    public int StreamCount { get; set; }
    public decimal PayoutAmount { get; set; }
    public decimal WithheldAmount { get; set; }
    public string? PayPalTransactionId { get; set; }
    public DateTime PaymentDate { get; set; }
}

public class AdminPayoutsModel : BlazorBase
{
    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected List<TipPayoutViewModel> _tipPayouts = new();
    protected List<StreamPayoutViewModel> _streamPayouts = new();
    protected SfGrid<TipPayoutViewModel> _tipGrid = default!;
    protected SfGrid<StreamPayoutViewModel> _streamGrid = default!;
    private bool _hasLoadedData = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await LoadPayoutsAsync();
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load payouts: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task LoadPayoutsAsync()
    {
        // Load tips
        var tips = await TipService.GetAllTipsAsync();
        _tipPayouts = tips.Select(t => new TipPayoutViewModel
        {
            Id = t.Id,
            TipperEmail = t.TipperUser?.Email ?? $"User #{t.TipperUserId}",
            ArtistName = GetCreatorName(t.Creator, t.CreatorId),
            ArtistEmail = t.Creator?.User?.Email ?? string.Empty,
            SongTitle = GetSongTitle(t.SongMetadata),
            Amount = t.Amount,
            Status = t.Status.ToString(),
            PayPalTransactionId = !string.IsNullOrEmpty(t.PayPalPayoutTransactionId)
                ? t.PayPalPayoutTransactionId
                : t.PayPalOrderId,
            CreatedAt = t.CreatedAt,
            PaidAt = t.PaidAt
        }).ToList();

        // Load stream payouts
        var payouts = await StreamPayoutService.GetAllPayoutsAsync();
        _streamPayouts = payouts.Select(p => new StreamPayoutViewModel
        {
            Id = p.Id,
            ArtistName = GetCreatorName(p.Creator, p.CreatorId),
            ArtistEmail = p.Creator?.User?.Email ?? string.Empty,
            SongTitle = GetSongTitle(p.SongMetadata) ?? "Unknown",
            StreamCount = p.NumberOfStreams,
            PayoutAmount = p.NetAmount,
            WithheldAmount = p.WithheldAmount,
            PayPalTransactionId = p.PayPalTransactionId,
            PaymentDate = p.PaymentDate
        }).ToList();
    }

    private static string? GetSongTitle(SongMetadata? metadata)
    {
        if (metadata == null) return null;
        return metadata.SongTitle
            ?? (metadata.Mp3BlobPath != null
                ? Path.GetFileNameWithoutExtension(metadata.Mp3BlobPath)
                : null);
    }

    private static string GetCreatorName(Creator? creator, int creatorId)
    {
        if (creator?.DisplayName != null)
            return creator.DisplayName;
        if (creator?.User?.Email != null)
            return creator.User.Email.Split('@')[0];
        return $"Creator #{creatorId}";
    }

    protected async Task ExportTipsToExcel()
    {
        if (_tipGrid != null)
        {
            var props = new ExcelExportProperties
            {
                FileName = $"TipPayouts_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx",
                IncludeTemplateColumn = true
            };
            await _tipGrid.ExportToExcelAsync(props);
        }
    }

    protected async Task ExportTipsToPdf()
    {
        if (_tipGrid != null)
        {
            var props = new PdfExportProperties
            {
                FileName = $"TipPayouts_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf",
                IncludeTemplateColumn = true
            };
            await _tipGrid.ExportToPdfAsync(props);
        }
    }

    protected async Task ExportStreamsToExcel()
    {
        if (_streamGrid != null)
        {
            var props = new ExcelExportProperties
            {
                FileName = $"StreamPayouts_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx",
                IncludeTemplateColumn = true
            };
            await _streamGrid.ExportToExcelAsync(props);
        }
    }

    protected async Task ExportStreamsToPdf()
    {
        if (_streamGrid != null)
        {
            var props = new PdfExportProperties
            {
                FileName = $"StreamPayouts_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf",
                IncludeTemplateColumn = true
            };
            await _streamGrid.ExportToPdfAsync(props);
        }
    }
}
