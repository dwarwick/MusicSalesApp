using System.Text;
using System.Xml;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for generating and managing the sitemap.xml file.
/// Generates URLs for all publicly accessible songs and albums.
/// </summary>
public class SitemapService : ISitemapService
{
    private readonly ISongMetadataService _songMetadataService;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<SitemapService> _logger;

    // Sitemap XML namespace
    private const string SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";

    public SitemapService(
        ISongMetadataService songMetadataService,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<SitemapService> logger)
    {
        _songMetadataService = songMetadataService;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task GenerateSitemapAsync()
    {
        _logger.LogInformation("Starting sitemap generation");

        try
        {
            // Get all song metadata
            var allMetadata = await _songMetadataService.GetAllAsync();

            // Get base URL from configuration
            var baseUrl = _configuration["App:BaseUrl"] ?? "https://streamtunes.net";
            baseUrl = baseUrl.TrimEnd('/');

            // Separate songs and albums
            var songs = allMetadata
                .Where(m => !string.IsNullOrEmpty(m.Mp3BlobPath) && string.IsNullOrEmpty(m.AlbumName))
                .ToList();

            var albums = allMetadata
                .Where(m => m.IsAlbumCover && !string.IsNullOrEmpty(m.AlbumName))
                .ToList();

            _logger.LogInformation("Found {SongCount} standalone songs and {AlbumCount} albums", songs.Count, albums.Count);

            // Generate sitemap XML
            var sitemapContent = GenerateSitemapXml(baseUrl, songs, albums);

            // Write to wwwroot/sitemap.xml
            var sitemapPath = Path.Combine(_environment.WebRootPath, "sitemap.xml");
            await File.WriteAllTextAsync(sitemapPath, sitemapContent);

            _logger.LogInformation("Sitemap generated successfully at {Path}", sitemapPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate sitemap");
            throw;
        }
    }

    /// <summary>
    /// Generates the sitemap XML content.
    /// </summary>
    private string GenerateSitemapXml(string baseUrl, List<Models.SongMetadata> songs, List<Models.SongMetadata> albums)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = Encoding.UTF8
        };

        using var stringWriter = new Utf8StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, settings);

        xmlWriter.WriteStartDocument();
        xmlWriter.WriteStartElement("urlset", SitemapNamespace);

        // Add home page
        WriteUrlEntry(xmlWriter, baseUrl, DateTime.UtcNow, "daily", "1.0");

        // Add song URLs
        foreach (var song in songs)
        {
            var songTitle = GetSongTitle(song);
            if (!string.IsNullOrEmpty(songTitle))
            {
                var songUrl = $"{baseUrl}/song/{Uri.EscapeDataString(songTitle)}";
                WriteUrlEntry(xmlWriter, songUrl, song.UpdatedAt, "weekly", "0.8");
            }
        }

        // Add album URLs
        foreach (var album in albums)
        {
            if (!string.IsNullOrEmpty(album.AlbumName))
            {
                var albumUrl = $"{baseUrl}/album/{Uri.EscapeDataString(album.AlbumName)}";
                WriteUrlEntry(xmlWriter, albumUrl, album.UpdatedAt, "weekly", "0.8");
            }
        }

        xmlWriter.WriteEndElement(); // urlset
        xmlWriter.WriteEndDocument();
        xmlWriter.Flush();

        return stringWriter.ToString();
    }

    /// <summary>
    /// Writes a URL entry to the sitemap.
    /// </summary>
    private void WriteUrlEntry(XmlWriter writer, string loc, DateTime lastMod, string changeFreq, string priority)
    {
        writer.WriteStartElement("url");

        writer.WriteElementString("loc", loc);
        writer.WriteElementString("lastmod", lastMod.ToString("yyyy-MM-dd"));
        writer.WriteElementString("changefreq", changeFreq);
        writer.WriteElementString("priority", priority);

        writer.WriteEndElement(); // url
    }

    /// <summary>
    /// Extracts the song title from the blob path.
    /// For standalone songs, the MP3 filename without extension is the title.
    /// </summary>
    private string GetSongTitle(Models.SongMetadata song)
    {
        if (string.IsNullOrEmpty(song.Mp3BlobPath))
        {
            return string.Empty;
        }

        // Extract filename from path (e.g., "songs/My Song.mp3" -> "My Song.mp3")
        var fileName = Path.GetFileName(song.Mp3BlobPath);

        // Remove extension (e.g., "My Song.mp3" -> "My Song")
        return Path.GetFileNameWithoutExtension(fileName);
    }

    /// <summary>
    /// A StringWriter that reports UTF-8 encoding instead of UTF-16.
    /// This is needed so that XmlWriter generates the correct encoding declaration.
    /// </summary>
    private class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
