using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IStorageBackupBlobGateway"/>.
/// All copies are server-side, so blob content never passes through the web server.
/// </summary>
public sealed class StorageBackupBlobGateway : IStorageBackupBlobGateway
{
    private static readonly TimeSpan CopyPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(10);

    private readonly BlobServiceClient _serviceClient;
    private readonly ILogger<StorageBackupBlobGateway> _logger;

    public StorageBackupBlobGateway(
        IOptions<AzureStorageOptions> options,
        ILogger<StorageBackupBlobGateway> logger)
    {
        _logger = logger;

        // Degrade rather than throw: a misconfigured box should surface an error on the admin
        // page, not fail DI resolution and take the page down. Mirrors CreatorPersonaService.
        var connectionString = options?.Value?.StorageAccountConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _serviceClient = null;
            _logger.LogWarning(
                "Azure storage connection string is not configured. Storage backup and restore are unavailable.");
            return;
        }

        _serviceClient = new BlobServiceClient(connectionString);
    }

    public bool IsConfigured => _serviceClient != null;

    public async Task<bool> ContainerExistsAsync(string containerName, CancellationToken cancellationToken)
    {
        var container = GetContainer(containerName);
        return await container.ExistsAsync(cancellationToken);
    }

    public async Task EnsureContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        var container = GetContainer(containerName);

        // Explicitly private. Never inherit whatever access level the source container carries.
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<StorageBlobSnapshot> ListAsync(
        string containerName,
        bool includeTags,
        bool includeMetadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var container = GetContainer(containerName);

        var traits = BlobTraits.None;
        if (includeTags) traits |= BlobTraits.Tags;
        if (includeMetadata) traits |= BlobTraits.Metadata | BlobTraits.CopyStatus;

        var options = new GetBlobsOptions { Traits = traits };
        await foreach (var blob in container.GetBlobsAsync(options, cancellationToken))
        {
            yield return ToSnapshot(blob);
        }
    }

    public string CreateContainerReadSasQuery(string containerName, TimeSpan lifetime)
    {
        var container = GetContainer(containerName);

        // Requires a shared-key credential. The app authenticates with an account-key connection
        // string, so this holds; the guard makes the failure legible if that ever changes.
        if (!container.CanGenerateSasUri)
        {
            throw new InvalidOperationException(
                $"Cannot generate a SAS for container '{containerName}'. "
                + "Storage backup requires a key-based connection string.");
        }

        var sasUri = container.GenerateSasUri(
            BlobContainerSasPermissions.Read,
            DateTimeOffset.UtcNow.Add(lifetime));

        return sasUri.Query.TrimStart('?');
    }

    public async Task CopyAsync(
        string sourceContainerName,
        string destinationContainerName,
        string blobName,
        string sourceSasQuery,
        IDictionary<string, string> metadata,
        IDictionary<string, string> tags,
        CancellationToken cancellationToken)
    {
        var sourceBlob = GetContainer(sourceContainerName).GetBlobClient(blobName);
        var destinationBlob = GetContainer(destinationContainerName).GetBlobClient(blobName);

        var sourceUri = new UriBuilder(sourceBlob.Uri) { Query = sourceSasQuery }.Uri;

        var options = new BlobCopyFromUriOptions { Metadata = metadata };

        // Copy Blob does not carry index tags across. Reapplying them matters because
        // AzureStorageService.ListFilesByAlbumAsync resolves album covers via FindBlobsByTags.
        if (tags != null && tags.Count > 0)
        {
            options.Tags = tags;
        }

        var operation = await destinationBlob.StartCopyFromUriAsync(sourceUri, options, cancellationToken);

        // Same-account copies almost always report completion on the initial response, so this
        // usually costs no extra requests at all.
        if (operation.HasCompleted)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CopyTimeout);

        try
        {
            await operation.WaitForCompletionAsync(CopyPollInterval, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TryAbortCopyAsync(destinationBlob, operation.Id, cancellationToken);
            throw new TimeoutException(
                $"Copying '{blobName}' into '{destinationContainerName}' did not complete within {CopyTimeout.TotalMinutes:0} minutes.");
        }
    }

    private async Task TryAbortCopyAsync(BlobClient destinationBlob, string copyId, CancellationToken cancellationToken)
    {
        try
        {
            await destinationBlob.AbortCopyFromUriAsync(copyId, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            // The copy may have completed in the meantime; nothing useful to do either way.
            _logger.LogWarning(ex, "Unable to abort timed-out copy {CopyId} for blob {BlobName}",
                copyId, destinationBlob.Name);
        }
    }

    private static StorageBlobSnapshot ToSnapshot(BlobItem blob)
    {
        var properties = blob.Properties;

        return new StorageBlobSnapshot(
            blob.Name,
            properties?.ETag?.ToString(),
            properties?.ContentLength ?? 0,
            properties?.LastModified,
            properties?.ContentHash == null ? null : Convert.ToBase64String(properties.ContentHash),
            properties?.CopyStatus?.ToString(),
            AsReadOnly(blob.Metadata),
            AsReadOnly(blob.Tags));
    }

    private static IReadOnlyDictionary<string, string> AsReadOnly(IDictionary<string, string> source)
        => source switch
        {
            null => null,
            IReadOnlyDictionary<string, string> readOnly => readOnly,
            _ => new Dictionary<string, string>(source)
        };

    private BlobContainerClient GetContainer(string containerName)
    {
        if (_serviceClient == null)
        {
            throw new InvalidOperationException(
                "Azure storage is not configured. Set Azure:StorageAccountConnectionString to enable storage backup.");
        }

        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw new ArgumentException("Container name is required.", nameof(containerName));
        }

        return _serviceClient.GetBlobContainerClient(containerName);
    }
}
