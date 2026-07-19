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
        /// Get all song metadata records (only enabled songs)
        /// </summary>
        Task<List<SongMetadata>> GetAllAsync();

        /// <summary>
        /// Get all song metadata records including disabled songs (for admin use)
        /// </summary>
        Task<List<SongMetadata>> GetAllIncludingDisabledAsync();

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
        /// Get metadata by artist name (from SongMetadata.ArtistName field)
        /// </summary>
        Task<List<SongMetadata>> GetByArtistNameAsync(string artistName);

        /// <summary>
        /// Get metadata by creator ID (all songs from a specific creator)
        /// </summary>
        Task<List<SongMetadata>> GetByCreatorIdAsync(int creatorId);

        /// <summary>
        /// Get metadata by genre. If genreName is "Unknown Genre", returns songs with null or empty genre.
        /// </summary>
        Task<List<SongMetadata>> GetByGenreAsync(string genreName);

        /// <summary>
        /// Create or update song metadata
        /// </summary>
        Task<SongMetadata> UpsertAsync(SongMetadata metadata);

        /// <summary>
        /// Persists a fully validated upload and is the only upsert path allowed to
        /// reactivate a previously quarantined song.
        /// </summary>
        Task<SongMetadata> UpsertValidatedUploadAsync(SongMetadata metadata);

        Task<SongMetadata> ValidateUploadTargetAsync(
            string mp3BlobPath,
            string originalAudioBlobPath,
            string imageBlobPath,
            int creatorId);

        /// <summary>
        /// Delete metadata by blob path
        /// </summary>
        Task<bool> DeleteAsync(string blobPath);

        /// <summary>
        /// Deactivate a song by blob path. Sets IsActive=false and IsEnabled=false
        /// with an optional reason. Used when uploads are cancelled mid-session.
        /// </summary>
        Task<bool> DeactivateByBlobPathAsync(string blobPath, string reason = null);

        /// <summary>
        /// Get paginated song metadata with filtering and sorting
        /// </summary>
        Task<PaginatedSongResult> GetPagedAsync(SongQueryParameters parameters);

        /// <summary>
        /// Given a set of candidate song titles, returns the subset that already exist
        /// as active songs in the database (case-insensitive comparison).
        /// Checks both the explicit SongTitle field and the title derived from Mp3BlobPath.
        /// </summary>
        Task<HashSet<string>> FindExistingSongTitlesAsync(IEnumerable<string> titles);

        /// <summary>
        /// Checks if a song title already exists as an active song, excluding the specified song ID.
        /// Checks both the explicit SongTitle field and the title derived from Mp3BlobPath.
        /// </summary>
        Task<bool> IsSongTitleDuplicateAsync(string title, int excludeSongId);

        /// <summary>
        /// Checks if an artist name is already used by a different creator.
        /// Returns true if another creator (different CreatorId) has songs with the given ArtistName.
        /// </summary>
        Task<bool> IsArtistNameTakenByAnotherCreatorAsync(string artistName, int? creatorId);
    }
}
