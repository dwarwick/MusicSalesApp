namespace MusicSalesApp.Services;

/// <summary>
/// Service for generating and managing the sitemap.xml file.
/// </summary>
public interface ISitemapService
{
    /// <summary>
    /// Generates and saves the sitemap.xml file to wwwroot directory.
    /// This method is designed to be called by a nightly Hangfire job.
    /// Includes all publicly accessible pages: songs and albums.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task GenerateSitemapAsync();
}
