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
        private readonly ICartService _cartService;
        private readonly IStreamCountService _streamCountService;
        private readonly UserManager<ApplicationUser> _userManager;

        public MusicController(
            IAzureStorageService storageService,
            ICartService cartService,
            IStreamCountService streamCountService,
            UserManager<ApplicationUser> userManager)
        {
            _storageService = storageService;
            _cartService = cartService;
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
        // Non-owners and unauthenticated users get shorter-lived URLs (for preview only)
        // Owners get longer-lived URLs for full access
        [HttpGet("url/{*fileName}")]
        public async Task<IActionResult> GetStreamUrl(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return BadRequest();

            // Check if user is authenticated and owns the song
            var user = await _userManager.GetUserAsync(User);
            bool ownsContent = false;
            
            if (user != null)
            {
                ownsContent = await _cartService.UserOwnsSongAsync(user.Id, fileName);
            }

            // Owners get 24 hour SAS URLs for full streaming
            // Non-owners get 2 hour SAS URLs (sufficient for preview but needs refresh for extended use)
            var lifetime = ownsContent ? TimeSpan.FromHours(24) : TimeSpan.FromHours(2);
            var uri = _storageService.GetReadSasUri(fileName, lifetime);

            return Ok(new { url = uri.ToString() });
        }

        /// <summary>
        /// Records a stream for a song. Called when a song has been played for at least 30 continuous seconds.
        /// </summary>
        /// <param name="songMetadataId">The ID of the song metadata record.</param>
        /// <returns>The updated stream count.</returns>
        [HttpPost("stream/{songMetadataId:int}")]
        public async Task<IActionResult> RecordStream(int songMetadataId)
        {
            if (songMetadataId <= 0)
                return BadRequest(new { error = "Invalid song metadata ID" });

            var newCount = await _streamCountService.IncrementStreamCountAsync(songMetadataId);
            
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