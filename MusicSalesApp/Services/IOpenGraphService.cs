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

    /// <summary>
    /// Deletes the cached Facebook-optimized image for a cover art path.
    /// Call this whenever the cover art changes: a song's sharing image has a fixed name, so the
    /// stale copy would otherwise keep being served.
    /// </summary>
    /// <param name="imageBlobPath">The blob path of the cover art whose sharing image is stale</param>
    Task InvalidateFacebookImageAsync(string imageBlobPath);

    /// <summary>
    /// Brings the sharing image back in step after a song's cover art changes: drops the cached
    /// image for both the old and new art, then regenerates it for the new art.
    ///
    /// This is the single entry point every cover-art writer should call. Failures are logged and
    /// swallowed - a stale share image must never fail the edit that caused it.
    /// </summary>
    /// <param name="previousCoverArtPath">The cover art being replaced, if any.</param>
    /// <param name="newCoverArtPath">The cover art now in use.</param>
    Task RefreshSharingImageAsync(string previousCoverArtPath, string newCoverArtPath);
}
