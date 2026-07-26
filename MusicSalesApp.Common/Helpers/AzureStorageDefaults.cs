namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// Default Azure Storage container names. These exist so the fallback used when configuring
/// Data Protection at startup and the default on AzureStorageOptions are provably the same
/// string — otherwise the backup job could target a different container than the app actually uses.
/// </summary>
public static class AzureStorageDefaults
{
    public const string DataProtectionContainerName = "dataprotection-keys";
}
