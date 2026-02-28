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
        private static readonly Expression<Func<SongMetadata, bool>> ActiveSongFromActiveCreator = 
            s => s.IsActive && s.IsEnabled && (s.CreatorId == null || s.Creator!.IsActive);

        /// <summary>
        /// Expression to filter songs by active status and active creator (including disabled songs for admin use).
        /// Songs without a creator (admin-uploaded) are always included.
        /// </summary>
        private static readonly Expression<Func<SongMetadata, bool>> ActiveSongFromActiveCreatorIncludingDisabled = 
            s => s.IsActive && (s.CreatorId == null || s.Creator!.IsActive);

        public SongMetadataService(IDbContextFactory<AppDbContext> contextFactory, ILogger<SongMetadataService> logger)
        {
            _contextFactory = contextFactory;
            _logger = logger;
        }

        public async Task<List<SongMetadata>> GetAllAsync()
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.SongMetadata
                .Include(s => s.Creator)
                    .ThenInclude(c => c.User)
                .Where(ActiveSongFromActiveCreator)
                .ToListAsync();
        }

        public async Task<List<SongMetadata>> GetAllIncludingDisabledAsync()
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.SongMetadata
                .Include(s => s.Creator)
                    .ThenInclude(c => c.User)
                .Where(ActiveSongFromActiveCreatorIncludingDisabled)
                .ToListAsync();
        }

        public async Task<SongMetadata> GetByIdAsync(int id)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.SongMetadata.FirstOrDefaultAsync(s => s.Id == id);
        }

        public async Task<SongMetadata> GetByBlobPathAsync(string blobPath)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            // Include disabled songs for admin operations
            return await context.SongMetadata
                .Include(s => s.Creator)
                .Where(ActiveSongFromActiveCreatorIncludingDisabled)
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
                .Where(ActiveSongFromActiveCreator)
                .Where(s => s.AlbumName == albumName)
                .ToListAsync();
        }

        public async Task<List<SongMetadata>> GetByArtistNameAsync(string artistName)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.SongMetadata
                .Include(s => s.Creator)
                    .ThenInclude(c => c.User)
                .Where(ActiveSongFromActiveCreator)
                .Where(s => 
                    // Match songs where ArtistName field matches
                    s.ArtistName == artistName ||
                    // Or match songs where ArtistName is null/empty and Creator.DisplayName matches
                    ((s.ArtistName == null || s.ArtistName == "") && s.Creator != null && s.Creator.DisplayName == artistName) ||
                    // Or match songs where both ArtistName and DisplayName are null/empty and email prefix matches
                    ((s.ArtistName == null || s.ArtistName == "") && 
                     (s.Creator == null || s.Creator.DisplayName == null || s.Creator.DisplayName == "") && 
                     s.Creator != null && s.Creator.User != null && s.Creator.User.Email != null &&
                     s.Creator.User.Email.StartsWith(artistName + "@")))
                .ToListAsync();
        }

        public async Task<List<SongMetadata>> GetByCreatorIdAsync(int creatorId)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.SongMetadata
                .Include(s => s.Creator)
                    .ThenInclude(c => c.User)
                .Where(ActiveSongFromActiveCreator)
                .Where(s => s.CreatorId == creatorId)
                .ToListAsync();
        }

        public async Task<List<SongMetadata>> GetByGenreAsync(string genreName)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            
            // If genreName is "Unknown Genre", return songs with null or empty genre
            if (genreName == "Unknown Genre")
            {
                return await context.SongMetadata
                    .Include(s => s.Creator)
                        .ThenInclude(c => c.User)
                    .Where(ActiveSongFromActiveCreator)
                    .Where(s => s.Genre == null || s.Genre == "")
                    .ToListAsync();
            }
            
            // Otherwise, return songs matching the genre
            return await context.SongMetadata
                .Include(s => s.Creator)
                    .ThenInclude(c => c.User)
                .Where(ActiveSongFromActiveCreator)
                .Where(s => s.Genre == genreName)
                .ToListAsync();
        }

        public async Task<SongMetadata> UpsertAsync(SongMetadata metadata)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            
            // Build a comprehensive lookup that checks all blob path fields
            var blobPath = metadata.BlobPath;
            var mp3BlobPath = metadata.Mp3BlobPath;
            var imageBlobPath = metadata.ImageBlobPath;
            
            var existing = await context.SongMetadata
                .FirstOrDefaultAsync(s => 
                    (!string.IsNullOrEmpty(blobPath) && (s.BlobPath == blobPath || s.Mp3BlobPath == blobPath || s.ImageBlobPath == blobPath)) ||
                    (!string.IsNullOrEmpty(mp3BlobPath) && (s.BlobPath == mp3BlobPath || s.Mp3BlobPath == mp3BlobPath)) ||
                    (!string.IsNullOrEmpty(imageBlobPath) && (s.BlobPath == imageBlobPath || s.ImageBlobPath == imageBlobPath)));
            
            if (existing != null)
            {
                _logger.LogInformation("SongMetadataService.UpsertAsync: Updating existing record Id={Id}, setting CreatorId from {OldCreatorId} to {NewCreatorId}", 
                    existing.Id, existing.CreatorId, metadata.CreatorId ?? existing.CreatorId);
                
                // Update existing
                existing.AlbumName = metadata.AlbumName;
                existing.IsAlbumCover = metadata.IsAlbumCover;
                existing.Genre = metadata.Genre;
                existing.SongTitle = metadata.SongTitle;
                existing.TrackNumber = metadata.TrackNumber;
                existing.TrackLength = metadata.TrackLength;
                existing.Mp3BlobPath = metadata.Mp3BlobPath;
                existing.ImageBlobPath = metadata.ImageBlobPath;
                existing.DisplayOnHomePage = metadata.DisplayOnHomePage;
                existing.CreatorId = metadata.CreatorId ?? existing.CreatorId; // Only update if provided
                existing.ArtistName = metadata.ArtistName;
                existing.UpdatedAt = DateTime.UtcNow;

                // Re-uploading to a previously used path should always reactivate the song.
                // This is critical when a creator re-signs up and re-uploads songs whose
                // SongMetadata rows were marked inactive (IsActive=false) during deactivation.
                existing.IsActive = true;
                existing.IsEnabled = true;
                
                context.SongMetadata.Update(existing);
            }
            else
            {
                // Create new - ensure IsActive is true by default
                _logger.LogInformation("SongMetadataService.UpsertAsync: Creating new record with BlobPath={BlobPath}, Mp3BlobPath={Mp3BlobPath}, CreatorId={CreatorId}", 
                    metadata.BlobPath, metadata.Mp3BlobPath, metadata.CreatorId);
                metadata.IsActive = true;
                context.SongMetadata.Add(metadata);
            }

            await context.SaveChangesAsync();
            return existing ?? metadata;
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
                await context.SaveChangesAsync();
                return true;
            }

            return false;
        }

        public async Task<PaginatedSongResult> GetPagedAsync(SongQueryParameters parameters)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var query = context.SongMetadata.Include(s => s.Creator).AsQueryable();

            // Only include active songs by default (unless specifically querying for inactive)
            if (!parameters.IncludeInactive)
            {
                query = query.Where(ActiveSongFromActiveCreator);
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
                SongTitle = System.IO.Path.GetFileNameWithoutExtension(m.Mp3BlobPath ?? m.ImageBlobPath ?? m.BlobPath),
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
                .Where(ActiveSongFromActiveCreator)
                .Where(s => !s.IsAlbumCover && s.Mp3BlobPath != null)
                .Select(s => new { s.SongTitle, s.Mp3BlobPath })
                .ToListAsync();

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in activeSongs)
            {
                var effectiveTitle = !string.IsNullOrEmpty(song.SongTitle)
                    ? song.SongTitle
                    : System.IO.Path.GetFileNameWithoutExtension(song.Mp3BlobPath ?? string.Empty);

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
                .Where(ActiveSongFromActiveCreator)
                .Where(s => !s.IsAlbumCover && s.Mp3BlobPath != null && s.Id != excludeSongId)
                .Select(s => new { s.Id, s.SongTitle, s.Mp3BlobPath })
                .ToListAsync();

            foreach (var song in activeSongs)
            {
                var effectiveTitle = !string.IsNullOrEmpty(song.SongTitle)
                    ? song.SongTitle
                    : System.IO.Path.GetFileNameWithoutExtension(song.Mp3BlobPath ?? string.Empty);

                if (string.Equals(effectiveTitle, title, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public async Task<bool> IsArtistNameTakenByAnotherCreatorAsync(string artistName, int? creatorId)
        {
            if (string.IsNullOrWhiteSpace(artistName))
                return false;

            await using var context = await _contextFactory.CreateDbContextAsync();

            // Find active songs that use this artist name but belong to a different creator
            var activeSongs = await context.SongMetadata
                .Include(s => s.Creator)
                .Where(ActiveSongFromActiveCreator)
                .Where(s => !s.IsAlbumCover && s.Mp3BlobPath != null)
                .Where(s => s.ArtistName != null && s.ArtistName != "")
                .Select(s => new { s.ArtistName, s.CreatorId })
                .ToListAsync();

            return activeSongs.Any(s =>
                string.Equals(s.ArtistName, artistName, StringComparison.OrdinalIgnoreCase) &&
                s.CreatorId != creatorId);
        }
    }
}
