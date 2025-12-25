namespace MusicSalesApp.Services;

/// <summary>
/// Service for generating Open Graph meta tags for social media sharing
/// </summary>
public interface IOpenGraphService
{
    /// <summary>
    /// Generates Open Graph meta tags for a song page
    /// </summary>
    /// <param name="songTitle">The title of the song (URL encoded)</param>
    /// <returns>HTML string containing meta tags, or empty string if song not found</returns>
    Task<string> GenerateSongMetaTagsAsync(string songTitle);

    /// <summary>
    /// Generates Open Graph meta tags for an album page
    /// </summary>
    /// <param name="albumName">The name of the album (URL encoded)</param>
    /// <returns>HTML string containing meta tags, or empty string if album not found</returns>
    Task<string> GenerateAlbumMetaTagsAsync(string albumName);
}
