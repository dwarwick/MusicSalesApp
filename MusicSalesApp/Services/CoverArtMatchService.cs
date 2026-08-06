#nullable enable
using Azure.Storage.Blobs.Models;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>One candidate image the upload page holds on local disk, ready to be staged.</summary>
/// <param name="FileName">The creator's own filename — half of what matching compares.</param>
/// <param name="TempPath">Where the page buffered it after receiving it from the browser.</param>
public sealed record CoverArtMatchCandidateSource(string FileName, string TempPath);

/// <summary>
/// Stages a batch of candidate cover art and asks the Azure Function to pair it with the audio
/// uploaded beside it.
///
/// <para>
/// Lives here rather than on the upload page because the page holds no blob-client code at all, and
/// <see cref="SongUploadJobService"/> is the precedent for "the page hands over files, this decides
/// where they land".
/// </para>
/// </summary>
public interface ICoverArtMatchService
{
    /// <summary>True when the matching queue is configured. False means the caller must match locally.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Uploads each candidate to the batch's staging folder and enqueues the pairing request.
    /// </summary>
    /// <param name="stagingProgress">Reports 0-100 across the whole batch as the images upload.</param>
    Task StageAndEnqueueAsync(
        Guid batchId,
        int creatorId,
        IReadOnlyList<string> audioFileNames,
        IReadOnlyList<CoverArtMatchCandidateSource> images,
        IProgress<double>? stagingProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a batch's staged images once the pairing is no longer needed.
    ///
    /// <para>
    /// Best effort, and never throws. The staging container's lifecycle rule sweeps anything left
    /// behind, so a failure here costs a few days of storage rather than correctness — but a
    /// fifty-image batch is worth reclaiming promptly rather than leaving for a week.
    /// </para>
    /// </summary>
    Task DeleteBatchAsync(Guid batchId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class CoverArtMatchService : ICoverArtMatchService
{
    private readonly IBlobContainerFactory _containerFactory;
    private readonly IMediaProcessingQueueClient _queueClient;
    private readonly ILogger<CoverArtMatchService> _logger;

    public CoverArtMatchService(
        IBlobContainerFactory containerFactory,
        IMediaProcessingQueueClient queueClient,
        ILogger<CoverArtMatchService> logger)
    {
        _containerFactory = containerFactory;
        _queueClient = queueClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable
        => _queueClient.IsCoverArtMatchConfigured
            && _containerFactory.GetUploadStagingContainer() is not null;

    /// <inheritdoc />
    public async Task StageAndEnqueueAsync(
        Guid batchId,
        int creatorId,
        IReadOnlyList<string> audioFileNames,
        IReadOnlyList<CoverArtMatchCandidateSource> images,
        IProgress<double>? stagingProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioFileNames);
        ArgumentNullException.ThrowIfNull(images);

        var staging = _containerFactory.GetUploadStagingContainer()
            ?? throw new InvalidOperationException("Upload staging is not configured.");

        await staging.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

        var candidates = new List<CoverArtMatchCandidate>(images.Count);

        for (var index = 0; index < images.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var image = images[index];
            var extension = Path.GetExtension(image.FileName);
            var blobPath = MediaProcessingStagingPaths.MatchBatchImage(batchId, index, extension);
            var contentType = MusicFileExtensions.GetCoverArtContentType(image.FileName);

            await using (var content = File.OpenRead(image.TempPath))
            {
                await staging.GetBlobClient(blobPath).UploadAsync(
                    content,
                    new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
                    },
                    cancellationToken);
            }

            candidates.Add(new CoverArtMatchCandidate
            {
                Index = index,
                FileName = image.FileName,
                BlobPath = blobPath,
                ContentType = contentType
            });

            // Per file rather than per byte. These are images, so each one finishes quickly and the
            // extra resolution would only cost render passes on the circuit.
            stagingProgress?.Report((index + 1) * 100d / images.Count);
        }

        await _queueClient.EnqueueCoverArtMatchAsync(
            new CoverArtMatchRequest
            {
                BatchId = batchId,
                CreatorId = creatorId,
                AudioFileNames = audioFileNames,
                Images = candidates
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteBatchAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        try
        {
            var staging = _containerFactory.GetUploadStagingContainer();
            if (staging is null)
            {
                return;
            }

            var prefix = MediaProcessingStagingPaths.MatchBatchFolder(batchId) + "/";

            await foreach (var blob in staging.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, prefix, cancellationToken))
            {
                try
                {
                    await staging.GetBlobClient(blob.Name)
                        .DeleteIfExistsAsync(cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex, "Could not delete staged match image {Path} for batch {BatchId}", blob.Name, batchId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean up the staged images for match batch {BatchId}", batchId);
        }
    }
}
