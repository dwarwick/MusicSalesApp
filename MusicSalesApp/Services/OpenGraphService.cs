using Microsoft.Extensions.Configuration;
using MusicSalesApp.Models;
using System.Text;
using System.Web;

namespace MusicSalesApp.Services;

/// <summary>
/// Service for generating Open Graph meta tags for social media sharing
/// </summary>
public class OpenGraphService : IOpenGraphService
{
    private readonly ISongMetadataService _songMetadataService;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public OpenGraphService(
        ISongMetadataService songMetadataService,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
    {
        _songMetadataService = songMetadataService;
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Generates Open Graph meta tags for a song page
    /// </summary>
    public async Task<string> GenerateSongMetaTagsAsync(string songTitle)
    {
        if (string.IsNullOrWhiteSpace(songTitle))
        {
            return string.Empty;
        }

        try
        {
            // URL decode the song title
            var decodedTitle = Uri.UnescapeDataString(songTitle);
            
            // Find song metadata by matching MP3 filename (without extension)
            var allMetadata = await _songMetadataService.GetAllAsync();
            var songMetadata = allMetadata.FirstOrDefault(m => 
                !string.IsNullOrEmpty(m.Mp3BlobPath) && 
                string.IsNullOrEmpty(m.AlbumName) && // Standalone song only
                Path.GetFileNameWithoutExtension(m.Mp3BlobPath) == decodedTitle);

            if (songMetadata == null)
            {
                return string.Empty;
            }

            // Find the associated image for this song using BlobPath or ImageBlobPath
            var imageMetadata = allMetadata.FirstOrDefault(m =>
                !string.IsNullOrEmpty(m.ImageBlobPath) &&
                !m.IsAlbumCover &&
                Path.GetFileNameWithoutExtension(m.ImageBlobPath) == decodedTitle);

            // Use the full blob path for the image URL
            string imageUrl;
            if (imageMetadata != null)
            {
                var imagePath = !string.IsNullOrEmpty(imageMetadata.ImageBlobPath) 
                    ? imageMetadata.ImageBlobPath 
                    : imageMetadata.BlobPath;
                imageUrl = GetAbsoluteUrl($"/api/music/{SafeEncodePath(imagePath)}");
            }
            else
            {
                imageUrl = GetAbsoluteUrl("/favicon.ico");
            }

            var tags = new Dictionary<string, string>
            {
                ["fb:app_id"] = _configuration["Facebook:AppId"] ?? "",
                ["og:url"] = GetCurrentUrl(),
                ["og:type"] = "music.song",
                ["og:title"] = decodedTitle,
                ["og:image"] = imageUrl,
                ["og:description"] = $"Listen to {decodedTitle} on StreamTunes"
            };

            if (!string.IsNullOrEmpty(songMetadata.Genre))
            {
                tags["music:genre"] = songMetadata.Genre;
            }

            if (songMetadata.TrackLength.HasValue)
            {
                tags["music:duration"] = ((int)songMetadata.TrackLength.Value).ToString();
            }

            return GenerateMetaTagsHtml(tags);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Generates Open Graph meta tags for an album page
    /// </summary>
    public async Task<string> GenerateAlbumMetaTagsAsync(string albumName)
    {
        if (string.IsNullOrWhiteSpace(albumName))
        {
            return string.Empty;
        }

        try
        {
            // URL decode the album name
            var decodedAlbumName = Uri.UnescapeDataString(albumName);

            // Find album metadata
            var albumMetadata = await _songMetadataService.GetByAlbumNameAsync(decodedAlbumName);
            
            if (albumMetadata == null || !albumMetadata.Any())
            {
                return string.Empty;
            }

            // Find the album cover image
            var coverImage = albumMetadata.FirstOrDefault(m => m.IsAlbumCover);
            var albumTracks = albumMetadata.Where(m => !string.IsNullOrEmpty(m.Mp3BlobPath)).ToList();

            // Use the full blob path for the image URL
            string imageUrl;
            if (coverImage != null)
            {
                var imagePath = !string.IsNullOrEmpty(coverImage.ImageBlobPath) 
                    ? coverImage.ImageBlobPath 
                    : coverImage.BlobPath;
                imageUrl = GetAbsoluteUrl($"/api/music/{SafeEncodePath(imagePath)}");
            }
            else
            {
                imageUrl = GetAbsoluteUrl("/favicon.ico");
            }

            var tags = new Dictionary<string, string>
            {
                ["fb:app_id"] = _configuration["Facebook:AppId"] ?? "",
                ["og:url"] = GetCurrentUrl(),
                ["og:type"] = "music.album",
                ["og:title"] = decodedAlbumName,
                ["og:image"] = imageUrl,
                ["og:description"] = $"Listen to {decodedAlbumName} album with {albumTracks.Count} track{(albumTracks.Count != 1 ? "s" : "")} on StreamTunes"
            };

            // Add genre from first track
            var firstTrack = albumTracks.FirstOrDefault();
            if (firstTrack != null && !string.IsNullOrEmpty(firstTrack.Genre))
            {
                tags["music:genre"] = firstTrack.Genre;
            }

            return GenerateMetaTagsHtml(tags);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private string GenerateMetaTagsHtml(Dictionary<string, string> tags)
    {
        var sb = new StringBuilder();
        
        foreach (var tag in tags)
        {
            var attribute = tag.Key.StartsWith("og:") || tag.Key.StartsWith("fb:") || tag.Key.StartsWith("music:") 
                ? "property" 
                : "name";
            
            // Escape values for HTML
            var escapedValue = HttpUtility.HtmlEncode(tag.Value);
            sb.AppendLine($"<meta {attribute}=\"{tag.Key}\" content=\"{escapedValue}\">");
        }
        
        return sb.ToString();
    }

    private string GetCurrentUrl()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null)
        {
            return "https://streamtunes.net";
        }

        return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}";
    }

    private string GetAbsoluteUrl(string relativePath)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null)
        {
            return $"https://streamtunes.net{relativePath}";
        }

        return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{relativePath}";
    }

    private string SafeEncodePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;

        // Check for path traversal attempts
        if (filePath.Contains("..") || filePath.Contains("~"))
            return string.Empty;

        // Split by forward slash, encode each segment, then rejoin
        var segments = filePath.Split('/');
        var encodedSegments = segments.Select(s => Uri.EscapeDataString(s));
        return string.Join("/", encodedSegments);
    }
}
