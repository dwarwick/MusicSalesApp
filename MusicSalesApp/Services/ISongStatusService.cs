using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for managing song enable/disable status
/// </summary>
public interface ISongStatusService
{
    /// <summary>
    /// Disables a song, records the change in history, removes it from playlists, and sends notification email to creator.
    /// </summary>
    /// <param name="songMetadataId">The ID of the song to disable</param>
    /// <param name="reason">The reason for disabling the song</param>
    /// <param name="adminUserId">The ID of the admin making the change</param>
    /// <param name="baseUrl">Base URL for email links</param>
    /// <returns>True if successful</returns>
    Task<bool> DisableSongAsync(int songMetadataId, string reason, int adminUserId, string baseUrl);

    /// <summary>
    /// Enables a previously disabled song, records the change in history, and sends notification email to creator.
    /// </summary>
    /// <param name="songMetadataId">The ID of the song to enable</param>
    /// <param name="reason">The reason for enabling the song</param>
    /// <param name="adminUserId">The ID of the admin making the change</param>
    /// <param name="baseUrl">Base URL for email links</param>
    /// <returns>True if successful</returns>
    Task<bool> EnableSongAsync(int songMetadataId, string reason, int adminUserId, string baseUrl);

    /// <summary>
    /// Gets the status history for a song
    /// </summary>
    /// <param name="songMetadataId">The ID of the song</param>
    /// <returns>List of status history records</returns>
    Task<List<SongStatusHistory>> GetSongStatusHistoryAsync(int songMetadataId);
}
