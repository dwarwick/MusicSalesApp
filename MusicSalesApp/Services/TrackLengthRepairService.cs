using System.IO;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;

namespace MusicSalesApp.Services;

/// <summary>
/// Repairs missing track lengths for active songs by re-reading the existing MP3 blob.
/// </summary>
public class TrackLengthRepairService : ITrackLengthRepairService
{
    private static readonly TimeSpan DelayBetweenCandidates = TimeSpan.FromMilliseconds(500);

    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly IAzureStorageService _storageService;
    private readonly IMusicService _musicService;
    private readonly ILogger<TrackLengthRepairService> _logger;

    public TrackLengthRepairService(
        IDbContextFactory<AppDbContext> contextFactory,
        IAzureStorageService storageService,
        IMusicService musicService,
        ILogger<TrackLengthRepairService> logger)
    {
        _contextFactory = contextFactory;
        _storageService = storageService;
        _musicService = musicService;
        _logger = logger;
    }

    public async Task<int> RepairMissingTrackLengthsAsync()
    {
        try
        {
            var candidates = await GetCandidatesAsync();

            if (candidates.Count == 0)
            {
                _logger.LogInformation("No active songs with missing track lengths were found.");
                return 0;
            }

            _logger.LogInformation(
                "Repairing missing track lengths for {CandidateCount} active song(s).",
                candidates.Count);

            int repairedCount = 0;
            int skippedCount = 0;
            int failureCount = 0;

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var outcome = await TryRepairCandidateAsync(candidate);
                switch (outcome)
                {
                    case TrackLengthRepairOutcome.Repaired:
                        repairedCount++;
                        break;
                    case TrackLengthRepairOutcome.Skipped:
                        skippedCount++;
                        break;
                    default:
                        failureCount++;
                        break;
                }

                if (i < candidates.Count - 1)
                {
                    await Task.Delay(DelayBetweenCandidates);
                }
            }

            _logger.LogInformation(
                "Track length repair finished. Candidates={CandidateCount}, Repaired={RepairedCount}, Skipped={SkippedCount}, Failed={FailureCount}",
                candidates.Count,
                repairedCount,
                skippedCount,
                failureCount);

            return repairedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error repairing missing track lengths.");
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

    private async Task<TrackLengthRepairOutcome> TryRepairCandidateAsync(TrackLengthRepairCandidate candidate)
    {
        try
        {
            _logger.LogInformation(
                "Attempting track length repair for song {SongId} from blob {BlobPath}.",
                candidate.Id,
                candidate.BlobPath);

            await using var stream = await _storageService.OpenReadAsync(candidate.BlobPath);
            if (stream == null || (stream.CanSeek && stream.Length == 0))
            {
                _logger.LogWarning(
                    "Skipping track length repair for song {SongId} because blob {BlobPath} could not be opened or was empty.",
                    candidate.Id,
                    candidate.BlobPath);
                return TrackLengthRepairOutcome.Skipped;
            }

            var duration = await _musicService.GetAudioDurationAsync(stream, Path.GetFileName(candidate.BlobPath));
            if (!duration.HasValue || duration.Value <= 0)
            {
                _logger.LogWarning(
                    "Skipping track length repair for song {SongId} because duration extraction failed for blob {BlobPath}.",
                    candidate.Id,
                    candidate.BlobPath);
                return TrackLengthRepairOutcome.Skipped;
            }

            var updated = await TryPersistTrackLengthAsync(candidate, duration.Value);
            return updated ? TrackLengthRepairOutcome.Repaired : TrackLengthRepairOutcome.Skipped;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to repair track length for song {SongId} and blob {BlobPath}.",
                candidate.Id,
                candidate.BlobPath);
            return TrackLengthRepairOutcome.Failed;
        }
    }

    private async Task<bool> TryPersistTrackLengthAsync(TrackLengthRepairCandidate candidate, double duration)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var song = await context.SongMetadata
            .Where(song => song.IsActive && !song.IsAlbumCover)
            .FirstOrDefaultAsync(song => song.Id == candidate.Id);

        if (song == null)
        {
            _logger.LogWarning(
                "Skipping track length repair for song {SongId} because the song is no longer an active playable track.",
                candidate.Id);
            return false;
        }

        if (song.TrackLength.HasValue)
        {
            _logger.LogInformation(
                "Skipping track length repair for song {SongId} because TrackLength is already populated.",
                candidate.Id);
            return false;
        }

        song.TrackLength = duration;
        if (string.IsNullOrEmpty(song.Mp3BlobPath))
        {
            song.Mp3BlobPath = candidate.BlobPath;
        }

        song.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Updated missing track length for song {SongId} to {TrackLengthSeconds} seconds from blob {BlobPath}.",
            candidate.Id,
            duration,
            candidate.BlobPath);

        return true;
    }

    private enum TrackLengthRepairOutcome
    {
        Repaired,
        Skipped,
        Failed
    }

    private sealed record TrackLengthRepairCandidate(int Id, string BlobPath);
}
