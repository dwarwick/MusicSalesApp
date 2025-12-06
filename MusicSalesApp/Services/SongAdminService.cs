using Microsoft.Extensions.Logging;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MusicSalesApp.Services
{
    /// <summary>
    /// Service for managing song administration with database-backed operations
    /// </summary>
    public class SongAdminService : ISongAdminService
    {
        private readonly ISongMetadataService _metadataService;
        private readonly IAzureStorageService _storageService;
        private readonly ILogger<SongAdminService> _logger;

        public SongAdminService(
            ISongMetadataService metadataService,
            IAzureStorageService storageService,
            ILogger<SongAdminService> logger)
        {
            _metadataService = metadataService;
            _storageService = storageService;
            _logger = logger;
        }

        public async Task RefreshCacheAsync()
        {
            _logger.LogInformation("Refresh cache is now a no-op - using database queries");
            await Task.CompletedTask;
        }

        public async Task<PaginatedSongResult> GetSongsAsync(SongQueryParameters parameters)
        {
            // Delegate to metadata service which queries the database directly
            var result = await _metadataService.GetPagedAsync(parameters);

            // Add image URLs from storage service
            foreach (var song in result.Items)
            {
                if (!string.IsNullOrEmpty(song.JpegFileName))
                {
                    song.SongImageUrl = _storageService.GetReadSasUri(song.JpegFileName, TimeSpan.FromHours(1)).ToString();
                }
                if (!string.IsNullOrEmpty(song.AlbumCoverBlobName))
                {
                    song.AlbumCoverImageUrl = _storageService.GetReadSasUri(song.AlbumCoverBlobName, TimeSpan.FromHours(1)).ToString();
                }
            }

            _logger.LogDebug("Returning {Count} of {Total} songs (Skip: {Skip}, Take: {Take})", 
                result.Items.Count(), result.TotalCount, parameters.Skip, parameters.Take);

            return result;
        }
    }
}
