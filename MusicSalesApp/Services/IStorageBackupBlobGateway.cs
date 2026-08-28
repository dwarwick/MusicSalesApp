using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// The Azure Blob Storage seam used by <see cref="IStorageBackupService"/>.
/// <para>
/// This exists because <c>BlobServiceClient</c> and <c>Pageable&lt;BlobItem&gt;</c> cannot be mocked,
/// so the orchestration logic could not otherwise be tested. It also lets the service address
/// arbitrary containers, which <see cref="IAzureStorageService"/> cannot — that one is a singleton
/// hard-bound to a single <c>BlobContainerClient</c> built in its constructor.
/// </para>
/// </summary>
public interface IStorageBackupBlobGateway
{
    /// <summary>True when a usable storage connection string is configured.</summary>
    bool IsConfigured { get; }

    Task<bool> ContainerExistsAsync(string containerName, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the container if absent. Always private — never inherits the source container's
    /// access level.
    ///
    /// <para>
    /// That applies to the encrypted-HLS streaming container too. It was going to be the one public
    /// container in the product, but both storage accounts set <c>allowBlobPublicAccess: false</c>
    /// and that guardrail is worth keeping: the premium account holds every song master and the
    /// Data Protection key rings, for Production as well as Test. Segments carry a container read
    /// SAS instead (<see cref="IHlsSegmentSasProvider"/>), so there is nothing here to choose.
    /// </para>
    /// </summary>
    Task EnsureContainerAsync(string containerName, CancellationToken cancellationToken);

    /// <summary>
    /// Streams a container listing. Never materialises the whole container, so it is safe for
    /// containers holding tens of thousands of blobs.
    /// </summary>
    IAsyncEnumerable<StorageBlobSnapshot> ListAsync(
        string containerName,
        bool includeTags,
        bool includeMetadata,
        CancellationToken cancellationToken);

    /// <summary>
    /// Generates one container-scoped read SAS query string, reused for every blob in that
    /// container for the lifetime of a run. Generated locally, so this costs zero requests.
    /// </summary>
    string CreateContainerReadSasQuery(string containerName, TimeSpan lifetime);

    /// <summary>
    /// Server-side copy of a single blob. No bytes traverse the app server.
    /// </summary>
    Task CopyAsync(
        string sourceContainerName,
        string destinationContainerName,
        string blobName,
        string sourceSasQuery,
        IDictionary<string, string> metadata,
        IDictionary<string, string> tags,
        CancellationToken cancellationToken);
}
