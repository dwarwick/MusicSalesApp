using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

public class MobileAppInstallOptions
{
    public bool Enabled { get; set; }
    public string AndroidPackageName { get; set; } = "net.streamtunes.musicsalesapp.maui";
    public string GooglePlayUrl { get; set; } = string.Empty;
    public string AppleAppStoreId { get; set; } = string.Empty;
    public string AppleAppStoreUrl { get; set; } = string.Empty;
    public string StreamTunesIconUrl { get; set; } = "/images/logo-light-small.png";
    public string GooglePlayBadgeUrl { get; set; } = string.Empty;
    public string AppleAppStoreBadgeUrl { get; set; } = string.Empty;
    public string AndroidFallbackMode { get; set; } = MobileAppInstallFallbackModes.Hide;
    public bool ShowIosFallbackBanner { get; set; }
}