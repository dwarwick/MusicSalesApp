using MusicSalesApp.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MusicSalesApp.Services
{
    /// <summary>
    /// Service for managing song metadata in the database
    /// </summary>
    public interface ISongMetadataService
    {
        /// <summary>
        /// Get all song metadata records
        /// </summary>
        Task<List<SongMetadata>> GetAllAsync();

        /// <summary>
        /// Get metadata by ID
        /// </summary>
        Task<SongMetadata> GetByIdAsync(int id);

        /// <summary>
        /// Get metadata by blob path
        /// </summary>
        Task<SongMetadata> GetByBlobPathAsync(string blobPath);

        /// <summary>
        /// Get metadata by album name
        /// </summary>
        Task<List<SongMetadata>> GetByAlbumNameAsync(string albumName);

        /// <summary>
        /// Create or update song metadata
        /// </summary>
        Task<SongMetadata> UpsertAsync(SongMetadata metadata);

        /// <summary>
        /// Delete metadata by blob path
        /// </summary>
        Task<bool> DeleteAsync(string blobPath);

        /// <summary>
        /// Get paginated song metadata with filtering and sorting
        /// </summary>
        Task<PaginatedSongResult> GetPagedAsync(SongQueryParameters parameters);
    }
}
