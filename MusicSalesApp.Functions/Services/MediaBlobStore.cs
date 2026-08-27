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
/// a Standard general-purpose account.
/// </para>
///
/// <para>
/// <b>Staging is read/write. Media is read/write for derived artefacts only.</b> This app writes the
/// WebP renditions of a song's cover art directly into the song's GUID folder, because it has the
/// decoded bitmap in hand and shipping it back for the web app to re-download and re-decode would
/// double the work. It writes <b>nothing else</b>: the playback MP3, the original audio, the
/// cover-art master and the original cover art are all still copied in by
/// <c>MediaProcessingCompletionService</c>, and this app still has no database access.
/// </para>
///
/// <para>
/// The rule is therefore <b>"no primary blob, no row"</b> rather than "no write" — nothing here may
/// create, overwrite or delete a blob a <c>SongMetadata</c> row already points at. That is the
/// invariant the older "media is read-only" wording was standing in for.
/// </para>
/// </summary>
public interface IMediaBlobStore
{
    /// <summary>Downloads a staged upload to a local path.</summary>
    Task DownloadStagedAsync(string blobPath, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a staged blob into memory, or null if it is missing.
    ///
    /// <para>
    /// In memory rather than to disk because the only caller hands the bytes straight to an HTTP
    /// request, and a temp file would exist only to be read back immediately. Bounded by the upload
    /// page's own image size cap, and only a few are ever held at once.
    /// </para>
    /// </summary>
    Task<byte[]> TryReadStagedAsync(string blobPath, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Writes a <b>derived</b> artefact into the media container — in practice a cover-art
    /// rendition, whose path is the master's path plus <c>.w{width}.webp</c>.
    ///
    /// <para>
    /// Overwrites unconditionally, which is what makes a queue redelivery idempotent: the paths are
    /// a pure function of the job GUID and the cover-art extension, so a second attempt rewrites
    /// byte-identical content rather than colliding.
    /// </para>
    /// </summary>
    Task UploadMediaAsync(
        string blobPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one file of an encrypted HLS package into the streaming container.
    ///
    /// <para>
    /// Only ciphertext produced by <c>IHlsPackager</c> and the manifest describing it may be written
    /// here. The container is private, but its contents are handed to listeners under a container
    /// read SAS, so anything unencrypted put here goes straight to whoever is listening.
    /// </para>
    ///
    /// <para>
    /// Overwrites unconditionally, which is what makes a queue redelivery idempotent: the folder is
    /// the <c>HlsStreamId</c> the web app minted and put in the message, so a second attempt
    /// rewrites the same paths rather than orphaning a folder per try.
    /// </para>
    /// </summary>
    Task UploadStreamingAsync(
        string blobPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class MediaBlobStore : IMediaBlobStore
{
    private readonly Lazy<BlobContainerClient> _staging;
    private readonly Lazy<BlobContainerClient> _media;
    private readonly Lazy<BlobContainerClient> _streaming;

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

        // Same account as _media, different container. Lazy for the same reason the others are: a
        // settings mistake on the streaming container must not break the upload path, which never
        // touches it.
        _streaming = new Lazy<BlobContainerClient>(() =>
        {
            var opts = options.Value;
            return new BlobContainerClient(opts.MediaStorageConnectionString, opts.StreamingContainerName);
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
    public async Task<byte[]> TryReadStagedAsync(
        string blobPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var buffer = new MemoryStream();
            await _staging.Value.GetBlobClient(blobPath).DownloadToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
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

    /// <inheritdoc />
    public async Task UploadMediaAsync(
        string blobPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        content.Position = 0;
        await _media.Value.GetBlobClient(blobPath).UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task UploadStreamingAsync(
        string blobPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        content.Position = 0;
        await _streaming.Value.GetBlobClient(blobPath).UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
            },
            cancellationToken);
    }
}
