namespace MusicSalesApp.Services;

/// <summary>
/// Service for generating and managing the sitemap.xml file.
/// </summary>
public interface ISitemapService
{
    /// <summary>
    /// Generates and saves the sitemap.xml file to wwwroot directory.
    /// This method is designed to be called by a nightly Hangfire job.
    /// Includes all publicly accessible pages: songs, albums, artists, and genres.
    /// Only processes active songs from active creators.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task GenerateSitemapAsync();
}
