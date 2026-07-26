namespace MusicSalesApp.Common.Helpers;

public static class AppSettingKeys
{
    public const string EmailAdminEmail = "EmailSettings:AdminEmail";
    public const string EmailCustomerServiceEmail = "EmailSettings:CustomerServiceEmail";
    public const string MobileExternalAuthCallbackUrl = "MobileExternalAuth:CallbackUrl";
    public const string MobileTipCallbackUrl = "MobileTips:CallbackUrl";
    public const string SyncfusionTheme = "SyncfusionTheme";
    public const string PayPalWebSubscriptionOfferSnapshot = "PayPalWebSubscriptionOffer:Snapshot";
    public const string PayPalAccountManagementUrl = "PayPal:AccountManagementUrl";
    public const string DevelopmentLocalBaseUrl = "Development:LocalBaseUrl";
    public const string AzureStorageConnectionString = "Azure:StorageAccountConnectionString";
    public const string AzureContainerName = "Azure:ContainerName";
    public const string AzurePersonaImageContainerName = "Azure:PersonaImageContainerName";
    public const string AzureDataProtectionContainerName = "Azure:DataProtectionContainerName";
    public const string StorageBackupMaxParallelCopies = "StorageBackup:MaxParallelCopies";
}
