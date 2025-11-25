namespace MusicSalesApp.Services
{
    public class AzureStorageOptions
    {
        public string StorageAccountName { get; set; }
        public string StorageAccountKey { get; set; } // Prefer Managed Identity; key only for local dev.
        public string StorageAccountConnectionString { get; set; }
        public string ContainerName { get; set; }
    }
}