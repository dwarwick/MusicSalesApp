using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Data;

namespace MusicSalesApp.Services;

/// <summary>
/// Repairs missing track lengths for active songs by re-reading the existing MP3 blob.
///
/// <para>
/// The decode itself happens in the Azure Function. This job only finds the candidates and queues
/// one probe per song; <see cref="AudioProbeResultHandler"/> writes the duration when the result
/// comes back. The count returned is therefore songs <em>dispatched</em>, not songs repaired -
/// nothing has been measured yet by the time this returns.
/// </para>
/// </summary>
public class TrackLengthRepairService : ITrackLengthRepairService
{
    private const int BatchSize = 25;

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IMediaProcessingQueueClient _queueClient;
    private readonly ILogger<TrackLengthRepairService> _logger;

    public TrackLengthRepairService(
        IDbContextFactory<AppDbContext> contextFactory,
        IMediaProcessingQueueClient queueClient,
        ILogger<TrackLengthRepairService> logger)
    {
        _contextFactory = contextFactory;
        _queueClient = queueClient;
        _logger = logger;
    }

    public async Task<int> RepairMissingTrackLengthsAsync()
    {
        if (!_queueClient.IsConfigured)
        {
            // This is a nightly Hangfire job on a best-effort repair. On an environment with no
            // media-processing storage configured it would otherwise throw every night and fill the
            // failed-jobs list with an error nobody is going to act on.
            _logger.LogInformation(
                "Track length repair skipped: audio processing is not configured in this environment.");
            return 0;
        }

        try
        {
            var candidates = await GetCandidatesAsync();

            if (candidates.Count == 0)
            {
                _logger.LogInformation("No active songs with missing track lengths were found.");
                return 0;
            }

            _logger.LogInformation(
                "Dispatching track length probes for {CandidateCount} active song(s).",
                candidates.Count);

            var probes = candidates
                .Select(candidate => new AudioProbeRequest
                {
                    ProbeId = Guid.NewGuid(),
                    Kind = AudioProbeKind.TrackLengthRepair,
                    SongMetadataId = candidate.Id,
                    BlobPath = candidate.BlobPath,
                    Attempt = 1
                })
                .ToList();

            var dispatched = 0;
            foreach (var batch in probes.Chunk(BatchSize))
            {
                await _queueClient.EnqueueProbesAsync(batch);
                dispatched += batch.Length;
            }

            _logger.LogInformation(
                "Track length repair dispatched {DispatchedCount} probe(s). Durations are written as results arrive.",
                dispatched);

            return dispatched;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching track length repairs.");
            throw;
        }
    }

    private async Task<List<TrackLengthRepairCandidate>> GetCandidatesAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.SongMetadata
            .Where(song => song.IsActive && !song.IsAlbumCover)
            .Where(song => !song.TrackLength.HasValue)
            .Where(song =>
                (song.Mp3BlobPath != null && song.Mp3BlobPath != string.Empty) ||
                (song.BlobPath != null && song.BlobPath != string.Empty && song.BlobPath.ToLower().EndsWith(".mp3")))
            .OrderBy(song => song.Id)
            .Select(song => new TrackLengthRepairCandidate(
                song.Id,
                song.Mp3BlobPath != null && song.Mp3BlobPath != string.Empty ? song.Mp3BlobPath : song.BlobPath))
            .ToListAsync();
    }

    private sealed record TrackLengthRepairCandidate(int Id, string BlobPath);
}
