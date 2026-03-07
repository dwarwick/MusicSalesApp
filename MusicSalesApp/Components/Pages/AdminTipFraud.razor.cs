using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using Syncfusion.Blazor.Grids;

#nullable enable

namespace MusicSalesApp.Components.Pages;

/// <summary>
/// View model for blocked tip attempt rows in the admin grid
/// </summary>
public class BlockedTipAttemptViewModel
{
    public int Id { get; set; }
    public string TipperEmail { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public string CreatorEmail { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string FraudRule { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? MachineFingerprint { get; set; }
    public DateTime AttemptedAt { get; set; }
}

public class AdminTipFraudModel : BlazorBase
{
    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected List<BlockedTipAttemptViewModel> _blockedAttempts = new();
    protected List<TipPayoutViewModel> _allowedTips = new();
    protected SfGrid<BlockedTipAttemptViewModel> _blockedGrid = default!;
    protected SfGrid<TipPayoutViewModel> _allowedGrid = default!;
    private bool _hasLoadedData = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await LoadDataAsync();
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load tip fraud data: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task LoadDataAsync()
    {
        // Load blocked attempts
        var blocked = await TipService.GetAllBlockedTipAttemptsAsync();
        _blockedAttempts = blocked.Select(b => new BlockedTipAttemptViewModel
        {
            Id = b.Id,
            TipperEmail = b.TipperUser?.Email ?? $"User #{b.TipperUserId}",
            CreatorName = GetCreatorName(b.Creator, b.CreatorId),
            CreatorEmail = b.Creator?.User?.Email ?? string.Empty,
            Amount = b.Amount,
            FraudRule = b.FraudRule,
            Reason = b.Reason,
            IpAddress = b.IpAddress,
            MachineFingerprint = b.MachineFingerprint,
            AttemptedAt = b.AttemptedAt
        }).ToList();

        // Load allowed tips
        var tips = await TipService.GetAllTipsAsync();
        _allowedTips = tips.Select(t => new TipPayoutViewModel
        {
            Id = t.Id,
            TipperEmail = t.TipperUser?.Email ?? $"User #{t.TipperUserId}",
            ArtistName = GetCreatorName(t.Creator, t.CreatorId),
            ArtistEmail = t.Creator?.User?.Email ?? string.Empty,
            SongTitle = t.SongMetadata?.SongTitle
                ?? (t.SongMetadata?.Mp3BlobPath != null ? Path.GetFileNameWithoutExtension(t.SongMetadata.Mp3BlobPath) : null),
            Amount = t.Amount,
            Status = t.Status.ToString(),
            PayPalTransactionId = !string.IsNullOrEmpty(t.PayPalPayoutTransactionId)
                ? t.PayPalPayoutTransactionId
                : t.PayPalOrderId,
            CreatedAt = t.CreatedAt,
            PaidAt = t.PaidAt
        }).ToList();
    }

    private static string GetCreatorName(Creator? creator, int creatorId)
    {
        if (!string.IsNullOrWhiteSpace(creator?.DisplayName))
            return creator.DisplayName;
        if (!string.IsNullOrWhiteSpace(creator?.User?.Email))
            return creator.User.Email.Split('@')[0];
        return $"Creator #{creatorId}";
    }

    protected async Task ExportBlockedToExcel()
    {
        if (_blockedGrid != null)
        {
            var props = new ExcelExportProperties
            {
                FileName = $"BlockedTipAttempts_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx",
                IncludeTemplateColumn = true
            };
            await _blockedGrid.ExportToExcelAsync(props);
        }
    }

    protected async Task ExportBlockedToPdf()
    {
        if (_blockedGrid != null)
        {
            var props = new PdfExportProperties
            {
                FileName = $"BlockedTipAttempts_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf",
                IncludeTemplateColumn = true
            };
            await _blockedGrid.ExportToPdfAsync(props);
        }
    }

    protected async Task ExportAllowedToExcel()
    {
        if (_allowedGrid != null)
        {
            var props = new ExcelExportProperties
            {
                FileName = $"AllowedTips_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx",
                IncludeTemplateColumn = true
            };
            await _allowedGrid.ExportToExcelAsync(props);
        }
    }

    protected async Task ExportAllowedToPdf()
    {
        if (_allowedGrid != null)
        {
            var props = new PdfExportProperties
            {
                FileName = $"AllowedTips_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf",
                IncludeTemplateColumn = true
            };
            await _allowedGrid.ExportToPdfAsync(props);
        }
    }
}
