#nullable enable
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MusicSalesApp.Common.Helpers;
using SkiaSharp;

namespace MusicSalesApp.Services;

/// <inheritdoc />
public sealed class ImageVariantService : IImageVariantService
{
    private readonly IBlobContainerFactory _containerFactory;
    private readonly ILogger<ImageVariantService> _logger;

    public ImageVariantService(IBlobContainerFactory containerFactory, ILogger<ImageVariantService> logger)
    {
        _containerFactory = containerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<ImageVariantResult> GenerateCoverArtVariantsAsync(
        string coverArtBlobPath,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
        => GenerateAsync(
            _containerFactory.GetMediaContainer(),
            coverArtBlobPath,
            ImageVariantSizes.CoverArt,
            dryRun,
            cancellationToken);

    /// <inheritdoc />
    public Task<ImageVariantResult> GeneratePersonaVariantsAsync(
        string personaBlobPath,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
        => GenerateAsync(
            _containerFactory.GetPersonaImageContainer(),
            personaBlobPath,
            ImageVariantSizes.Persona,
            dryRun,
            cancellationToken);

    /// <inheritdoc />
    public Task DeleteCoverArtVariantsAsync(
        string coverArtBlobPath,
        IEnumerable<int> widths,
        CancellationToken cancellationToken = default)
        => DeleteAsync(_containerFactory.GetMediaContainer(), coverArtBlobPath, widths, cancellationToken);

    /// <inheritdoc />
    public Task DeletePersonaVariantsAsync(
        string personaBlobPath,
        IEnumerable<int> widths,
        CancellationToken cancellationToken = default)
        => DeleteAsync(_containerFactory.GetPersonaImageContainer(), personaBlobPath, widths, cancellationToken);

    private async Task<ImageVariantResult> GenerateAsync(
        BlobContainerClient container,
        string baseBlobPath,
        IReadOnlyList<int> ladder,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseBlobPath))
            return ImageVariantResult.Failed(ImageVariantFailureCodes.NoSourcePath);

        SKBitmap? source;
        try
        {
            source = await DownloadAndDecodeAsync(container, baseBlobPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the master image {BlobPath} to generate renditions", baseBlobPath);
            return ImageVariantResult.Failed(ImageVariantFailureCodes.SourceMissing);
        }

        if (source == null)
            return ImageVariantResult.Failed(ImageVariantFailureCodes.DecodeFailed);

        using (source)
        {
            var sourceWidth = source.Width;
            var sourceHeight = source.Height;
            var widths = ImageVariantEncoder.SelectWidths(ladder, sourceWidth);

            var generated = new List<int>();
            long bytesWritten = 0;

            foreach (var width in widths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var encoded = ImageVariantEncoder.EncodeWebp(source, width);
                if (encoded == null || encoded.Length == 0)
                {
                    _logger.LogWarning(
                        "Encoding the {Width}px rendition of {BlobPath} produced nothing", width, baseBlobPath);
                    continue;
                }

                bytesWritten += encoded.Length;

                // A dry run still decodes, resamples and encodes — only the upload is skipped. That
                // makes both its byte estimate and its duration a true rehearsal of the real run.
                if (!dryRun)
                {
                    try
                    {
                        await UploadVariantAsync(container, baseBlobPath, width, encoded, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex, "Could not upload the {Width}px rendition of {BlobPath}", width, baseBlobPath);
                        return ImageVariantResult.Failed(ImageVariantFailureCodes.UploadFailed);
                    }
                }

                generated.Add(width);
            }

            if (generated.Count == 0)
                return ImageVariantResult.Failed(ImageVariantFailureCodes.EncodeFailed);

            return new ImageVariantResult(
                Succeeded: true,
                GeneratedWidths: generated,
                SourceWidth: sourceWidth,
                SourceHeight: sourceHeight,
                BytesWritten: bytesWritten,
                FailureReason: null);
        }
    }

    private static async Task<SKBitmap?> DownloadAndDecodeAsync(
        BlobContainerClient container,
        string blobPath,
        CancellationToken cancellationToken)
    {
        var blobClient = container.GetBlobClient(blobPath);

        using var buffer = new MemoryStream();
        await blobClient.DownloadToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
            return null;

        buffer.Position = 0;

        // Decoded once and reused for every rung: a 3000x3000 source is ~36 MB of pixels, and
        // re-decoding per rendition would multiply both the time and the peak memory.
        try
        {
            return SKBitmap.Decode(buffer);
        }
        catch
        {
            return null;
        }
    }

    private static async Task UploadVariantAsync(
        BlobContainerClient container,
        string baseBlobPath,
        int width,
        Stream content,
        CancellationToken cancellationToken)
    {
        var variantPath = ImageVariantPaths.Variant(baseBlobPath, width);
        var blobClient = container.GetBlobClient(variantPath);

        content.Position = 0;
        await blobClient.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = ImageVariantPaths.VariantContentType }
            },
            cancellationToken);
    }

    private async Task DeleteAsync(
        BlobContainerClient container,
        string baseBlobPath,
        IEnumerable<int> widths,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseBlobPath) || widths == null)
            return;

        foreach (var variantPath in ImageVariantPaths.VariantsFor(baseBlobPath, widths))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await container.GetBlobClient(variantPath).DeleteIfExistsAsync(cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A rendition left behind is wasted storage, not a correctness problem: nothing
                // links to it once the width set is rewritten. Never fail the caller over it.
                _logger.LogWarning(ex, "Could not delete the superseded rendition {BlobPath}", variantPath);
            }
        }
    }
}
