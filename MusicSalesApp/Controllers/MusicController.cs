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
        /// <summary>
        /// Response for an opinion written against a song that no longer exists. Returned as 400 on
        /// every like route, never 404 or 500, and the choice is load-bearing: the MAUI app reads a 404
        /// on <c>like-state</c> as "this server predates the endpoint" and falls back to the toggles,
        /// while it retries any 5xx forever. Its flush stops at the first failure, so a permanent error
        /// dressed up as a transient one strands every queued intent behind it.
        /// </summary>
        internal const string SongNotFoundError = "Song not found";

        /// <summary>
        /// Returned as a 403 body when the caller tries to rate a song they have never streamed.
        ///
        /// 403 rather than 400 so the client can tell "you are not allowed to do this" from "that song
        /// does not exist" - the mobile queue drops both permanently, but only one of them is worth
        /// explaining to the user.
        /// </summary>
        internal const string LikeRequiresStreamError = "Listen to this song before rating it";

        /// <summary>
        /// Upper bound on a bulk like request. Set well above any plausible catalogue - the point is to
        /// stop an unbounded list becoming an unbounded query, not to constrain real clients. The old
        /// GET form had no cap because IIS imposed one for it; the POST form has no such accident.
        /// </summary>
        internal const int MaxBulkSongIds = 10_000;

        internal const string TooManySongIdsError = "Too many song IDs in one request";

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
        private readonly ICreatorService _creatorService;
        private readonly IAuthorizationService _authorizationService;
        private readonly IImageVariantCoordinator _imageVariants;
        private readonly ISongLyricsService _lyricsService;
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
            ICreatorService creatorService,
            IAuthorizationService authorizationService,
            IImageVariantCoordinator imageVariants,
            ISongLyricsService lyricsService,
            ILogger<MusicController> logger)
        {
            _creatorService = creatorService;
            _authorizationService = authorizationService;
            _imageVariants = imageVariants;
            _lyricsService = lyricsService;
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
            var streamQualifying = await _appSettingsService.GetStreamQualifyingSettingsAsync();

            var playable = allMetadata
                .Where(m => !string.IsNullOrEmpty(m.Mp3BlobPath))
                .ToList();

            // One query for the whole page rather than one per song, the same shape the web's
            // library uses. Only the published ones come back with a usable path; the mapper
            // does that gating so no caller has to remember it.
            // Coalesced, and that is not defensive noise: this feeds a TryGetValue for every song
            // on the page, so a null here would turn a missing lyrics column into an empty song
            // list. Lyrics are supplementary to the catalogue - a failure costs the lyrics, not
            // the songs.
            var lyrics = await _lyricsService.GetForSongsAsync(playable.Select(m => m.Id))
                         ?? new Dictionary<int, SongLyrics>();

            var songs = playable
                .Select(m => _songMapper.MapToSongListItem(
                    m,
                    sasLifetime,
                    streamQualifying,
                    lyrics.TryGetValue(m.Id, out var songLyrics) ? songLyrics : null))
                .ToList();

            return Ok(songs);
        }

        // Legacy / fallback streaming endpoint (server proxy)
        [HttpGet("{*fileName}")]
        public async Task<IActionResult> Stream(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return BadRequest();

            if (!await IsRegisteredPublicMediaPathAsync(fileName))
                return NotFound();

            var stream = await _storageService.OpenReadAsync(fileName);
            var contentTypeSource = fileName;

            if ((stream == null || stream.Length == 0)
                && ImageVariantPaths.TryParseVariant(fileName, out var masterPath, out _))
            {
                // A rendition can be legitimately absent: mid-backfill, or restored from a backup
                // taken before the backfill ran. Serve the full-size master instead of 404ing -
                // a browser does not fall back to another srcset candidate on a 404, it just shows
                // a broken image. Slower, but correct, and it makes the whole feature fail soft.
                stream = await _storageService.OpenReadAsync(masterPath);
                contentTypeSource = masterPath;
            }

            if (stream == null || stream.Length == 0)
                return NotFound();

            var contentType = NormalizeContentType(null, contentTypeSource);

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

            if (!await IsRegisteredPublicMediaPathAsync(fileName))
                return NotFound();

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

        private async Task<bool> IsRegisteredPublicMediaPathAsync(string fileName)
        {
            // Lyrics artifacts are checked first and separately, because they cannot be resolved the
            // way everything else here is: their paths live on SongLyrics, not on SongMetadata, so
            // GetByBlobPathAsync below would simply not find them and the request would 404 before
            // any decision was reached.
            //
            // The gate is that row's STATUS, never the shape of the path. Timings held back as
            // NeedsReview sit at exactly the same path as published ones - the path does not change
            // between runs, the version does - so admitting anything that merely looks like a lyrics
            // artifact would serve every low-confidence alignment the pipeline deliberately withheld.
            if (SongMediaPaths.IsLyricsArtifactPath(fileName))
            {
                return await _lyricsService.IsPubliclyReadableAsync(fileName);
            }

            // A rendition path is its master's path with ".w{width}.webp" appended, so the master is
            // recoverable by string alone and the existing lookup works unchanged - no extra query,
            // no new index.
            var isVariant = ImageVariantPaths.TryParseVariant(fileName, out var basePath, out var width);
            var lookupPath = isVariant ? basePath : fileName;

            var metadata = await _songMetadataService.GetByBlobPathAsync(lookupPath);
            if (metadata is not { IsActive: true, IsEnabled: true })
                return false;

            if (!isVariant)
            {
                return string.Equals(metadata.Mp3BlobPath, fileName, StringComparison.Ordinal)
                    || string.Equals(metadata.ImageBlobPath, fileName, StringComparison.Ordinal);
            }

            // A rendition is public only when its master is this song's registered cover art, which
            // rules out a forged "{guid}-music.mp3.w320.webp".
            if (!string.Equals(metadata.ImageBlobPath, basePath, StringComparison.Ordinal))
                return false;

            // Ladder widths are accepted unconditionally, and deliberately not by consulting the
            // song's recorded set: during a backfill the blobs exist before the row is updated, and
            // checking the row would 404 renditions sitting right there in storage.
            //
            // The recorded set is then consulted as well, because a master narrower than the smallest
            // rung yields one rendition at its own, non-ladder width. Those are real, they are the
            // only candidate in that song's srcset, and a browser shows a broken image rather than
            // falling back to src when one 404s.
            return ImageVariantSizes.IsKnownCoverArtWidth(width)
                || ImageVariantSizes.CsvContains(metadata.CoverArtVariantWidths, width);
        }

        /// <summary>
        /// Records a stream for a song. Called when a song has been played for at least the creator's configured continuous seconds.
        /// Creators streaming their own songs do not generate paid stream counts; admin streams do count.
        /// </summary>
        /// <remarks>
        /// Anonymous listeners may record streams for featured songs, so the endpoint stays
        /// [AllowAnonymous]. The explicit [Authorize] schemes are what make the MAUI app's Bearer
        /// token authenticate here - the app's default authenticate scheme is the Identity cookie,
        /// so without this the JWT is ignored and every mobile stream is recorded with no user.
        /// AuthorizationMiddleware authenticates the listed schemes and populates HttpContext.User
        /// before it honours [AllowAnonymous] and skips the requirement.
        /// </remarks>
        /// <param name="songMetadataId">The ID of the song metadata record.</param>
        /// <returns>The updated stream count.</returns>
        [HttpPost("stream/{songMetadataId:int}")]
        [Authorize(AuthenticationSchemes = "Identity.Application,Bearer")]
        [AllowAnonymous]
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
        /// Accepts a cropped cover-art upload (as a binary blob from canvas) and stores it in
        /// Azure Blob Storage.
        ///
        /// The destination is derived from the song's own record rather than supplied by the
        /// caller, so an authenticated user cannot write to an arbitrary blob path. Callers must
        /// either hold the song-management permission or own the song.
        /// </summary>
        [HttpPost("upload-cropped-image")]
        [Microsoft.AspNetCore.Authorization.Authorize]
        public async Task<IActionResult> UploadCroppedImage([FromQuery] int songMetadataId)
        {
            if (songMetadataId <= 0)
                return BadRequest(new { error = "songMetadataId is required" });

            if (Request.ContentLength == null || Request.ContentLength == 0)
                return BadRequest(new { error = "No image data provided" });

            // Max 10 MB
            if (Request.ContentLength > 10 * 1024 * 1024)
                return BadRequest(new { error = "Image too large" });

            var song = await _songMetadataService.GetByIdAsync(songMetadataId);
            if (song == null)
                return NotFound(new { error = "Song not found" });

            if (!await CanManageSongAsync(song))
                return Forbid();

            var blobPath = SongMediaPaths.ResolveCoverArtTarget(
                song.MediaGuid,
                song.ImageBlobPath,
                song.Mp3BlobPath,
                SongTitleHelper.GetEffectiveTitle(song.SongTitle, song.Mp3BlobPath, song.BlobPath),
                ".png");

            // Defence in depth: the path is server-derived, but the legacy branch still
            // interpolates stored values.
            if (string.IsNullOrWhiteSpace(blobPath) || blobPath.Contains("..") || blobPath.Contains("~"))
                return BadRequest(new { error = "Invalid destination for this song" });

            // Buffer into a MemoryStream because Request.Body is non-seekable and
            // does not support .Length (AzureStorageService logs data.Length after upload).
            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            ms.Position = 0;

            await _storageService.UploadAsync(blobPath, ms, "image/png");

            // Under the GUID scheme the crop target is the song's *current* cover-art path, so the
            // upload above has already replaced the artwork the whole site is serving - before the
            // edit dialog is saved, and whether or not it ever is. Refresh here so the renditions and
            // the version match the bytes now in storage; abandoning the dialog would otherwise leave
            // every srcset candidate and every mobile cache entry showing the pre-crop image forever,
            // with no version change to invalidate either.
            //
            // The legacy branch resolves to a path the song row does not reference yet, so there is
            // nothing being served to keep consistent and the save is still the right moment.
            if (string.Equals(blobPath, song.ImageBlobPath, StringComparison.Ordinal))
                await _imageVariants.RefreshCoverArtVariantsAsync(song.Id);

            return Ok(new { blobPath });
        }

        /// <summary>
        /// Whether the current user may replace this song's cover art: admins and song managers
        /// may edit any song, creators only their own.
        /// </summary>
        private async Task<bool> CanManageSongAsync(SongMetadata song)
        {
            if (User.IsInRole("Admin")
                || (await _authorizationService.AuthorizeAsync(User, Permissions.ManageSongs)).Succeeded
                || (await _authorizationService.AuthorizeAsync(User, Permissions.ManageAllCreatorSongs)).Succeeded)
            {
                return true;
            }

            if (song.CreatorId == null)
                return false;

            // Read the id from the principal's claims rather than loading the whole user row.
            if (!int.TryParse(_userManager.GetUserId(User), out var userId))
                return false;

            var creatorId = await _creatorService.GetCreatorIdForUserAsync(userId);
            return creatorId != null && creatorId == song.CreatorId;
        }

        /// <summary>
        /// Returns like and dislike counts for multiple songs in a single request.
        /// Used by the MAUI app to populate like/dislike counts on song cards.
        /// </summary>
        /// <param name="ids">Comma-separated song metadata IDs</param>
        [HttpGet("likes/bulk")]
        public Task<IActionResult> GetBulkLikeCounts([FromQuery] string ids)
            => BuildBulkLikeCountsAsync(ParseIds(ids));

        /// <summary>
        /// Body-based form of <see cref="GetBulkLikeCounts"/>.
        ///
        /// The catalogue is sent whole, one ID per song, and a query string cannot carry it: IIS request
        /// filtering caps a query at 2048 characters by default, which a five-digit ID list exhausts at
        /// roughly 340 songs - and the failure is a 404 from the request filter, not something the app
        /// can read as "too many". The GET stays for apps built before this route existed.
        /// </summary>
        [HttpPost("likes/bulk")]
        public Task<IActionResult> PostBulkLikeCounts([FromBody] BulkSongIdsRequest request)
            => BuildBulkLikeCountsAsync(NormalizeIds(request?.Ids));

        private async Task<IActionResult> BuildBulkLikeCountsAsync(List<int> songIds)
        {
            if (songIds.Count == 0)
                return Ok(Array.Empty<object>());

            if (songIds.Count > MaxBulkSongIds)
                return BadRequest(new { error = TooManySongIdsError });

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

            bool isLiked;
            try
            {
                isLiked = await _songLikeService.ToggleLikeAsync(user.Id, songMetadataId);
            }
            catch (SongNotFoundException)
            {
                return BadRequest(new { error = SongNotFoundError });
            }
            catch (LikeRequiresStreamException)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = LikeRequiresStreamError });
            }

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

            bool isDisliked;
            try
            {
                isDisliked = await _songLikeService.ToggleDislikeAsync(user.Id, songMetadataId);
            }
            catch (SongNotFoundException)
            {
                return BadRequest(new { error = SongNotFoundError });
            }
            catch (LikeRequiresStreamException)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = LikeRequiresStreamError });
            }

            var (likeCount, dislikeCount) = await _songLikeService.GetLikeCountsAsync(songMetadataId);

            return Ok(new { isDisliked, likeCount, dislikeCount });
        }

        /// <summary>
        /// Idempotently sets the caller's like/dislike state for a song. Requires authentication.
        /// Unlike the toggle endpoints above, the outcome depends only on the requested state, so the
        /// mobile app can safely replay a queued offline intent without flipping the wrong way.
        /// </summary>
        [HttpPut("like-state/{songMetadataId:int}")]
        [Authorize]
        public async Task<IActionResult> SetLikeState(int songMetadataId, [FromBody] SetLikeStateRequest request)
        {
            if (songMetadataId <= 0)
                return BadRequest(new { error = "Invalid song metadata ID" });

            if (request == null)
                return BadRequest(new { error = "Request body is required" });

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            bool? userLikeStatus;
            try
            {
                userLikeStatus = await _songLikeService.SetLikeStateAsync(user.Id, songMetadataId, request.Status);
            }
            catch (SongNotFoundException)
            {
                return BadRequest(new { error = SongNotFoundError });
            }
            catch (LikeRequiresStreamException)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = LikeRequiresStreamError });
            }

            var (likeCount, dislikeCount) = await _songLikeService.GetLikeCountsAsync(songMetadataId);

            return Ok(new { userLikeStatus, likeCount, dislikeCount });
        }

        /// <summary>
        /// Returns per-user like/dislike status for a batch of songs, plus whether the caller has streamed
        /// each one - which is what decides whether they may rate it at all.
        ///
        /// One entry per requested ID, including songs the caller has never rated: an unrated song is
        /// exactly the case where the client needs to know whether the buttons should be live, so the
        /// sparse "only what you have rated" shape this used to return carried no signal for it.
        /// </summary>
        [HttpGet("likes/user-status")]
        [Authorize]
        public Task<IActionResult> GetBulkUserLikeStatus([FromQuery] string ids)
            => BuildBulkUserLikeStatusAsync(ParseIds(ids));

        /// <summary>
        /// Body-based form of <see cref="GetBulkUserLikeStatus"/>. Same reasoning as
        /// <see cref="PostBulkLikeCounts"/>: the ID list outgrows a query string.
        /// </summary>
        [HttpPost("likes/user-status")]
        [Authorize]
        public Task<IActionResult> PostBulkUserLikeStatus([FromBody] BulkSongIdsRequest request)
            => BuildBulkUserLikeStatusAsync(NormalizeIds(request?.Ids));

        private async Task<IActionResult> BuildBulkUserLikeStatusAsync(List<int> songIds)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            if (songIds.Count == 0)
                return Ok(Array.Empty<object>());

            if (songIds.Count > MaxBulkSongIds)
                return BadRequest(new { error = TooManySongIdsError });

            var statuses = await _songLikeService.GetBulkUserLikeStatusAsync(user.Id, songIds);
            var streamedSongIds = await _streamCountService.GetUserStreamedSongIdsAsync(user.Id, songIds);

            var result = songIds.Select(id => new
            {
                songMetadataId = id,
                userLikeStatus = statuses.TryGetValue(id, out var status) ? status : null,
                hasStreamed = streamedSongIds.Contains(id)
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
                string.Equals(
                    SongTitleHelper.GetEffectiveTitle(s.SongTitle, s.Mp3BlobPath, s.BlobPath),
                    decodedTitle,
                    StringComparison.OrdinalIgnoreCase));

            if (song == null)
                return NotFound(new { error = "Song not found" });

            var sasLifetime = TimeSpan.FromHours(24);
            var streamQualifying = await _appSettingsService.GetStreamQualifyingSettingsAsync();
            var songLyrics = await _lyricsService.GetForSongAsync(song.Id);
            var mappedSong = _songMapper.MapToSongListItem(song, sasLifetime, streamQualifying, songLyrics);
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
                albumArtThumbUrl = mappedSong.AlbumArtThumbUrl,
                albumArtHeroUrl = mappedSong.AlbumArtHeroUrl,
                albumArtVersion = mappedSong.AlbumArtVersion,
                personaImageUrl = mappedSong.PersonaImageUrl,
                personaImageThumbUrl = mappedSong.PersonaImageThumbUrl,
                personaImageHeroUrl = mappedSong.PersonaImageHeroUrl,
                personaImageVersion = mappedSong.PersonaImageVersion,
                personaBio = mappedSong.PersonaBio,
                personaWebsiteUrl = mappedSong.PersonaWebsiteUrl,
                lyricsTimingsPath = mappedSong.LyricsTimingsPath,
                lyricsVersion = mappedSong.LyricsVersion,
                streamUrl = mappedSong.StreamUrl,
                streamCount,
                streamQualifyingSeconds = mappedSong.StreamQualifyingSeconds,
                trackLengthSeconds = mappedSong.TrackLengthSeconds,
                displayOnHomePage = mappedSong.DisplayOnHomePage,
                displayOrder = mappedSong.DisplayOrder,
                isAiGenerated = mappedSong.IsAiGenerated,
                isAiVocals = mappedSong.IsAiVocals,
                isAiLyrics = mappedSong.IsAiLyrics,
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

            return NormalizeIds(idsString
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var id) ? id : -1));
        }

        /// <summary>
        /// Drops non-positive IDs and duplicates.
        ///
        /// Distinct is not cosmetic: both bulk endpoints project one response entry per ID, and the MAUI
        /// client keys the response by song - a repeated ID would give it two entries for one song.
        /// </summary>
        private static List<int> NormalizeIds(IEnumerable<int> ids)
        {
            if (ids == null)
                return [];

            return ids.Where(id => id > 0).Distinct().ToList();
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
                // Images reach this endpoint too - cover art has always been served through it, and
                // the pre-resized renditions now are as well. Browsers mostly sniff their way past a
                // wrong type, but they should not have to.
                ".webp" => ImageVariantPaths.VariantContentType,
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
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

        /// <summary>
        /// Body for the POST forms of the bulk like endpoints - the song IDs the caller wants data for.
        /// </summary>
        public class BulkSongIdsRequest
        {
            public List<int> Ids { get; set; } = [];
        }

        public class SetLikeStateRequest
        {
            /// <summary>True to like, false to dislike, null to clear any opinion.</summary>
            public bool? Status { get; set; }
        }
    }
}
