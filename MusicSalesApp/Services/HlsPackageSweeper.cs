#nullable enable
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// Deletes an encrypted-HLS package folder that nothing points at any more.
///
/// <para>
/// Necessary because the streaming container has <b>no lifecycle rule</b>, and cannot safely be
/// given one — its prefixes are the live catalogue. The media container has the same constraint,
/// which is why <c>MediaProcessingCompletionService</c> already sweeps orphaned cover-art renditions
/// by hand on the failure path. This is the audio equivalent.
/// </para>
///
/// <para>
/// Orphans arise from two ordinary events, not just from bugs: repackaging a song mints a new folder
/// and abandons the old one, and a packaging run that fails after uploading some segments leaves a
/// partial folder behind. Neither is reachable — the row names one folder and one only — so without
/// a sweep they would sit there forever, paying storage on a catalogue's worth of audio nobody can
/// play.
/// </para>
/// </summary>
public interface IHlsPackageSweeper
{
    /// <summary>
    /// Deletes every blob under one package folder. Never throws: a sweep is cleanup, and failing
    /// the caller over it would turn a tidy-up problem into a lost song or a failed callback.
    /// </summary>
    Task SweepAsync(Guid hlsStreamId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class HlsPackageSweeper : IHlsPackageSweeper
{
    private readonly IBlobContainerFactory _containerFactory;
    private readonly ILogger<HlsPackageSweeper> _logger;

    public HlsPackageSweeper(IBlobContainerFactory containerFactory, ILogger<HlsPackageSweeper> logger)
    {
        _containerFactory = containerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SweepAsync(Guid hlsStreamId, CancellationToken cancellationToken = default)
    {
        if (hlsStreamId == Guid.Empty)
        {
            return;
        }

        var prefix = HlsPackagePaths.Folder(hlsStreamId) + "/";

        try
        {
            var container = _containerFactory.GetStreamingContainer();
            var deleted = 0;

            await foreach (var blob in container.GetBlobsAsync(
                               Azure.Storage.Blobs.Models.BlobTraits.None,
                               Azure.Storage.Blobs.Models.BlobStates.None,
                               prefix,
                               cancellationToken))
            {
                await container.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: cancellationToken);
                deleted++;
            }

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Swept {Count} blobs from superseded HLS package {StreamId}",
                    deleted,
                    hlsStreamId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not sweep HLS package {StreamId}. Its blobs are unreachable but still stored.",
                hlsStreamId);
        }
    }
}
