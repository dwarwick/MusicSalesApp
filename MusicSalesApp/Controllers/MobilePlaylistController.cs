using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Middleware;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

/// <summary>
/// Playlist endpoints for the MAUI Android app. All endpoints require the
/// mobile API key (X-Api-Key) and JWT bearer authentication.
/// </summary>
[Route("api/mobile/playlists")]
[ApiController]
[RequireMobileApiKey]
[Authorize]
public class MobilePlaylistController : ControllerBase
{
    private static readonly TimeSpan SasLifetime = TimeSpan.FromHours(24);

    private readonly IPlaylistService _playlistService;
    private readonly IRecommendationService _recommendationService;
    private readonly ITopStreamedPlaylistService _topStreamedPlaylistService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ISongMetadataService _songMetadataService;
    private readonly IAppSettingsService _appSettingsService;
    private readonly IMobileSongMapper _songMapper;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<MobilePlaylistController> _logger;

    public MobilePlaylistController(
        IPlaylistService playlistService,
        IRecommendationService recommendationService,
        ITopStreamedPlaylistService topStreamedPlaylistService,
        ISubscriptionService subscriptionService,
        ISongMetadataService songMetadataService,
        IAppSettingsService appSettingsService,
        IMobileSongMapper songMapper,
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<MobilePlaylistController> logger)
    {
        _playlistService = playlistService;
        _recommendationService = recommendationService;
        _topStreamedPlaylistService = topStreamedPlaylistService;
        _subscriptionService = subscriptionService;
        _songMetadataService = songMetadataService;
        _appSettingsService = appSettingsService;
        _songMapper = songMapper;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    private bool TryGetUserId(out int userId)
    {
        userId = 0;
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out userId);
    }

    /// <summary>
    /// Returns the Recommended and Liked Songs playlists for the home page.
    /// Each value is null when that playlist has zero playable songs so the
    /// client can hide empty tiles.
    /// </summary>
    [HttpGet("home")]
    public async Task<IActionResult> GetHomePlaylists()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = new MobileHomePlaylistsDto();

        // Recommended playlist
        var recommended = await _recommendationService.GetRecommendedPlaylistAsync(userId);
        if (recommended.Count > 0)
        {
            result.Recommended = new MobilePlaylistDto
            {
                Id = 0,
                Name = "Recommended For You",
                SongCount = recommended.Count,
                IsSystemGenerated = true,
                Kind = MobilePlaylistKinds.Recommended
            };
        }

        // Liked Songs system playlist
        await _playlistService.SyncLikedSongsPlaylistAsync(userId);
        var likedSongs = await _playlistService.GetOrCreateLikedSongsPlaylistAsync(userId);
        var likedCount = (await _playlistService.GetPlaylistSongsAsync(likedSongs.Id)).Count;
        if (likedCount > 0)
        {
            result.LikedSongs = new MobilePlaylistDto
            {
                Id = likedSongs.Id,
                Name = likedSongs.PlaylistName,
                SongCount = likedCount,
                IsSystemGenerated = true,
                Kind = MobilePlaylistKinds.LikedSongs
            };
        }

        result.TopStreamed = await BuildTopStreamedTilesAsync();

