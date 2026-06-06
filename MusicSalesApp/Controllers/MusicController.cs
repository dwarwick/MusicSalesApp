using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Common.Helpers;
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
        private readonly ISongMetadataService _songMetadataService;
        private readonly ISongLikeService _songLikeService;
        private readonly ICreatorPersonaService _creatorPersonaService;
        private readonly IReportedSongService _reportedSongService;
        private readonly IAppSettingsService _appSettingsService;
        private readonly IMobileSongMapper _songMapper;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<MusicController> _logger;

        public MusicController(
            IAzureStorageService storageService,
            ISubscriptionService subscriptionService,
            IStreamCountService streamCountService,
            ISongMetadataService songMetadataService,
            ISongLikeService songLikeService,
            ICreatorPersonaService creatorPersonaService,
            IReportedSongService reportedSongService,
            IAppSettingsService appSettingsService,
            IMobileSongMapper songMapper,
            UserManager<ApplicationUser> userManager,
            ILogger<MusicController> logger)
        {
            _storageService = storageService;
            _subscriptionService = subscriptionService;
            _streamCountService = streamCountService;
            _songMetadataService = songMetadataService;
            _songLikeService = songLikeService;
            _creatorPersonaService = creatorPersonaService;
            _reportedSongService = reportedSongService;
            _appSettingsService = appSettingsService;
            _songMapper = songMapper;
            _userManager = userManager;
            _logger = logger;
        }

        /// <summary>
        /// Returns all active songs with metadata, album art SAS URLs, and streaming SAS URLs.
        /// Used by the MAUI Android app to populate the music library.
        /// </summary>
        [HttpGet("songs")]
        [ResponseCache(Duration = 300)] // Cache for 5 minutes
        public async Task<IActionResult> GetSongs()
        {
            var allMetadata = await _songMetadataService.GetAllAsync();
            var sasLifetime = TimeSpan.FromHours(24);
            var defaultStreamQualifyingSeconds = await _appSettingsService.GetStreamQualifyingSecondsAsync();

            var songs = allMetadata
                .Where(m => !string.IsNullOrEmpty(m.Mp3BlobPath))
                .Select(m => _songMapper.MapToSongListItem(m, sasLifetime, defaultStreamQualifyingSeconds))
                .ToList();

            return Ok(songs);
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
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
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

            _logger.LogInformation("MusicController.RecordStream: songMetadataId={SongMetadataId}, userId={UserId}, isAdmin={IsAdmin}, userIsAuthenticated={IsAuthenticated}", 
                songMetadataId, userId, isAdmin, User?.Identity?.IsAuthenticated);

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

        /// <summary>
        /// Returns like and dislike counts for multiple songs in a single request.
        /// Used by the MAUI app to populate like/dislike counts on song cards.
        /// </summary>
        /// <param name="ids">Comma-separated song metadata IDs</param>
        [HttpGet("likes/bulk")]
        public async Task<IActionResult> GetBulkLikeCounts([FromQuery] string ids)
        {
            if (string.IsNullOrWhiteSpace(ids))
                return Ok(Array.Empty<object>());

            var songIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var id) ? id : -1)
                .Where(id => id > 0)
                .ToList();

            if (songIds.Count == 0)
                return Ok(Array.Empty<object>());

            var counts = await _songLikeService.GetBulkLikeDislikeCountsAsync(songIds);

            var result = songIds.Select(id =>
            {
                counts.TryGetValue(id, out var c);
                return new { songMetadataId = id, likeCount = c.likeCount, dislikeCount = c.dislikeCount };
            });

            return Ok(result);
        }

        /// <summary>
        /// Toggle like for a song. Requires authentication.
        /// </summary>
        [HttpPost("like/{songMetadataId:int}")]
        [Authorize]
        public async Task<IActionResult> ToggleLike(int songMetadataId)
        {
            if (songMetadataId <= 0)
                return BadRequest(new { error = "Invalid song metadata ID" });

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            var isLiked = await _songLikeService.ToggleLikeAsync(user.Id, songMetadataId);
            var (likeCount, dislikeCount) = await _songLikeService.GetLikeCountsAsync(songMetadataId);

            return Ok(new { isLiked, likeCount, dislikeCount });
        }

        /// <summary>
        /// Toggle dislike for a song. Requires authentication.
        /// </summary>
        [HttpPost("dislike/{songMetadataId:int}")]
        [Authorize]
        public async Task<IActionResult> ToggleDislike(int songMetadataId)
        {
            if (songMetadataId <= 0)
                return BadRequest(new { error = "Invalid song metadata ID" });

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            var isDisliked = await _songLikeService.ToggleDislikeAsync(user.Id, songMetadataId);
            var (likeCount, dislikeCount) = await _songLikeService.GetLikeCountsAsync(songMetadataId);

            return Ok(new { isDisliked, likeCount, dislikeCount });
        }

        /// <summary>
        /// Returns per-user like/dislike status for a batch of songs.
        /// </summary>
        [HttpGet("likes/user-status")]
        [Authorize]
        public async Task<IActionResult> GetBulkUserLikeStatus([FromQuery] string ids)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            var songIds = ParseIds(ids);
            if (songIds.Count == 0)
                return Ok(Array.Empty<object>());

            var statuses = await _songLikeService.GetBulkUserLikeStatusAsync(user.Id, songIds);

            var result = statuses.Select(kvp => new
            {
                songMetadataId = kvp.Key,
                userLikeStatus = kvp.Value
            });

            return Ok(result);
        }

        /// <summary>
        /// Returns a single song DTO by title (for deep linking from shared URLs).
        /// </summary>
        [HttpGet("song-by-title/{*title}")]
        public async Task<IActionResult> GetSongByTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return BadRequest(new { error = "Title is required" });

            var decodedTitle = Uri.UnescapeDataString(title);
            var allSongs = await _songMetadataService.GetAllAsync();
            var song = allSongs.FirstOrDefault(s =>
                s.Mp3BlobPath != null &&
                string.Equals(s.SongTitle, decodedTitle, StringComparison.OrdinalIgnoreCase));

            if (song == null)
                return NotFound(new { error = "Song not found" });

            var sasLifetime = TimeSpan.FromHours(24);
            var defaultStreamQualifyingSeconds = await _appSettingsService.GetStreamQualifyingSecondsAsync();
            var mappedSong = _songMapper.MapToSongListItem(song, sasLifetime, defaultStreamQualifyingSeconds);
            mappedSong.StreamUrl = _storageService.GetReadSasUri(song.Mp3BlobPath!, TimeSpan.FromHours(2)).ToString();

            var streamCount = await _streamCountService.GetStreamCountAsync(song.Id);
            var (likeCount, dislikeCount) = await _songLikeService.GetLikeCountsAsync(song.Id);

            return Ok(new
            {
                id = mappedSong.Id,
                songTitle = mappedSong.SongTitle,
                artistName = mappedSong.ArtistName,
                genre = mappedSong.Genre,
                albumArtUrl = mappedSong.AlbumArtUrl,
                personaImageUrl = mappedSong.PersonaImageUrl,
                personaBio = mappedSong.PersonaBio,
                streamUrl = mappedSong.StreamUrl,
                streamCount,
                streamQualifyingSeconds = mappedSong.StreamQualifyingSeconds,
                trackLengthSeconds = mappedSong.TrackLengthSeconds,
                displayOnHomePage = mappedSong.DisplayOnHomePage,
                displayOrder = mappedSong.DisplayOrder,
                creatorId = mappedSong.CreatorId,
                creatorUserId = mappedSong.CreatorUserId,
                likeCount,
                dislikeCount
            });
        }

        private static List<int> ParseIds(string idsString)
        {
            if (string.IsNullOrWhiteSpace(idsString))
                return [];

            return idsString
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var id) ? id : -1)
                .Where(id => id > 0)
                .ToList();
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

        [HttpPost("report/{songMetadataId:int}")]
        [Authorize(Policy = Permissions.ValidatedUser)]
        public async Task<IActionResult> ReportSong(int songMetadataId, [FromBody] ReportSongRequest request)
        {
            if (songMetadataId <= 0)
                return BadRequest(new { error = "Invalid song metadata ID" });

            if (request == null || string.IsNullOrWhiteSpace(request.Reason))
                return BadRequest(new { error = "Reason is required" });

            if (!ReportReasonTypes.All.Contains(request.Reason))
                return BadRequest(new { error = "Invalid report reason" });

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            try
            {
                await _reportedSongService.ReportSongAsync(user.Id, songMetadataId, request.Reason);
                return Ok(new { message = "Report submitted successfully" });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already reported"))
            {
                return Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { error = ex.Message });
            }
        }

        public class ReportSongRequest
        {
            public string Reason { get; set; } = string.Empty;
        }
    }
}
