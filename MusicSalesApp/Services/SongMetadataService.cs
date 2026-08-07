using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace MusicSalesApp.Services
{
    /// <summary>
    /// Service for managing song metadata in the database
    /// </summary>
    public class SongMetadataService : ISongMetadataService
    {
        private readonly IDbContextFactory<AppDbContext> _contextFactory;
        private readonly ILogger<SongMetadataService> _logger;

        /// <summary>
        /// Expression to filter songs by active status and active creator.
        /// Songs without a creator (admin-uploaded) are always included.
        /// Also filters out disabled songs.
        /// </summary>
        private static readonly Expression<Func<SongMetadata, bool>> ActiveSongsFromActiveCreators = 
            song => song.IsActive && song.IsEnabled && (song.CreatorId == null || song.Creator!.IsActive);

        /// <summary>
        /// Expression to filter songs by active status and active creator (including disabled songs for admin use).
        /// Songs without a creator (admin-uploaded) are always included.
        /// </summary>
        private static readonly Expression<Func<SongMetadata, bool>> ActiveSongsFromActiveCreatorsIncludingDisabled = 
            song => song.IsActive && (song.CreatorId == null || song.Creator!.IsActive);

        public SongMetadataService(IDbContextFactory<AppDbContext> contextFactory, ILogger<SongMetadataService> logger)
        {
            _contextFactory = contextFactory;
            _logger = logger;
        }

        private static IQueryable<SongMetadata> ApplyDefaultOrdering(IQueryable<SongMetadata> query)
        {
            return query.OrderBy(s => s.Id);
        }

        public async Task<List<SongMetadata>> GetAllAsync()
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await ApplyDefaultOrdering(context.SongMetadata
                .Include(s => s.Creator)
                    .ThenInclude(c => c.User)
                .Include(s => s.Persona)
                .WhereActiveSongsFromActiveCreators())
                .ToListAsync();
        }

        public async Task<List<SongMetadata>> GetAllIncludingDisabledAsync()
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await ApplyDefaultOrdering(context.SongMetadata
                .Include(s => s.Creator)
                    .ThenInclude(c => c.User)
                .Include(s => s.Persona)
                .WhereActiveSongsFromActiveCreatorsIncludingDisabled())
                .ToListAsync();
        }

        public async Task<SongMetadata> GetByIdAsync(int id)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.SongMetadata
                .Include(s => s.Persona)
                .FirstOrDefaultAsync(s => s.Id == id);
        }

        public async Task<SongMetadata> GetByBlobPathAsync(string blobPath)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            // Include disabled songs for admin operations
            return await context.SongMetadata
                .Include(s => s.Creator)
                .Include(s => s.Persona)
                .WhereActiveSongsFromActiveCreatorsIncludingDisabled()
                .FirstOrDefaultAsync(s => s.BlobPath == blobPath || 
                    s.Mp3BlobPath == blobPath || 
                    s.ImageBlobPath == blobPath);
        }

        public async Task<List<SongMetadata>> GetByAlbumNameAsync(string albumName)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.SongMetadata
                .Include(s => s.Creator)
                    .ThenInclude(c => c.User)
                .Include(s => s.Persona)
                .WhereActiveSongsFromActiveCreators()
                .Where(s => s.AlbumName == albumName)
                .ToListAsync();
        }

        public async Task<List<SongMetadata>> GetByArtistNameAsync(string artistName)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await ApplyDefaultOrdering(context.SongMetadata
                .Include(s => s.Creator)
                    .ThenInclude(c => c.User)
                .Include(s => s.Persona)
                .WhereActiveSongsFromActiveCreators()
                .Where(s =>
                    // Highest priority: enabled persona name (matches GetEffectiveArtistName priority)
                    (s.Persona != null && s.Persona.IsEnabled && s.Persona.Name == artistName) ||
                    // Match songs where ArtistName field matches and no enabled persona overrides it
                    ((s.Persona == null || !s.Persona.IsEnabled) && s.ArtistName == artistName) ||
                    // Or match songs where ArtistName is null/empty and Creator.DisplayName matches (and no enabled persona)
                    ((s.Persona == null || !s.Persona.IsEnabled) && (s.ArtistName == null || s.ArtistName == "") && s.Creator != null && s.Creator.DisplayName == artistName) ||
                    // Or match songs where both ArtistName and DisplayName are null/empty and email prefix matches (and no enabled persona)
                    ((s.Persona == null || !s.Persona.IsEnabled) && (s.ArtistName == null || s.ArtistName == "") && 
                     (s.Creator == null || s.Creator.DisplayName == null || s.Creator.DisplayName == "") && 
                     s.Creator != null && s.Creator.User != null && s.Creator.User.Email != null &&
                     s.Creator.User.Email.StartsWith(artistName + "@"))))
                .ToListAsync();
        }

        public async Task<List<SongMetadata>> GetByCreatorIdAsync(int creatorId)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await ApplyDefaultOrdering(context.SongMetadata
                .Include(s => s.Creator)
                    .ThenInclude(c => c.User)
                .Include(s => s.Persona)
                .WhereActiveSongsFromActiveCreators()
                .Where(s => s.CreatorId == creatorId))
                .ToListAsync();
        }

        public async Task<List<SongMetadata>> GetByGenreAsync(string genreName)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            
            // If genreName is "Unknown Genre", return songs with null or empty genre
            if (genreName == "Unknown Genre")
            {
                return await ApplyDefaultOrdering(context.SongMetadata
                    .Include(s => s.Creator)
                        .ThenInclude(c => c.User)
                    .Include(s => s.Persona)
                    .WhereActiveSongsFromActiveCreators()
                    .Where(s => s.Genre == null || s.Genre == ""))
                    .ToListAsync();
            }
            
            // Otherwise, return songs matching the genre
            return await ApplyDefaultOrdering(context.SongMetadata
                .Include(s => s.Creator)
                    .ThenInclude(c => c.User)
                .Include(s => s.Persona)
                .WhereActiveSongsFromActiveCreators()
                .Where(s => s.Genre == genreName))
                .ToListAsync();
        }

        public Task<SongMetadata> UpsertAsync(SongMetadata metadata)
            => UpsertInternalAsync(metadata, isValidatedUpload: false);

        public Task<SongMetadata> UpsertValidatedUploadAsync(SongMetadata metadata)
        {
            if (string.IsNullOrWhiteSpace(metadata.Mp3BlobPath)
                || !metadata.TrackLength.HasValue
                || metadata.TrackLength.Value <= 0
                || !Common.Helpers.SongTitleHelper.IsValidTitle(metadata.SongTitle))
            {
                throw new InvalidOperationException(
                    "A validated upload requires a valid title, playback path, and positive duration.");
            }

            return UpsertInternalAsync(metadata, isValidatedUpload: true);
        }

        public async Task<SongMetadata> ValidateUploadTargetAsync(
            string mp3BlobPath,
            string originalAudioBlobPath,
            string imageBlobPath,
            int creatorId)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var matches = await FindUploadTargetMatches(
                    context,
                    mp3BlobPath,
                    originalAudioBlobPath,
                    imageBlobPath)
                .AsNoTracking()
                .ToListAsync();
            return ValidateReplacementOwnership(matches, creatorId, mp3BlobPath);
        }

        private async Task<SongMetadata> UpsertInternalAsync(
            SongMetadata metadata,
            bool isValidatedUpload)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            
            // Build a comprehensive lookup that checks all blob path fields
            var blobPath = metadata.BlobPath;
            var mp3BlobPath = metadata.Mp3BlobPath;
            var imageBlobPath = metadata.ImageBlobPath;
            var originalAudioBlobPath = metadata.OriginalAudioBlobPath;
            
            var matches = await FindUploadTargetMatches(
                    context,
                    string.IsNullOrWhiteSpace(mp3BlobPath) ? blobPath : mp3BlobPath,
                    originalAudioBlobPath,
                    imageBlobPath)
                .ToListAsync();
            var existing = isValidatedUpload
                ? ValidateReplacementOwnership(
                    matches,
                    metadata.CreatorId
                        ?? throw new InvalidOperationException("A validated upload requires a creator."),
                    mp3BlobPath)
                : matches.FirstOrDefault();
            
            if (existing != null)
            {
                _logger.LogInformation("SongMetadataService.UpsertAsync: Updating existing record Id={Id}, setting CreatorId from {OldCreatorId} to {NewCreatorId}", 
                    existing.Id, existing.CreatorId, metadata.CreatorId ?? existing.CreatorId);
                
                // Update existing
                if (isValidatedUpload)
                {
                    existing.SongTitle = metadata.SongTitle.Trim();
                    existing.BlobPath = metadata.Mp3BlobPath;
                    existing.Mp3BlobPath = metadata.Mp3BlobPath;
                    existing.TrackLength = metadata.TrackLength;
                    existing.FileExtension = ".mp3";
                    existing.OriginalAudioBlobPath = metadata.OriginalAudioBlobPath;
                    existing.OriginalAudioFileName = metadata.OriginalAudioFileName;
                    existing.OriginalAudioFileSize = metadata.OriginalAudioFileSize;
                    existing.OriginalAudioContentType = metadata.OriginalAudioContentType;
                    if (!string.IsNullOrWhiteSpace(metadata.ImageBlobPath))
                    {
                        existing.ImageBlobPath = metadata.ImageBlobPath;
                    }

                    // Cover-art fields reach this branch only on a retried assembly - a fresh upload
                    // mints a new GUID and is always an insert. They were missing from this
                    // whitelist, so a retry silently dropped them and left the song serving its
                    // full-size master with no recorded widths.
                    //
                    // Each guarded so a null never wipes a good value, and CoverArtVariantVersion is
                    // deliberately absent: it is a cache-busting counter that must never regress.
                    if (!string.IsNullOrWhiteSpace(metadata.OriginalCoverArtBlobPath))
                    {
                        existing.OriginalCoverArtBlobPath = metadata.OriginalCoverArtBlobPath;
                    }
                    if (!string.IsNullOrWhiteSpace(metadata.OriginalCoverArtFileName))
                    {
                        existing.OriginalCoverArtFileName = metadata.OriginalCoverArtFileName;
                    }
                    if (!string.IsNullOrWhiteSpace(metadata.CoverArtVariantWidths))
                    {
                        existing.CoverArtVariantWidths = metadata.CoverArtVariantWidths;
                    }
                }
                else
                {
                    existing.AlbumName = metadata.AlbumName;
                    existing.IsAlbumCover = metadata.IsAlbumCover;
                    existing.Genre = metadata.Genre;
                    if (!string.IsNullOrWhiteSpace(metadata.SongTitle))
                    {
                        existing.SongTitle = metadata.SongTitle.Trim();
                    }
                    existing.TrackNumber = metadata.TrackNumber;
                    existing.TrackLength = metadata.TrackLength;
                    existing.Mp3BlobPath = metadata.Mp3BlobPath;
                    existing.ImageBlobPath = metadata.ImageBlobPath;
                    existing.DisplayOnHomePage = metadata.DisplayOnHomePage;
                    existing.IsAiGenerated = metadata.IsAiGenerated;
                    existing.IsAiVocals = metadata.IsAiVocals;
                    existing.IsAiLyrics = metadata.IsAiLyrics;
                    existing.CreatorId = metadata.CreatorId ?? existing.CreatorId;
                    existing.ArtistName = metadata.ArtistName;
                    existing.PersonaId = metadata.PersonaId;
                }
                existing.UpdatedAt = DateTime.UtcNow;

                if (isValidatedUpload)
                {
                    var wasDisabled = !existing.IsEnabled;
                    existing.IsActive = true;
                    existing.IsEnabled = true;
                    existing.StatusReason = null;
                    if (wasDisabled)
                    {
                        context.SongStatusHistories.Add(new SongStatusHistory
                        {
                            SongMetadataId = existing.Id,
                            IsEnabled = true,
                            Reason = Common.Helpers.MediaIntegrityConstants.RecoveryReason,
                            ChangedByUserId = null,
                            ChangedAt = DateTime.UtcNow
                        });
                    }
                }
                
                context.SongMetadata.Update(existing);
            }
            else
            {
                // Create new - ensure IsActive is true by default
                _logger.LogInformation("SongMetadataService.UpsertAsync: Creating new record with BlobPath={BlobPath}, Mp3BlobPath={Mp3BlobPath}, CreatorId={CreatorId}",
                    metadata.BlobPath, metadata.Mp3BlobPath, metadata.CreatorId);
                metadata.IsActive = true;
                metadata.IsEnabled = isValidatedUpload || metadata.IsEnabled;
                context.SongMetadata.Add(metadata);
            }

            // Enforce the same "Mp3BlobPath implies a non-blank title" invariant the
            // SQL Server check constraint applies in production, on every upsert path
            // (create and update), so it's caught here — testably, on every provider —
            // instead of only ever surfacing as an opaque SqlException.
            var target = existing ?? metadata;
            if (!string.IsNullOrWhiteSpace(target.Mp3BlobPath) && string.IsNullOrWhiteSpace(target.SongTitle))
            {
                target.SongTitle = Common.Helpers.SongTitleHelper.GetEffectiveTitle(
                    target.SongTitle,
                    target.Mp3BlobPath,
                    target.BlobPath);
            }

            if (!string.IsNullOrWhiteSpace(target.Mp3BlobPath) && string.IsNullOrWhiteSpace(target.SongTitle))
            {
                throw new InvalidOperationException(
                    $"Cannot save song metadata with Mp3BlobPath '{target.Mp3BlobPath}' and a blank title.");
            }

            await context.SaveChangesAsync();
            return existing ?? metadata;
        }

        private static IQueryable<SongMetadata> FindUploadTargetMatches(
            AppDbContext context,
            string mp3BlobPath,
            string originalAudioBlobPath,
            string imageBlobPath)
            => context.SongMetadata.Where(song =>
                (!string.IsNullOrEmpty(mp3BlobPath)
                    && (song.BlobPath == mp3BlobPath
                        || song.Mp3BlobPath == mp3BlobPath
                        || song.ImageBlobPath == mp3BlobPath
                        || song.OriginalAudioBlobPath == mp3BlobPath))
                || (!string.IsNullOrEmpty(originalAudioBlobPath)
                    && (song.BlobPath == originalAudioBlobPath
                        || song.Mp3BlobPath == originalAudioBlobPath
                        || song.ImageBlobPath == originalAudioBlobPath
                        || song.OriginalAudioBlobPath == originalAudioBlobPath))
                || (!string.IsNullOrEmpty(imageBlobPath)
                    && (song.BlobPath == imageBlobPath
                        || song.Mp3BlobPath == imageBlobPath
                        || song.ImageBlobPath == imageBlobPath
                        || song.OriginalAudioBlobPath == imageBlobPath)));

        private static SongMetadata ValidateReplacementOwnership(
            IReadOnlyCollection<SongMetadata> matches,
            int creatorId,
            string mp3BlobPath)
        {
            if (matches.Count == 0)
                return null;
            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    "The requested media paths are already associated with multiple catalog records.");
            }

            var existing = matches.Single();
            if (existing.CreatorId != creatorId)
            {
                throw new UnauthorizedAccessException(
                    "That media storage location belongs to another creator and cannot be replaced.");
            }

            if (existing.IsActive && existing.IsEnabled)
            {
                throw new InvalidOperationException(
                    $"An active song already uses playback path '{mp3BlobPath}'. Edit that song instead of uploading over it.");
            }

            return existing;
        }

        public async Task<bool> DeleteAsync(string blobPath)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var metadata = await context.SongMetadata
                .FirstOrDefaultAsync(s => s.BlobPath == blobPath || 
                                         s.Mp3BlobPath == blobPath || 
                                         s.ImageBlobPath == blobPath);
            if (metadata != null)
            {
                context.SongMetadata.Remove(metadata);
                await context.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public async Task<bool> DeactivateByBlobPathAsync(string blobPath, string reason = null)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var metadata = await context.SongMetadata
                .FirstOrDefaultAsync(s => s.BlobPath == blobPath ||
                                         s.Mp3BlobPath == blobPath ||
                                         s.ImageBlobPath == blobPath);
            if (metadata != null)
            {
                metadata.IsActive = false;
                metadata.IsEnabled = false;
                metadata.StatusReason = reason;
                metadata.UpdatedAt = DateTime.UtcNow;

                // Remove from all user playlists
                var userPlaylists = await context.UserPlaylists
                    .Where(up => up.SongMetadataId == metadata.Id)
                    .ToListAsync();
                context.UserPlaylists.RemoveRange(userPlaylists);

                // Remove from all recommended playlists
                var recommendedPlaylists = await context.RecommendedPlaylists
                    .Where(rp => rp.SongMetadataId == metadata.Id)
                    .ToListAsync();
                context.RecommendedPlaylists.RemoveRange(recommendedPlaylists);

                await context.SaveChangesAsync();
                return true;
            }

            return false;
        }

        public async Task<PaginatedSongResult> GetPagedAsync(SongQueryParameters parameters)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var query = context.SongMetadata.Include(s => s.Creator).Include(s => s.Persona).AsQueryable();

            // Only include active songs by default (unless specifically querying for inactive)
            if (!parameters.IncludeInactive)
            {
                query = query.Where(ActiveSongsFromActiveCreators);
            }

            // Apply filters
            if (!string.IsNullOrWhiteSpace(parameters.FilterAlbumName))
            {
                query = query.Where(s => s.AlbumName != null && 
                    s.AlbumName.Contains(parameters.FilterAlbumName));
            }

            if (!string.IsNullOrWhiteSpace(parameters.FilterSongTitle))
            {
                // SongTitle is derived from BlobPath filename, filter by BlobPath
                query = query.Where(s => s.BlobPath.Contains(parameters.FilterSongTitle));
            }

            if (!string.IsNullOrWhiteSpace(parameters.FilterGenre))
            {
                query = query.Where(s => s.Genre != null && 
                    s.Genre == parameters.FilterGenre);
            }

            if (!string.IsNullOrWhiteSpace(parameters.FilterType))
            {
                if (parameters.FilterType == "album")
                {
                    query = query.Where(s => s.IsAlbumCover);
                }
                else if (parameters.FilterType == "song")
                {
                    query = query.Where(s => !s.IsAlbumCover && s.FileExtension == ".mp3");
                }
            }

            // Filter by creator ID if specified
            if (parameters.CreatorId.HasValue)
            {
                query = query.Where(s => s.CreatorId == parameters.CreatorId.Value);
            }

            // Apply sorting - always have a default order for consistent pagination
            if (!string.IsNullOrEmpty(parameters.SortColumn))
            {
                query = parameters.SortColumn switch
                {
                    "AlbumName" => parameters.SortAscending
                        ? query.OrderBy(s => s.AlbumName)
                        : query.OrderByDescending(s => s.AlbumName),
                    "Genre" => parameters.SortAscending
                        ? query.OrderBy(s => s.Genre)
                        : query.OrderByDescending(s => s.Genre),
                    "TrackNumber" => parameters.SortAscending
                        ? query.OrderBy(s => s.TrackNumber)
                        : query.OrderByDescending(s => s.TrackNumber),
                    "TrackLength" => parameters.SortAscending
                        ? query.OrderBy(s => s.TrackLength)
                        : query.OrderByDescending(s => s.TrackLength),
                    _ => query.OrderBy(s => s.Id) // Default ordering
                };
            }
            else
            {
                // Default ordering by Id for consistent pagination results
                query = query.OrderBy(s => s.Id);
            }

            var totalCount = await query.CountAsync();

            // Apply pagination
            var items = await query
                .Skip(parameters.Skip)
                .Take(parameters.Take)
                .ToListAsync();

            // Convert to SongAdminViewModel
            // Note: Filter out album covers for legacy data that may still exist in the database.
            // The IsAlbumCover field remains in the schema for backward compatibility but is no longer used for new uploads.
            var viewModels = items
                .Where(m => !m.IsAlbumCover)
                .Select(m => new SongAdminViewModel
            {
                Id = m.Id.ToString(),
                SongTitle = Common.Helpers.SongTitleHelper.GetEffectiveTitle(
                    m.SongTitle, m.Mp3BlobPath, m.ImageBlobPath, m.BlobPath),
                Mp3FileName = m.Mp3BlobPath ?? (m.FileExtension == ".mp3" ? m.BlobPath : string.Empty),
                JpegFileName = m.ImageBlobPath ?? ((m.FileExtension == ".jpg" || m.FileExtension == ".jpeg" || m.FileExtension == ".png") ? m.BlobPath : string.Empty),
                Genre = m.Genre ?? string.Empty,
                TrackLength = m.TrackLength,
                DisplayOnHomePage = m.DisplayOnHomePage,
                CreatorId = m.CreatorId,
                IsActive = m.IsActive
            }).ToList();

            return new PaginatedSongResult
            {
                Items = viewModels,
                TotalCount = totalCount
            };
        }

        public async Task<HashSet<string>> FindExistingSongTitlesAsync(IEnumerable<string> titles)
        {
            var titleSet = new HashSet<string>(titles, StringComparer.OrdinalIgnoreCase);
            if (!titleSet.Any())
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await using var context = await _contextFactory.CreateDbContextAsync();

            var activeSongs = await context.SongMetadata
                .Include(s => s.Creator)
                .Where(ActiveSongsFromActiveCreators)
                .Where(s => !s.IsAlbumCover && s.Mp3BlobPath != null)
                .Select(s => new { s.SongTitle, s.Mp3BlobPath })
                .ToListAsync();

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in activeSongs)
            {
                var effectiveTitle = Common.Helpers.SongTitleHelper.GetEffectiveTitle(
                    song.SongTitle, song.Mp3BlobPath);

                if (!string.IsNullOrEmpty(effectiveTitle) && titleSet.Contains(effectiveTitle))
                    result.Add(effectiveTitle);
            }

            return result;
        }

        public async Task<bool> IsSongTitleDuplicateAsync(string title, int excludeSongId)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            await using var context = await _contextFactory.CreateDbContextAsync();

            var activeSongs = await context.SongMetadata
                .Include(s => s.Creator)
                .Where(ActiveSongsFromActiveCreators)
                .Where(s => !s.IsAlbumCover && s.Mp3BlobPath != null && s.Id != excludeSongId)
                .Select(s => new { s.Id, s.SongTitle, s.Mp3BlobPath })
                .ToListAsync();

            foreach (var song in activeSongs)
            {
                var effectiveTitle = Common.Helpers.SongTitleHelper.GetEffectiveTitle(
                    song.SongTitle, song.Mp3BlobPath);

                if (string.Equals(effectiveTitle, title, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public async Task<bool> IsArtistNameTakenByAnotherCreatorAsync(string artistName, int? creatorId)
        {
            if (string.IsNullOrWhiteSpace(artistName))
                return false;

            // Strip email domain if artist name contains @ to normalize input
            if (artistName.Contains('@'))
            {
                artistName = artistName.Split('@')[0];
            }

            await using var context = await _contextFactory.CreateDbContextAsync();

            // Find active songs that use this artist name but belong to a different creator
            var activeSongs = await context.SongMetadata
                .Include(s => s.Creator)
                .Where(ActiveSongsFromActiveCreators)
                .Where(s => !s.IsAlbumCover && s.Mp3BlobPath != null)
                .Where(s => !string.IsNullOrEmpty(s.ArtistName))
                .Select(s => new { s.ArtistName, s.CreatorId })
                .ToListAsync();

            return activeSongs.Any(s =>
                string.Equals(s.ArtistName, artistName, StringComparison.OrdinalIgnoreCase) &&
                s.CreatorId != creatorId);
        }
    }
}
