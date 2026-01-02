using MusicSalesApp.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MusicSalesApp.Services
{
    /// <summary>
    /// Service for managing song administration with support for server-side operations
    /// </summary>
    public interface ISongAdminService
    {
        /// <summary>
        /// Get paginated songs with optional filtering and sorting
        /// </summary>
        Task<PaginatedSongResult> GetSongsAsync(SongQueryParameters parameters);

        /// <summary>
        /// Refresh the song cache from storage
        /// </summary>
        Task RefreshCacheAsync();
    }

    public class SongQueryParameters
    {
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 10;
        public string SortColumn { get; set; } = string.Empty;
        public bool SortAscending { get; set; } = true;
        public string FilterAlbumName { get; set; } = string.Empty;
        public string FilterSongTitle { get; set; } = string.Empty;
        public string FilterGenre { get; set; } = string.Empty;
        public string FilterType { get; set; } = string.Empty; // "album" or "song"
        public int? SellerId { get; set; } // Filter by seller ID (for seller-specific views)
        public bool IncludeInactive { get; set; } = false; // Include inactive songs (for admin views)
    }

    public class PaginatedSongResult
    {
        public IEnumerable<SongAdminViewModel> Items { get; set; } = new List<SongAdminViewModel>();
        public int TotalCount { get; set; }
    }
}
