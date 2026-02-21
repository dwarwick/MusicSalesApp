using Microsoft.Extensions.Configuration;
using MusicSalesApp.Models;
using SkiaSharp;
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
    private readonly IAzureStorageService _storageService;
    private readonly ILogger<OpenGraphService> _logger;

    /// <summary>
    /// Facebook recommended image dimensions for og:image (1.91:1 ratio)
    /// </summary>
    internal const int FacebookImageWidth = 1200;
    internal const int FacebookImageHeight = 630;

    public OpenGraphService(
        ISongMetadataService songMetadataService,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        IAzureStorageService storageService,
        ILogger<OpenGraphService> logger)
    {
        _songMetadataService = songMetadataService;
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
        _storageService = storageService;
        _logger = logger;
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
            
            // Find song metadata by matching stored title or MP3 filename (without extension)
            var allMetadata = await _songMetadataService.GetAllAsync();
            var songMetadata = allMetadata.FirstOrDefault(m => 
                !string.IsNullOrEmpty(m.Mp3BlobPath) && 
                string.IsNullOrEmpty(m.AlbumName) && // Standalone song only
                ((!string.IsNullOrEmpty(m.SongTitle) && m.SongTitle.Equals(decodedTitle, StringComparison.OrdinalIgnoreCase)) ||
                 Path.GetFileNameWithoutExtension(m.Mp3BlobPath).Equals(decodedTitle, StringComparison.OrdinalIgnoreCase)));

            if (songMetadata == null)
            {
                return string.Empty;
            }

            // Find the associated image for this song using BlobPath or ImageBlobPath
            // First try using the image from the song metadata record itself
            string imageBlobPath = null;
            if (!string.IsNullOrEmpty(songMetadata.ImageBlobPath))
            {
                imageBlobPath = songMetadata.ImageBlobPath;
            }
            else
            {
                // Fall back to searching for a separate image record matching the decoded title
                var imageMetadata = allMetadata.FirstOrDefault(m =>
                    !string.IsNullOrEmpty(m.ImageBlobPath) &&
                    !m.IsAlbumCover &&
                    Path.GetFileNameWithoutExtension(m.ImageBlobPath).Equals(decodedTitle, StringComparison.OrdinalIgnoreCase));
                if (imageMetadata != null)
                {
                    imageBlobPath = imageMetadata.ImageBlobPath;
                }
            }

            // Get Facebook-optimized image URL (or fall back to original/favicon)
            string imageUrl;
            bool isFacebookImage = false;
            if (!string.IsNullOrEmpty(imageBlobPath))
            {
                var fbImagePath = await GetOrCreateFacebookImageAsync(imageBlobPath);
                imageUrl = GetAbsoluteUrl($"/api/music/{SafeEncodePath(fbImagePath)}");
                isFacebookImage = fbImagePath != imageBlobPath;
            }
            else
            {
                imageUrl = GetAbsoluteUrl("/favicon.ico");
            }

            var displayTitle = !string.IsNullOrEmpty(songMetadata.SongTitle) ? songMetadata.SongTitle : decodedTitle;

            var tags = new Dictionary<string, string>
            {
                ["fb:app_id"] = _configuration["Facebook:AppId"] ?? "",
                ["og:url"] = GetCurrentUrl(),
                ["og:type"] = "music.song",
                ["og:title"] = displayTitle,
                ["og:image"] = imageUrl,
                ["og:image:width"] = isFacebookImage ? FacebookImageWidth.ToString() : "",
                ["og:image:height"] = isFacebookImage ? FacebookImageHeight.ToString() : "",
                ["og:description"] = $"Listen to {displayTitle} on StreamTunes"
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
            bool isFacebookImage = false;
            if (coverImage != null)
            {
                var imagePath = !string.IsNullOrEmpty(coverImage.ImageBlobPath) 
                    ? coverImage.ImageBlobPath 
                    : coverImage.BlobPath;
                var fbImagePath = await GetOrCreateFacebookImageAsync(imagePath);
                imageUrl = GetAbsoluteUrl($"/api/music/{SafeEncodePath(fbImagePath)}");
                isFacebookImage = fbImagePath != imagePath;
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
                ["og:image:width"] = isFacebookImage ? FacebookImageWidth.ToString() : "",
                ["og:image:height"] = isFacebookImage ? FacebookImageHeight.ToString() : "",
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

    /// <summary>
    /// Gets the Facebook-optimized image path, creating it if it doesn't exist.
    /// Facebook recommends 1200×630 pixels (1.91:1 ratio) for og:image.
    /// The original image is centered on a black canvas without stretching.
    /// </summary>
    internal async Task<string> GetOrCreateFacebookImageAsync(string originalImagePath)
    {
        if (string.IsNullOrEmpty(originalImagePath))
            return originalImagePath;

        var fbImagePath = GetFacebookImagePath(originalImagePath);

        try
        {
            // Check if the Facebook-optimized image already exists
            if (await _storageService.ExistsAsync(fbImagePath))
            {
                return fbImagePath;
            }

            // Download the original image
            using var originalStream = await _storageService.DownloadAsync(originalImagePath);
            if (originalStream == null || originalStream.Length == 0)
            {
                return originalImagePath;
            }

            // Create Facebook-optimized image
            using var fbImageStream = CreateFacebookImage(originalStream);
            if (fbImageStream == null)
            {
                return originalImagePath;
            }

            // Upload the Facebook-optimized image
            await _storageService.UploadAsync(fbImagePath, fbImageStream, "image/png");
            return fbImagePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create Facebook-optimized image for {Path}, using original", originalImagePath);
            return originalImagePath;
        }
    }

    /// <summary>
    /// Generates the blob path for a Facebook-optimized version of an image.
    /// Example: "folder/image.jpg" → "folder/image_fb.png"
    /// </summary>
    internal static string GetFacebookImagePath(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath)?.Replace('\\', '/') ?? "";
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(originalPath);
        var fbFileName = $"{fileNameWithoutExt}_fb.png";
        return string.IsNullOrEmpty(directory) ? fbFileName : $"{directory}/{fbFileName}";
    }

    /// <summary>
    /// Creates a 1200×630 Facebook-optimized image by centering the original
    /// on a black canvas without stretching.
    /// </summary>
    internal static MemoryStream CreateFacebookImage(Stream originalStream)
    {
        SKBitmap original;
        try
        {
            original = SKBitmap.Decode(originalStream);
        }
        catch
        {
            return null;
        }

        if (original == null)
            return null;

        using (original)
        {
            using var surface = SKSurface.Create(new SKImageInfo(FacebookImageWidth, FacebookImageHeight));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            // Scale the original to fit within the Facebook dimensions while maintaining aspect ratio
            float scaleX = (float)FacebookImageWidth / original.Width;
            float scaleY = (float)FacebookImageHeight / original.Height;
            float scale = Math.Min(scaleX, scaleY);

            int scaledWidth = (int)(original.Width * scale);
            int scaledHeight = (int)(original.Height * scale);

            // Center the image on the canvas
            int offsetX = (FacebookImageWidth - scaledWidth) / 2;
            int offsetY = (FacebookImageHeight - scaledHeight) / 2;

            var destRect = new SKRect(offsetX, offsetY, offsetX + scaledWidth, offsetY + scaledHeight);
            var sourceRect = new SKRect(0, 0, original.Width, original.Height);
            var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
            using var skImage = SKImage.FromBitmap(original);
            canvas.DrawImage(skImage, sourceRect, destRect, sampling);

            // Encode as PNG (lossless)
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Position = 0;
            return ms;
        }
    }

    private string GenerateMetaTagsHtml(Dictionary<string, string> tags)
    {
        var sb = new StringBuilder();
        
        foreach (var tag in tags)
        {
            // Skip tags with empty values
            if (string.IsNullOrEmpty(tag.Value))
                continue;

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
