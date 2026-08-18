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

    // Audio processing runs against a *different* storage account from the "Azure" one above. That
    // account is Premium and offers no Queue service at all, so the queues and the upload staging
    // container have to live on a Standard general-purpose account.
    //
    // That account is already configured: "AzureLowSpeed" is `musicsalesstorageaccount`. One
    // section per storage account, not per use - so there is exactly one copy of its connection
    // string to rotate. (The name predates this; it describes the tier, which is precisely why it
    // can host queues and the premium account cannot.)
    public const string AzureLowSpeedStorageConnectionString = "AzureLowSpeed:StorageAccountConnectionString";
    public const string AzureLowSpeedUploadStagingContainerName = "AzureLowSpeed:UploadStagingContainerName";
    public const string AzureLowSpeedTranscodeQueueName = "AzureLowSpeed:TranscodeQueueName";
    public const string AzureLowSpeedProbeQueueName = "AzureLowSpeed:ProbeQueueName";
    public const string AzureLowSpeedMatchQueueName = "AzureLowSpeed:MatchQueueName";

    // The lyrics-alignment pipeline runs in a *second* Function app - Python on Linux, because
    // Demucs and the forced aligner are PyTorch, and a Function app is pinned to one language
    // runtime. Unlike the audio pipeline it is invoked over HTTP rather than through a queue: a
    // Durable HTTP starter answers with the orchestration's instance id and its management URLs,
    // which a queue trigger has no way to hand back. That id is what makes the reconciler able to
    // ask Azure how a run is going instead of inferring it from a stale timestamp.
    public const string LyricsFunctionsBaseUrl = "LyricsFunctions:BaseUrl";

    /// <summary>
    /// The Function app's key, presented as <c>x-functions-key</c> when starting an orchestration.
    /// Authorises this app to the Function app - the opposite direction from
    /// <see cref="MediaProcessingApiKey"/>, and therefore a separate secret.
    /// </summary>
    public const string LyricsFunctionsFunctionKey = "LyricsFunctions:FunctionKey";

    /// <summary>
    /// Shared secret the audio-processing Function presents on its callbacks. Top-level, matching
    /// the existing <c>MobileApiKey</c> - it authorises a caller, not a storage account, so it does
    /// not belong inside a storage section.
    /// </summary>
    public const string MediaProcessingApiKey = "MediaProcessingApiKey";
}
