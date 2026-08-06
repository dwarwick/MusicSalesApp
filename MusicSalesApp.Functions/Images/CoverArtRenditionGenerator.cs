#nullable enable
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Functions.Services;
using MusicSalesApp.Imaging;
using SkiaSharp;

namespace MusicSalesApp.Functions.Images;

/// <summary>
/// What one cover art's rendition pass produced.
/// </summary>
/// <param name="Widths">
/// The rendition widths actually written, ascending. <b>Empty</b> means art was present but nothing
/// could be produced — never null, because the caller distinguishes "no art at all" by not calling
/// this in the first place.
/// </param>
/// <param name="SourceWidth">Decoded pixel width, or null if it never decoded.</param>
/// <param name="SourceHeight">Decoded pixel height, or null if it never decoded.</param>
/// <param name="DiagnosticCode">
/// An <see cref="ImageVariantFailureCodes"/> constant when <paramref name="Widths"/> is empty or
/// short, otherwise null. Diagnostic only — it is never shown to the creator.
/// </param>
public sealed record CoverArtRenditionResult(
    IReadOnlyList<int> Widths,
    int? SourceWidth,
    int? SourceHeight,
    string? DiagnosticCode)
{
    public static CoverArtRenditionResult Failed(string diagnosticCode)
        => new(Array.Empty<int>(), null, null, diagnosticCode);
}

/// <summary>
/// Builds a song's cover-art renditions from the staged master and writes them into the song's
/// folder in the media container.
/// </summary>
public interface ICoverArtRenditionGenerator
{
    /// <summary>
    /// Downloads the staged cover art to <paramref name="localCoverPath"/>, decodes it once, and
    /// writes one WebP rendition per rung the source can fill.
    ///
    /// <para>
    /// <b>Never throws except <see cref="OperationCanceledException"/>.</b> Renditions are derived
    /// data the admin backfill can rebuild at any time, and a song with no recorded widths serves
    /// its full-size master — the behaviour that predates renditions entirely. So every failure here
    /// comes back as an empty result with a diagnostic code, and the song still publishes. This is
    /// deliberately <i>not</i> the three-way playable/unplayable/inconclusive rule the audio path
    /// uses; see the throw-vs-report section of this project's CLAUDE.md for why.
    /// </para>
    /// </summary>
    /// <param name="localCoverPath">
    /// Scratch path for the download. Owned by the caller, whose <c>finally</c> deletes it — the
    /// same arrangement the audio source and transcode paths use.
    /// </param>
    Task<CoverArtRenditionResult> GenerateAsync(
        Guid jobId,
        string stagedCoverBlobPath,
        string coverArtExtension,
        string localCoverPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class CoverArtRenditionGenerator : ICoverArtRenditionGenerator
{
    private readonly IMediaBlobStore _blobStore;
    private readonly ILogger<CoverArtRenditionGenerator> _logger;

    public CoverArtRenditionGenerator(
        IMediaBlobStore blobStore,
        ILogger<CoverArtRenditionGenerator> logger)
    {
        _blobStore = blobStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CoverArtRenditionResult> GenerateAsync(
        Guid jobId,
        string stagedCoverBlobPath,
        string coverArtExtension,
        string localCoverPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stagedCoverBlobPath) || string.IsNullOrWhiteSpace(coverArtExtension))
            return CoverArtRenditionResult.Failed(ImageVariantFailureCodes.NoSourcePath);

        try
        {
            await _blobStore.DownloadStagedAsync(stagedCoverBlobPath, localCoverPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately not a terminal job failure, even for a 404. Assembly is about to try to
            // copy this very blob and already knows how to unwind and leave the job retryable;
            // pre-empting that decision here would fail a song over a thumbnail.
            _logger.LogWarning(
                ex, "Could not read the staged cover art {BlobPath} for job {JobId}", stagedCoverBlobPath, jobId);
            return CoverArtRenditionResult.Failed(ImageVariantFailureCodes.SourceMissing);
        }

        // The renditions hang off where the master will live once assembly copies it in, which is
        // derived through the same helper the API uses - not passed along as a second precomputed
        // string that would have to stay byte-identical for the public whitelist to resolve it back.
        var masterMediaPath = SongMediaPaths.CoverArt(jobId, coverArtExtension);

        try
        {
            using var source = DecodeOrNull(localCoverPath);
            if (source is null)
            {
                // The web app already decode-validated this image before staging it, so reaching
                // here means storage corruption or a SkiaSharp disagreement - not the creator's fault.
                _logger.LogWarning(
                    "SkiaSharp could not decode the cover art for job {JobId} ({BlobPath})",
                    jobId,
                    stagedCoverBlobPath);
                return CoverArtRenditionResult.Failed(ImageVariantFailureCodes.DecodeFailed);
            }

            var sourceWidth = source.Width;
            var sourceHeight = source.Height;
            var widths = ImageVariantEncoder.SelectWidths(ImageVariantSizes.CoverArt, sourceWidth);

            var generated = new List<int>();
            string? diagnosticCode = null;

            for (var i = 0; i < widths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var width = widths[i];

                // Every rung is drawn from the one decoded source. Re-decoding per rung would
                // multiply both the time and the peak heap, which on a 1.5 GB Consumption instance
                // is sitting alongside an FFmpeg transcode.
                using var encoded = ImageVariantEncoder.EncodeWebp(source, width);
                if (encoded is null || encoded.Length == 0)
                {
                    _logger.LogWarning(
                        "Encoding the {Width}px rendition for job {JobId} produced nothing", width, jobId);
                    diagnosticCode = ImageVariantFailureCodes.EncodeFailed;
                    continue;
                }

                try
                {
                    await _blobStore.UploadMediaAsync(
                        ImageVariantPaths.Variant(masterMediaPath, width),
                        encoded,
                        ImageVariantPaths.VariantContentType,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Keep whatever already landed rather than discarding the run. A partial ladder
                    // still serves every surface whose rung exists, and the rest fall back to the
                    // master exactly as they would with no renditions at all.
                    _logger.LogWarning(
                        ex, "Could not write the {Width}px rendition for job {JobId}", width, jobId);
                    return new CoverArtRenditionResult(
                        generated, sourceWidth, sourceHeight, ImageVariantFailureCodes.UploadFailed);
                }

                generated.Add(width);
                progress?.Report((i + 1) * 100d / widths.Count);
            }

            if (generated.Count == 0)
                diagnosticCode ??= ImageVariantFailureCodes.EncodeFailed;

            return new CoverArtRenditionResult(generated, sourceWidth, sourceHeight, diagnosticCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Generating cover-art renditions for job {JobId} failed", jobId);
            return CoverArtRenditionResult.Failed(ImageVariantFailureCodes.Unexpected);
        }
    }

    /// <remarks>
    /// Decoded from the file rather than from a buffered copy, so the compressed bytes and the
    /// decoded bitmap are never both on the heap. A 3000x3000 source is already ~36 MB decoded.
    /// </remarks>
    private static SKBitmap? DecodeOrNull(string localCoverPath)
    {
        try
        {
            using var stream = File.OpenRead(localCoverPath);
            return SKBitmap.Decode(stream);
        }
        catch
        {
            return null;
        }
    }
}
