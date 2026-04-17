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

    /// <summary>
    /// Generates Open Graph meta tags for a song looked up by database ID.
    /// Used by /share/{id} to avoid URL-encoding issues with song titles.
    /// </summary>
    Task<string> GenerateSongMetaTagsByIdAsync(int songId);

    /// <summary>
    /// Pre-generates the Facebook-optimized image (1200x630) for a given image blob path.
    /// Call this during upload so the image is ready before Facebook's crawler requests it.
    /// </summary>
    /// <param name="imageBlobPath">The blob path of the original image</param>
    Task PreGenerateFacebookImageAsync(string imageBlobPath);
}
