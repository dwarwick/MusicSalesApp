#nullable enable
using Azure;
using Azure.Storage.Blobs.Models;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// The few things the upload pipeline needs to do to a blob the browser has already written.
///
/// <para>
/// Exists as an interface for one reason: <c>BlobContainerClient</c> cannot be usefully mocked, and
/// without a seam here the size cap and the header sniff on a staged upload would be verifiable only
/// against real storage. Those are the two checks standing between a creator and the catalogue once
/// the bytes stop passing through the web server, so they are exactly the ones worth testing.
/// </para>
/// </summary>
public interface IStagedBlobReader
{
    /// <summary>The blob's size, or null if it does not exist.</summary>
    Task<long?> GetLengthAsync(string blobPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The first <paramref name="byteCount"/> bytes, or null if the blob is missing.
    ///
    /// <para>
    /// A ranged read, so proving a 60 MB upload is really audio costs 64 bytes rather than a
    /// download. Returned as an array rather than a stream because the blob download stream is not
    /// seekable, and <c>AudioContainerSniffer</c> rewinds only when it can - it happens to work at
    /// position 0, but relying on that would be fragile.
    /// </para>
    /// </summary>
    Task<byte[]?> ReadHeaderAsync(string blobPath, int byteCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a blob within the staging container.
    ///
    /// <para>
    /// Same account, so this is a server-side copy: no bytes travel through the web host. Used to
    /// move a matched cover image out of its batch folder and into the song's folder, which cannot
    /// happen at upload time because which song an image belongs to is unknown until after matching.
    /// </para>
    /// </summary>
    Task CopyWithinStagingAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class StagedBlobReader : IStagedBlobReader
{
    private readonly IBlobContainerFactory _containerFactory;

    public StagedBlobReader(IBlobContainerFactory containerFactory)
    {
        _containerFactory = containerFactory;
    }

    private Azure.Storage.Blobs.BlobContainerClient Staging
        => _containerFactory.GetUploadStagingContainer()
            ?? throw new InvalidOperationException("Upload staging is not configured.");

    /// <inheritdoc />
    public async Task<long?> GetLengthAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var properties = await Staging.GetBlobClient(blobPath).GetPropertiesAsync(cancellationToken: cancellationToken);
            return properties.Value.ContentLength;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadHeaderAsync(
        string blobPath,
        int byteCount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Staging.GetBlobClient(blobPath).DownloadStreamingAsync(
                new BlobDownloadOptions { Range = new HttpRange(0, byteCount) },
                cancellationToken);

            using var buffer = new MemoryStream();
            await response.Value.Content.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task CopyWithinStagingAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var source = Staging.GetBlobClient(sourcePath);
        var destination = Staging.GetBlobClient(destinationPath);

        var operation = await destination.StartCopyFromUriAsync(source.Uri, cancellationToken: cancellationToken);
        await operation.WaitForCompletionAsync(cancellationToken);
    }
}
