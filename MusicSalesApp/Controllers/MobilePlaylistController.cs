using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
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
    private readonly ISubscriptionService _subscriptionService;
    private readonly ISongMetadataService _songMetadataService;
    private readonly IMobileSongMapper _songMapper;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<MobilePlaylistController> _logger;

    public MobilePlaylistController(
        IPlaylistService playlistService,
        IRecommendationService recommendationService,
        ISubscriptionService subscriptionService,
        ISongMetadataService songMetadataService,
        IMobileSongMapper songMapper,
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<MobilePlaylistController> logger)
    {
        _playlistService = playlistService;
        _recommendationService = recommendationService;
        _subscriptionService = subscriptionService;
        _songMetadataService = songMetadataService;
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

        return Ok(result);
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
                Kind = p.IsSystemGenerated ? MobilePlaylistKinds.LikedSongs : MobilePlaylistKinds.Custom
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

        var songs = entries
            .Where(up => up.SongMetadata != null && !string.IsNullOrEmpty(up.SongMetadata.Mp3BlobPath))
            .Select(up => _songMapper.MapToPlaylistSong(up.SongMetadata, SasLifetime, up.Id))
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

        // Materialize the SongMetadata (navigation property was included by service)
        var songs = recommended
            .Where(r => r.SongMetadata != null && !string.IsNullOrEmpty(r.SongMetadata.Mp3BlobPath))
            .Select(r => _songMapper.MapToPlaylistSong(r.SongMetadata, SasLifetime, userPlaylistId: null))
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
        var dtos = available
            .Where(m => !string.IsNullOrEmpty(m.Mp3BlobPath))
            .Select(m => _songMapper.MapToPlaylistSong(m, SasLifetime, userPlaylistId: null))
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
}
