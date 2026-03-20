namespace MusicSalesApp.Services
{
    public class AzureStorageOptions
    {
        public string StorageAccountName { get; set; }
        public string StorageAccountKey { get; set; } // Prefer Managed Identity; key only for local dev.
        public string StorageAccountConnectionString { get; set; }
        public string ContainerName { get; set; }

        /// <summary>
        /// The Azure Blob Storage container name for creator persona profile images.
        /// Defaults to "persona-images" if not configured.
        /// </summary>
        public string PersonaImageContainerName { get; set; } = "persona-images";
    }
}
