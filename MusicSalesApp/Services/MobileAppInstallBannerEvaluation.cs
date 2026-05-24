namespace MusicSalesApp.Services;

public sealed class MobileAppInstallBannerEvaluation
{
    public bool ShowBanner { get; set; }
    public string Platform { get; set; } = string.Empty;
    public bool IsPromotional { get; set; }
}