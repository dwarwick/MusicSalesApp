using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MusicController : ControllerBase
    {
        private readonly IAzureStorageService _storageService;
        private readonly ISubscriptionService _subscriptionService;
        private readonly IStreamCountService _streamCountService;
        private readonly UserManager<ApplicationUser> _userManager;

        public MusicController(
            IAzureStorageService storageService,
            ISubscriptionService subscriptionService,
            IStreamCountService streamCountService,
            UserManager<ApplicationUser> userManager)
        {
            _storageService = storageService;
            _subscriptionService = subscriptionService;
            _streamCountService = streamCountService;
            _userManager = userManager;
        }

        // Legacy / fallback streaming endpoint (server proxy)
        [HttpGet("{*fileName}")]
        public async Task<IActionResult> Stream(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return BadRequest();

            var stream = await _storageService.OpenReadAsync(fileName);
            if (stream == null || stream.Length == 0)
                return NotFound();

            var contentType = NormalizeContentType(null, fileName);

            // Allow aggressive client/CDN caching for static media
            Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";

            return File(stream, contentType, enableRangeProcessing: true);
        }

        // Preferred: obtain a short-lived SAS URL so the browser can stream directly from Blob Storage
        // Non-subscribers and unauthenticated users get shorter-lived URLs (for preview only)
        // Subscribers get longer-lived URLs for full access
        [HttpGet("url/{*fileName}")]
        public async Task<IActionResult> GetStreamUrl(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return BadRequest();

            // Check if user is authenticated and has active subscription
            var user = await _userManager.GetUserAsync(User);
            bool hasAccess = false;
            
            if (user != null)
            {
                hasAccess = await _subscriptionService.HasActiveSubscriptionAsync(user.Id);
            }

            // Subscribers get 24 hour SAS URLs for full streaming
            // Non-subscribers get 2 hour SAS URLs (sufficient for preview but needs refresh for extended use)
            var lifetime = hasAccess ? TimeSpan.FromHours(24) : TimeSpan.FromHours(2);
            var uri = _storageService.GetReadSasUri(fileName, lifetime);

            return Ok(new { url = uri.ToString() });
        }

        /// <summary>
        /// Records a stream for a song. Called when a song has been played for at least the creator's configured continuous seconds.
        /// Creators streaming their own songs and admins do not generate paid stream counts.
        /// </summary>
        /// <param name="songMetadataId">The ID of the song metadata record.</param>
        /// <returns>The updated stream count.</returns>
        [HttpPost("stream/{songMetadataId:int}")]
        public async Task<IActionResult> RecordStream(int songMetadataId)
        {
            if (songMetadataId <= 0)
                return BadRequest(new { error = "Invalid song metadata ID" });

            var user = await _userManager.GetUserAsync(User);
            int? userId = user?.Id;
            bool isAdmin = user != null && await _userManager.IsInRoleAsync(user, Common.Helpers.Roles.Admin);

            var newCount = await _streamCountService.IncrementStreamCountAsync(songMetadataId, userId, isAdmin);
            
            return Ok(new { songMetadataId, streamCount = newCount });
        }

        /// <summary>
        /// Gets the stream count for a song.
        /// </summary>
        /// <param name="songMetadataId">The ID of the song metadata record.</param>
        /// <returns>The current stream count.</returns>
        [HttpGet("stream-count/{songMetadataId:int}")]
        public async Task<IActionResult> GetStreamCount(int songMetadataId)
        {
            if (songMetadataId <= 0)
                return BadRequest(new { error = "Invalid song metadata ID" });

            var count = await _streamCountService.GetStreamCountAsync(songMetadataId);
            
            return Ok(new { songMetadataId, streamCount = count });
        }

        /// <summary>
        /// Accepts a cropped image upload (as a binary blob from canvas) and stores it in Azure Blob Storage.
        /// The target blob path is supplied via query string. Only authenticated users may call this.
        /// </summary>
        [HttpPost("upload-cropped-image")]
        [Microsoft.AspNetCore.Authorization.Authorize]
        public async Task<IActionResult> UploadCroppedImage([FromQuery] string blobPath)
        {
            if (string.IsNullOrWhiteSpace(blobPath))
                return BadRequest(new { error = "blobPath is required" });

            // Reject path traversal
            if (blobPath.Contains("..") || blobPath.Contains("~"))
                return BadRequest(new { error = "Invalid blobPath" });

            if (Request.ContentLength == null || Request.ContentLength == 0)
                return BadRequest(new { error = "No image data provided" });

            // Max 10 MB
            if (Request.ContentLength > 10 * 1024 * 1024)
                return BadRequest(new { error = "Image too large" });

            // Buffer into a MemoryStream because Request.Body is non-seekable and
            // does not support .Length (AzureStorageService logs data.Length after upload).
            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            ms.Position = 0;

            await _storageService.UploadAsync(blobPath, ms, "image/png");
            return Ok(new { blobPath });
        }

        private static string NormalizeContentType(string original, string fileName)
        {
            if (!string.IsNullOrWhiteSpace(original) && original != "application/octet-stream")
                return original;

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".wav" => "audio/wav",
                ".mp3" => "audio/mpeg",
                ".ogg" => "audio/ogg",
                ".flac" => "audio/flac",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                _ => "application/octet-stream"
            };
        }
    }
}