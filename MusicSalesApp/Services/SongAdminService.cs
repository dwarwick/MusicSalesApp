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
    /// Service for managing song administration with caching and server-side operations
    /// </summary>
    public class SongAdminService : ISongAdminService
    {
        private readonly IAzureStorageService _storageService;
        private readonly ILogger<SongAdminService> _logger;
        private List<SongAdminViewModel> _cachedSongs = null;
        private readonly object _cacheLock = new object();

        public SongAdminService(IAzureStorageService storageService, ILogger<SongAdminService> logger)
        {
            _storageService = storageService;
            _logger = logger;
        }

        public async Task RefreshCacheAsync()
        {
            _logger.LogInformation("Refreshing song cache from storage");
            var songs = await LoadSongsFromStorageAsync();
            lock (_cacheLock)
            {
                _cachedSongs = songs;
            }
            _logger.LogInformation("Song cache refreshed with {Count} items", songs.Count);
        }

        public async Task<PaginatedSongResult> GetSongsAsync(SongQueryParameters parameters)
        {
            // Ensure cache is loaded
            if (_cachedSongs == null)
            {
                await RefreshCacheAsync();
            }

            List<SongAdminViewModel> workingSet;
            lock (_cacheLock)
            {
                workingSet = new List<SongAdminViewModel>(_cachedSongs);
            }

            // Apply filters
            var filtered = workingSet.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(parameters.FilterAlbumName))
            {
                filtered = filtered.Where(s => s.AlbumName != null && 
                    s.AlbumName.Contains(parameters.FilterAlbumName, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(parameters.FilterSongTitle))
            {
                filtered = filtered.Where(s => s.SongTitle != null && 
                    s.SongTitle.Contains(parameters.FilterSongTitle, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(parameters.FilterGenre))
            {
                filtered = filtered.Where(s => s.Genre != null && 
                    s.Genre.Equals(parameters.FilterGenre, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(parameters.FilterType))
            {
                if (parameters.FilterType == "album")
                {
                    filtered = filtered.Where(s => s.IsAlbum);
                }
                else if (parameters.FilterType == "song")
                {
                    filtered = filtered.Where(s => !s.IsAlbum);
                }
            }

            // Apply sorting
            if (!string.IsNullOrEmpty(parameters.SortColumn))
            {
                filtered = parameters.SortColumn switch
                {
                    nameof(SongAdminViewModel.AlbumName) => parameters.SortAscending
                        ? filtered.OrderBy(s => s.AlbumName)
                        : filtered.OrderByDescending(s => s.AlbumName),
                    nameof(SongAdminViewModel.SongTitle) => parameters.SortAscending
                        ? filtered.OrderBy(s => s.SongTitle)
                        : filtered.OrderByDescending(s => s.SongTitle),
                    nameof(SongAdminViewModel.IsAlbum) => parameters.SortAscending
                        ? filtered.OrderBy(s => s.IsAlbum)
                        : filtered.OrderByDescending(s => s.IsAlbum),
                    nameof(SongAdminViewModel.AlbumPrice) => parameters.SortAscending
                        ? filtered.OrderBy(s => s.AlbumPrice ?? 0)
                        : filtered.OrderByDescending(s => s.AlbumPrice ?? 0),
                    nameof(SongAdminViewModel.SongPrice) => parameters.SortAscending
                        ? filtered.OrderBy(s => s.SongPrice ?? 0)
                        : filtered.OrderByDescending(s => s.SongPrice ?? 0),
                    nameof(SongAdminViewModel.Genre) => parameters.SortAscending
                        ? filtered.OrderBy(s => s.Genre)
                        : filtered.OrderByDescending(s => s.Genre),
                    nameof(SongAdminViewModel.TrackNumber) => parameters.SortAscending
                        ? filtered.OrderBy(s => s.TrackNumber ?? 0)
                        : filtered.OrderByDescending(s => s.TrackNumber ?? 0),
                    nameof(SongAdminViewModel.TrackLength) => parameters.SortAscending
                        ? filtered.OrderBy(s => s.TrackLength ?? 0)
                        : filtered.OrderByDescending(s => s.TrackLength ?? 0),
                    _ => filtered
                };
            }

            var filteredList = filtered.ToList();
            var totalCount = filteredList.Count;

            // Apply pagination
            var pagedItems = filteredList.Skip(parameters.Skip).Take(parameters.Take).ToList();

            _logger.LogDebug("Returning {Count} of {Total} songs (Skip: {Skip}, Take: {Take})", 
                pagedItems.Count, totalCount, parameters.Skip, parameters.Take);

            return new PaginatedSongResult
            {
                Items = pagedItems,
                TotalCount = totalCount
            };
        }

        private async Task<List<SongAdminViewModel>> LoadSongsFromStorageAsync()
        {
            var allFiles = await _storageService.ListFilesAsync();
            var songMap = new Dictionary<string, SongAdminViewModel>();

            // Group files by base name (song title)
            foreach (var file in allFiles)
            {
                var fileName = file.Name;
                var isImage = fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                             fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
                var isMp3 = fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

                if (!isImage && !isMp3) continue;

                // Extract album name and song title from tags
                var albumName = file.Tags.TryGetValue(IndexTagNames.AlbumName, out var album) ? album : string.Empty;
                var isAlbumCover = file.Tags.TryGetValue(IndexTagNames.IsAlbumCover, out var coverFlag) &&
                                  coverFlag.Equals("true", StringComparison.OrdinalIgnoreCase);

                // Get base name (remove extension and "_mastered" suffix)
                var baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
                if (baseName.EndsWith("_mastered", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = baseName.Substring(0, baseName.Length - "_mastered".Length);
                }

                // Create a unique key combining album name and base name
                var key = string.IsNullOrEmpty(albumName) ? baseName : $"{albumName}|{baseName}";

                if (!songMap.ContainsKey(key))
                {
                    songMap[key] = new SongAdminViewModel
                    {
                        Id = key,
                        AlbumName = albumName,
                        SongTitle = baseName,
                        IsAlbum = false
                    };
                }

                var song = songMap[key];

                // Parse prices and genre from tags
                if (file.Tags.TryGetValue(IndexTagNames.AlbumPrice, out var albumPriceStr) &&
                    decimal.TryParse(albumPriceStr, out var albumPrice))
                {
                    song.AlbumPrice = albumPrice;
                }

                if (file.Tags.TryGetValue(IndexTagNames.SongPrice, out var songPriceStr) &&
                    decimal.TryParse(songPriceStr, out var songPrice))
                {
                    song.SongPrice = songPrice;
                }

                if (file.Tags.TryGetValue(IndexTagNames.Genre, out var genre))
                {
                    song.Genre = genre;
                }

                if (file.Tags.TryGetValue(IndexTagNames.TrackNumber, out var trackNumberStr) &&
                    int.TryParse(trackNumberStr, out var trackNumber))
                {
                    song.TrackNumber = trackNumber;
                }

                if (file.Tags.TryGetValue(IndexTagNames.TrackLength, out var trackLengthStr) &&
                    double.TryParse(trackLengthStr, out var trackLength))
                {
                    song.TrackLength = trackLength;
                }

                if (isMp3)
                {
                    song.Mp3FileName = fileName;
                }
                else if (isImage)
                {
                    if (isAlbumCover)
                    {
                        // This is an album cover
                        song.IsAlbum = true;
                        song.AlbumCoverBlobName = fileName;
                        song.AlbumCoverImageUrl = _storageService.GetReadSasUri(fileName, TimeSpan.FromHours(1)).ToString();
                        song.HasAlbumCover = true;
                    }
                    else
                    {
                        // This is a song cover
                        song.JpegFileName = fileName;
                        song.SongImageUrl = _storageService.GetReadSasUri(fileName, TimeSpan.FromHours(1)).ToString();
                    }
                }
            }

            // Now handle album covers - find all songs with the same album name and link them
            var albumCovers = songMap.Values.Where(s => s.IsAlbum && s.HasAlbumCover).ToList();
            foreach (var albumCover in albumCovers)
            {
                var songsInAlbum = songMap.Values.Where(s =>
                    !s.IsAlbum &&
                    !string.IsNullOrEmpty(s.AlbumName) &&
                    s.AlbumName.Equals(albumCover.AlbumName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var song in songsInAlbum)
                {
                    song.HasAlbumCover = true;
                    song.AlbumCoverBlobName = albumCover.AlbumCoverBlobName;
                    song.AlbumCoverImageUrl = albumCover.AlbumCoverImageUrl;
                }
            }

            return songMap.Values.ToList();
        }
    }
}
