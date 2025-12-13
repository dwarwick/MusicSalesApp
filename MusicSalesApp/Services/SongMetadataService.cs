using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
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

        public SongMetadataService(IDbContextFactory<AppDbContext> contextFactory, ILogger<SongMetadataService> logger)
        {
            _contextFactory = contextFactory;
            _logger = logger;
        }

        public async Task<List<SongMetadata>> GetAllAsync()
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.SongMetadata.ToListAsync();
        }

        public async Task<SongMetadata> GetByBlobPathAsync(string blobPath)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.SongMetadata
                .FirstOrDefaultAsync(s => s.BlobPath == blobPath || 
                                         s.Mp3BlobPath == blobPath || 
                                         s.ImageBlobPath == blobPath);
        }

        public async Task<List<SongMetadata>> GetByAlbumNameAsync(string albumName)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            return await context.SongMetadata
                .Where(s => s.AlbumName == albumName)
                .ToListAsync();
        }

        public async Task<SongMetadata> UpsertAsync(SongMetadata metadata)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var existing = await context.SongMetadata
                .FirstOrDefaultAsync(s => s.BlobPath == metadata.BlobPath || 
                                         s.Mp3BlobPath == metadata.BlobPath || 
                                         s.ImageBlobPath == metadata.BlobPath);
            
            if (existing != null)
            {
                // Update existing
                existing.AlbumName = metadata.AlbumName;
                existing.IsAlbumCover = metadata.IsAlbumCover;
                existing.AlbumPrice = metadata.AlbumPrice;
                existing.SongPrice = metadata.SongPrice;
                existing.Genre = metadata.Genre;
                existing.TrackNumber = metadata.TrackNumber;
                existing.TrackLength = metadata.TrackLength;
                existing.Mp3BlobPath = metadata.Mp3BlobPath;
                existing.ImageBlobPath = metadata.ImageBlobPath;
                existing.UpdatedAt = DateTime.UtcNow;
                
                context.SongMetadata.Update(existing);
            }
            else
            {
                // Create new
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

        public async Task<PaginatedSongResult> GetPagedAsync(SongQueryParameters parameters)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var query = context.SongMetadata.AsQueryable();

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
                    "AlbumPrice" => parameters.SortAscending
                        ? query.OrderBy(s => s.AlbumPrice)
                        : query.OrderByDescending(s => s.AlbumPrice),
                    "SongPrice" => parameters.SortAscending
                        ? query.OrderBy(s => s.SongPrice)
                        : query.OrderByDescending(s => s.SongPrice),
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
            var viewModels = items.Select(m => new SongAdminViewModel
            {
                Id = m.Id.ToString(),
                AlbumName = m.AlbumName ?? string.Empty,
                SongTitle = System.IO.Path.GetFileNameWithoutExtension(m.Mp3BlobPath ?? m.ImageBlobPath ?? m.BlobPath),
                Mp3FileName = m.Mp3BlobPath ?? (m.FileExtension == ".mp3" ? m.BlobPath : string.Empty),
                JpegFileName = m.IsAlbumCover ? string.Empty : (m.ImageBlobPath ?? ((m.FileExtension == ".jpg" || m.FileExtension == ".jpeg" || m.FileExtension == ".png") ? m.BlobPath : string.Empty)),
                AlbumCoverBlobName = m.IsAlbumCover ? (m.ImageBlobPath ?? m.BlobPath) : string.Empty,
                IsAlbum = m.IsAlbumCover,
                AlbumPrice = m.AlbumPrice,
                SongPrice = m.SongPrice,
                Genre = m.Genre ?? string.Empty,
                TrackNumber = m.TrackNumber,
                TrackLength = m.TrackLength,
                HasAlbumCover = m.IsAlbumCover
            }).ToList();

            return new PaginatedSongResult
            {
                Items = viewModels,
                TotalCount = totalCount
            };
        }
    }
}
