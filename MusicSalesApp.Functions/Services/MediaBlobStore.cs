using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace MusicSalesApp.Functions.Services;

/// <summary>Blob properties this app cares about, flattened so callers need no Azure types.</summary>
public sealed record MediaBlobProperties(
    bool Exists,
    long? Length,
    string ETag,
    string ContentType,
    DateTimeOffset? LastModified);

/// <summary>
/// Reads and writes the two storage accounts this app touches.
///
/// <para>
/// <b>There are two, and they are not interchangeable.</b> Song media lives on a Premium account,
/// which offers no Queue service at all, so the queues and the upload staging container had to go on
/// a Standard general-purpose account. Staging is read/write; media is read-only from here — the web
/// app owns every write into the catalogue.
/// </para>
/// </summary>
public interface IMediaBlobStore
{
    /// <summary>Downloads a staged upload to a local path.</summary>
    Task DownloadStagedAsync(string blobPath, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>Uploads the produced playback MP3 back to staging.</summary>
    Task UploadStagedAsync(
        string blobPath,
        string sourcePath,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>Downloads a playback blob from the media account for probing. Returns false if absent.</summary>
    Task<bool> TryDownloadMediaAsync(
        string blobPath,
        string destinationPath,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a media blob's properties without downloading it.</summary>
    Task<MediaBlobProperties> GetMediaPropertiesAsync(string blobPath, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class MediaBlobStore : IMediaBlobStore
{
    private readonly Lazy<BlobContainerClient> _staging;
    private readonly Lazy<BlobContainerClient> _media;

    /// <remarks>
    /// Both clients are built on first use rather than here. The two accounts serve different
    /// functions - staging for uploads, media for probes - and a settings mistake on one has no
    /// business breaking the other. Constructing both eagerly meant a missing
    /// <c>MediaStorageConnectionString</c> threw inside the DI container and took down the upload
    /// path, which never touches the media account.
    /// </remarks>
    public MediaBlobStore(IOptions<FunctionOptions> options)
    {
        _staging = new Lazy<BlobContainerClient>(() =>
        {
            var opts = options.Value;
            return new BlobContainerClient(opts.StagingStorageConnectionString, opts.StagingContainerName);
        });

        _media = new Lazy<BlobContainerClient>(() =>
        {
            var opts = options.Value;
            return new BlobContainerClient(opts.MediaStorageConnectionString, opts.MediaContainerName);
        });
    }

    /// <inheritdoc />
    public async Task DownloadStagedAsync(
        string blobPath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        await _staging.Value.GetBlobClient(blobPath).DownloadToAsync(destinationPath, cancellationToken);
    }

    /// <inheritdoc />
    public async Task UploadStagedAsync(
        string blobPath,
        string sourcePath,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(sourcePath);
        await _staging.Value.GetBlobClient(blobPath).UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TryDownloadMediaAsync(
        string blobPath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _media.Value.GetBlobClient(blobPath).DownloadToAsync(destinationPath, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // A registered blob that no longer exists is a finding for the audit, not a fault here.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<MediaBlobProperties> GetMediaPropertiesAsync(
        string blobPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var properties = await _media.Value.GetBlobClient(blobPath).GetPropertiesAsync(cancellationToken: cancellationToken);
            return new MediaBlobProperties(
                Exists: true,
                Length: properties.Value.ContentLength,
                ETag: properties.Value.ETag.ToString(),
                ContentType: properties.Value.ContentType,
                LastModified: properties.Value.LastModified);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new MediaBlobProperties(false, null, null, null, null);
        }
    }
}
