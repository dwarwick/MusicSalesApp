namespace MusicSalesApp.Common.Helpers;

public static class GoogleAdsTrackingConfigKeys
{
    public const string SectionName = "GoogleAdsTracking";
    public const string Enabled = SectionName + ":Enabled";
    public const string TagId = SectionName + ":TagId";
    public const string CreatorSignupConversionLabel = SectionName + ":CreatorSignupConversionLabel";
    public const string GoogleTagManagerId = SectionName + ":GoogleTagManagerId";
    public const string EnabledHosts = SectionName + ":EnabledHosts";

    public const string TrackConversionFunctionName = "streamtunesGoogleAds.trackConversion";
    public const string CreatorSignupTransactionIdPrefix = "creator-";
}