        return Ok(result);
    }

    /// <summary>
    /// The five global "most streamed" playlists as tiles, in display order, with empty ones omitted.
    /// </summary>
    /// <remarks>
    /// Anonymous-friendly: nothing here depends on who is asking. Each tile carries <c>Key</c> rather
    /// than a usable <c>Id</c>, because these are generated lists with no <c>Playlists</c> row - so the
    /// client must open them by key.
    /// </remarks>
    [HttpGet("top-streamed")]
    [Authorize(AuthenticationSchemes = "Identity.Application,Bearer")]
    [AllowAnonymous]
    public async Task<IActionResult> GetTopStreamedPlaylists()
        => Ok(await BuildTopStreamedTilesAsync());

    private async Task<List<MobilePlaylistDto>> BuildTopStreamedTilesAsync()
    {
        var counts = await _topStreamedPlaylistService.GetCountsAsync();
        var generatedAtUtc = await _topStreamedPlaylistService.GetLastGeneratedAtAsync();

        return TopStreamedPlaylists.All
            .OrderBy(descriptor => descriptor.DisplayOrder)
            .Where(descriptor => counts.ContainsKey(descriptor.Window))
            .Select(descriptor => new MobilePlaylistDto
            {
                Id = 0,
                Key = descriptor.Window,
                Name = descriptor.Name,
                SongCount = counts[descriptor.Window],
                IsSystemGenerated = true,
                DisplayOrder = descriptor.DisplayOrder,
                GeneratedAtUtc = generatedAtUtc,
                Kind = MobilePlaylistKinds.TopStreamed
            })
            .ToList();
    }

    /// <summary>
    /// Returns one "most streamed" playlist as a playable list, in rank order - most streamed first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anonymous is allowed so the playlists are browsable before sign-in, matching
    /// <c>GET api/music/songs</c>. The explicit schemes sit alongside <c>[AllowAnonymous]</c> for the
    /// reason documented there: without them the MAUI bearer token is ignored and a signed-in
    /// subscriber would be served preview-length audio. No new exposure - these songs are a strict
    /// subset of what that endpoint already returns to the same callers.
    /// </para>
    /// <para>
    /// Each song carries both counts. <c>StreamCount</c> is the lifetime total; <c>PeriodStreamCount</c>
    /// is the number this list was ranked on. They differ for the four rolling windows, so a client
    /// showing only the first would render a correctly ordered list that looks mis-sorted.
    /// </para>
    /// </remarks>
    [HttpGet("top-streamed/{window}")]
    [Authorize(AuthenticationSchemes = "Identity.Application,Bearer")]
    [AllowAnonymous]
    public async Task<IActionResult> GetTopStreamedSongs(string window)
    {
        var descriptor = TopStreamedPlaylists.Find(window);
        if (descriptor is null)
            return NotFound();

        var entries = await _topStreamedPlaylistService.GetAsync(descriptor.Window);
        var streamQualifying = await _appSettingsService.GetStreamQualifyingSettingsAsync();
        var streamContext = await BuildStreamContextForCallerAsync();

        var songs = entries
            .Where(entry => entry.SongMetadata != null && !string.IsNullOrEmpty(entry.SongMetadata.Mp3BlobPath))
            .Select(entry =>
            {
                var song = _songMapper.MapToPlaylistSong(
                    entry.SongMetadata, SasLifetime, userPlaylistId: null, streamQualifying, null, streamContext);
                // Only meaningful for the rolling windows; on the all-time list the ranking number and
                // the lifetime counter are the same figure, so a second column would just repeat it.
                song.PeriodStreamCount = descriptor.PeriodLabel is null ? null : entry.StreamCount;
                return song;
            })
            .ToList();

        return Ok(new MobilePlaylistSongsDto
        {
            PlaylistId = 0,
            PlaylistName = descriptor.Name,
            IsSystemGenerated = true,
            PeriodLabel = descriptor.PeriodLabel,
            GeneratedAtUtc = entries.Count > 0 ? entries[0].GeneratedAt : null,
            Songs = songs
        });
    }

    /// <summary>
    /// Returns all of the user's playlists (custom + system Liked Songs).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetMyPlaylists()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        // Ensure Liked Songs exists and is in sync
        await _playlistService.GetOrCreateLikedSongsPlaylistAsync(userId);
        await _playlistService.SyncLikedSongsPlaylistAsync(userId);

        var playlists = await _playlistService.GetUserPlaylistsAsync(userId);

        // Pull counts once per playlist (small N)
        var dtos = new List<MobilePlaylistDto>(playlists.Count);
        foreach (var p in playlists)
        {
            var songs = await _playlistService.GetPlaylistSongsAsync(p.Id);
            dtos.Add(new MobilePlaylistDto
            {
                Id = p.Id,
                Name = p.PlaylistName,
                SongCount = songs.Count,
                IsSystemGenerated = p.IsSystemGenerated,
                // Name-matched, not just IsSystemGenerated: that flag says "the user may not edit
                // this", not "this is Liked Songs", so a second system playlist would be mislabelled.
                Kind = p.IsSystemGenerated && p.PlaylistName == PlaylistNames.LikedSongs
                    ? MobilePlaylistKinds.LikedSongs
                    : MobilePlaylistKinds.Custom
            });
        }

        return Ok(dtos);
    }

    /// <summary>
    /// Returns the songs in a playlist owned by the caller, with SAS stream
    /// URLs ready for playback.
    /// </summary>
    [HttpGet("{playlistId:int}/songs")]
    public async Task<IActionResult> GetPlaylistSongs(int playlistId)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var playlist = await _playlistService.GetPlaylistByIdAsync(playlistId);
        if (playlist == null || playlist.UserId != userId)
            return NotFound();

        // Liked Songs: make sure it reflects current likes before we read
        if (playlist.IsSystemGenerated)
        {
            await _playlistService.SyncLikedSongsPlaylistAsync(userId);
        }

        var entries = await _playlistService.GetPlaylistSongsAsync(playlistId);
        var streamQualifying = await _appSettingsService.GetStreamQualifyingSettingsAsync();
        var streamContext = await BuildStreamContextAsync(userId);

        var songs = entries
            .Where(up => up.SongMetadata != null && !string.IsNullOrEmpty(up.SongMetadata.Mp3BlobPath))
            .Select(up => _songMapper.MapToPlaylistSong(up.SongMetadata, SasLifetime, up.Id, streamQualifying, null, streamContext))
            .ToList();

        return Ok(new MobilePlaylistSongsDto
        {
            PlaylistId = playlist.Id,
            PlaylistName = playlist.PlaylistName,
            IsSystemGenerated = playlist.IsSystemGenerated,
            Songs = songs
        });
    }

    /// <summary>
    /// Returns the user's recommended songs as a playable list.
    /// </summary>
    [HttpGet("recommended")]
    public async Task<IActionResult> GetRecommendedSongs()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var recommended = await _recommendationService.GetRecommendedPlaylistAsync(userId);
        var streamQualifying = await _appSettingsService.GetStreamQualifyingSettingsAsync();
        var streamContext = await BuildStreamContextAsync(userId);

        // Materialize the SongMetadata (navigation property was included by service)
        var songs = recommended
            .Where(r => r.SongMetadata != null && !string.IsNullOrEmpty(r.SongMetadata.Mp3BlobPath))
            .Select(r => _songMapper.MapToPlaylistSong(r.SongMetadata, SasLifetime, userPlaylistId: null, streamQualifying, null, streamContext))
            .ToList();

        return Ok(new MobilePlaylistSongsDto
        {
            PlaylistId = 0,
            PlaylistName = "Recommended For You",
            IsSystemGenerated = true,
            Songs = songs
        });
    }

    /// <summary>
    /// Creates a new custom playlist. Requires an active subscription.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreatePlaylist([FromBody] CreateMobilePlaylistRequest request)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Playlist name is required." });

        if (!await _subscriptionService.HasActiveSubscriptionAsync(userId))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "An active subscription is required to create playlists.", requiresSubscription = true });

        var playlist = await _playlistService.CreatePlaylistAsync(userId, request.Name.Trim());
        return Ok(new MobilePlaylistDto
        {
            Id = playlist.Id,
            Name = playlist.PlaylistName,
            SongCount = 0,
            IsSystemGenerated = false,
            Kind = MobilePlaylistKinds.Custom
        });
    }

    /// <summary>
    /// Renames a custom playlist. Returns 403 for system-generated playlists.
    /// </summary>
    [HttpPut("{playlistId:int}")]
    public async Task<IActionResult> RenamePlaylist(int playlistId, [FromBody] RenameMobilePlaylistRequest request)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Playlist name is required." });

        var playlist = await _playlistService.GetPlaylistByIdAsync(playlistId);
        if (playlist == null || playlist.UserId != userId)
            return NotFound();
        if (playlist.IsSystemGenerated)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "System playlists cannot be renamed." });

        var ok = await _playlistService.UpdatePlaylistAsync(playlistId, userId, request.Name.Trim());
        return ok ? NoContent() : BadRequest(new { message = "Failed to rename playlist." });
    }

    /// <summary>
    /// Deletes a custom playlist. Returns 403 for system-generated playlists.
    /// </summary>
    [HttpDelete("{playlistId:int}")]
    public async Task<IActionResult> DeletePlaylist(int playlistId)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var playlist = await _playlistService.GetPlaylistByIdAsync(playlistId);
        if (playlist == null || playlist.UserId != userId)
            return NotFound();
        if (playlist.IsSystemGenerated)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "System playlists cannot be deleted." });

        var ok = await _playlistService.DeletePlaylistAsync(playlistId, userId);
        return ok ? NoContent() : BadRequest(new { message = "Failed to delete playlist." });
    }

    /// <summary>
    /// Lists songs available to add to the given playlist. Subscription-gated;
    /// non-subscribers receive an empty list and requiresSubscription = true.
    /// </summary>
    [HttpGet("{playlistId:int}/available-songs")]
    public async Task<IActionResult> GetAvailableSongs(int playlistId)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var playlist = await _playlistService.GetPlaylistByIdAsync(playlistId);
        if (playlist == null || playlist.UserId != userId)
            return NotFound();

        var hasSub = await _subscriptionService.HasActiveSubscriptionAsync(userId);
        if (!hasSub)
        {
            return Ok(new { songs = Array.Empty<MobilePlaylistSongDto>(), requiresSubscription = true });
        }

        var available = await _playlistService.GetAvailableSongsForPlaylistAsync(userId, playlistId);
        var streamQualifying = await _appSettingsService.GetStreamQualifyingSettingsAsync();

        // hasSub was checked above and is true on this path, so the context is already known.
        var streamContext = new MobileStreamContext(userId, true);
        var dtos = available
            .Where(m => !string.IsNullOrEmpty(m.Mp3BlobPath))
            .Select(m => _songMapper.MapToPlaylistSong(m, SasLifetime, userPlaylistId: null, streamQualifying, null, streamContext))
            .ToList();
        return Ok(new { songs = dtos, requiresSubscription = false });
    }

    /// <summary>
    /// Adds a song to a custom playlist. Requires an active subscription.
    /// </summary>
    [HttpPost("{playlistId:int}/songs")]
    public async Task<IActionResult> AddSong(int playlistId, [FromBody] AddSongToMobilePlaylistRequest request)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        if (request == null || request.SongMetadataId <= 0)
            return BadRequest(new { message = "SongMetadataId is required." });

        if (!await _subscriptionService.HasActiveSubscriptionAsync(userId))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "An active subscription is required to add songs to playlists.", requiresSubscription = true });

        var ok = await _playlistService.AddSongToPlaylistAsync(userId, playlistId, request.SongMetadataId);
        return ok ? NoContent() : BadRequest(new { message = "Failed to add song to playlist." });
    }

    /// <summary>
    /// Removes a single song entry from a playlist owned by the caller.
    /// </summary>
    [HttpDelete("{playlistId:int}/songs/{userPlaylistId:int}")]
    public async Task<IActionResult> RemoveSong(int playlistId, int userPlaylistId)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var ok = await _playlistService.RemoveSongFromPlaylistAsync(playlistId, userPlaylistId, userId);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>
    /// Reorders songs in a playlist. The supplied list of UserPlaylist ids
    /// represents the new order (first = top). Subscription-gated.
    /// </summary>
    [HttpPut("{playlistId:int}/reorder")]
    public async Task<IActionResult> Reorder(int playlistId, [FromBody] ReorderMobilePlaylistRequest request)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();
        if (request == null || request.UserPlaylistIds == null)
            return BadRequest(new { message = "UserPlaylistIds is required." });

        if (!await _subscriptionService.HasActiveSubscriptionAsync(userId))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "An active subscription is required to reorder playlists.", requiresSubscription = true });

        var ok = await _playlistService.ReorderPlaylistAsync(playlistId, userId, request.UserPlaylistIds);
        return ok ? NoContent() : BadRequest(new { message = "Failed to reorder playlist." });
    }

    /// <summary>
    /// Who the playback URLs in this response are for.
    ///
    /// <para>
    /// Resolved once per response rather than per song: entitlement is then baked into each
    /// manifest token, so a playlist of a hundred tracks costs one subscription lookup rather than a
    /// hundred, and a subscription lapsing mid-song does not cut the audio off.
    /// </para>
    /// </summary>
    private async Task<MobileStreamContext> BuildStreamContextAsync(int userId)
        => new(userId, await _subscriptionService.HasActiveSubscriptionAsync(userId));

    /// <summary>
    /// The stream context for whoever is calling, signed in or not.
    ///
    /// <para>
    /// Only the anonymous-capable endpoints use this. A caller with no id gets no full access, so the
    /// manifest they are handed is preview-length - the same treatment a signed-out listener already
    /// gets from the songs endpoint.
    /// </para>
    /// </summary>
    private async Task<MobileStreamContext> BuildStreamContextForCallerAsync()
        => TryGetUserId(out var userId)
            ? await BuildStreamContextAsync(userId)
            : new MobileStreamContext(null, false);
}
