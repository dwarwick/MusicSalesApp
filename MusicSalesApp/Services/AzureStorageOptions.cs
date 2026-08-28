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

        /// <summary>
        /// The container holding encrypted HLS packages - the manifests and AES-128 segments the
        /// player streams. Same storage account as <see cref="ContainerName"/>.
        ///
        /// <para>
        /// <b>Private</b>, like every other container here. Its contents are ciphertext and
        /// worthless without the per-song key the API gates; segment URLs carry a container read
        /// SAS stamped on by the manifest builder. This is what lets segment bytes go straight from
        /// storage to the listener instead of through the SmarterASP shared host.
        /// </para>
        ///
        /// <para>
        /// It was going to be public, for credential-free stable URLs. Both storage accounts set
        /// <c>allowBlobPublicAccess: false</c>, and that guardrail is worth more - this account
        /// holds every song master and the Data Protection key rings, Production included.
        /// </para>
        /// </summary>
        public string StreamingContainerName { get; set; } = MusicSalesApp.Common.Helpers.MediaProcessingContainers.StreamingProduction;
    }
}
