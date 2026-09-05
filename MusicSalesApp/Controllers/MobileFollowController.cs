#nullable enable
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MusicSalesApp.Middleware;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Controllers;

/// <summary>
/// The mobile app's side of the artist follow feature.
/// </summary>
/// <remarks>
/// <para>
/// <b>Domain failures answer 400, never 5xx.</b> The client queues follow intents while offline and
/// replays them in order after a reconnect; its flush stops at the first failure and it retries any
/// 5xx forever. A permanent condition dressed as a transient one - "that artist is gone", say -
/// would therefore strand every intent queued behind it. 400 is in the client's drop set. This is
/// the same contract <c>PUT api/music/like-state/{id}</c> is built on.
/// </para>
/// <para>
/// Nothing here returns a creator's account details to a listener, or anything at all about a
/// listener to a creator: the creator's follower list is a web surface, served by
/// <see cref="IArtistFollowerDirectoryService"/>, and has no route in this controller.
/// </para>
/// </remarks>
[Route("api/mobile/follows")]
[ApiController]
[RequireMobileApiKey]
[Authorize]
public class MobileFollowController : ControllerBase
{
    private readonly IArtistFollowService _followService;
    private readonly IArtistFollowerMessageService _messageService;
    private readonly IArtistReleaseNotificationService _releaseNotificationService;
    private readonly IArtistNotificationPreferenceService _preferenceService;

    public MobileFollowController(
        IArtistFollowService followService,
        IArtistFollowerMessageService messageService,
        IArtistReleaseNotificationService releaseNotificationService,
        IArtistNotificationPreferenceService preferenceService)
    {
        _followService = followService;
        _messageService = messageService;
        _releaseNotificationService = releaseNotificationService;
        _preferenceService = preferenceService;
    }

    /// <summary>
    /// Sets whether the caller follows this artist. Idempotent - see the note on the class.
    /// </summary>
    [HttpPut("{creatorPersonaId:int}")]
    public async Task<IActionResult> SetFollowState(int creatorPersonaId, [FromBody] SetFollowStateRequest request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if (request is null)
        {
            return BadRequest(new { error = "A request body is required." });
        }

        var outcome = await _followService.SetFollowStateAsync(
            creatorPersonaId, userId, request.Following, request.SourceSongId);

        return outcome switch
        {
            ArtistFollowOutcome.Followed or ArtistFollowOutcome.AlreadyFollowing =>
                Ok(new FollowStateResponse(creatorPersonaId, true, outcome.ToString())),

            ArtistFollowOutcome.Unfollowed or ArtistFollowOutcome.NotFollowing =>
                Ok(new FollowStateResponse(creatorPersonaId, false, outcome.ToString())),

            // Both are permanent as far as this request is concerned, so they must not be retried.
            ArtistFollowOutcome.ArtistUnavailable =>
                BadRequest(new { error = "This artist is not available to follow.", outcome = outcome.ToString() }),

            ArtistFollowOutcome.Blocked =>
                BadRequest(new { error = "You have blocked this artist.", outcome = outcome.ToString() }),

            _ => BadRequest(new { error = "The follow could not be updated.", outcome = outcome.ToString() }),
        };
    }

    /// <summary>
    /// The caller's followed artists.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetFollowedArtists()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        return Ok(await _followService.GetFollowedArtistsAsync(userId));
    }

    /// <summary>
    /// Which of the given personas the caller follows, so a track list can render its buttons in
    /// one round trip. POST rather than GET with a query string: IIS caps a URL at 2048 characters,
    /// which a long list of ids passes easily - the same reason <c>api/music/likes/bulk</c> is a POST.
    /// </summary>
    [HttpPost("states")]
    public async Task<IActionResult> GetFollowStates([FromBody] FollowStatesRequest request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if (request?.PersonaIds is null || request.PersonaIds.Count == 0)
        {
            return Ok(Array.Empty<int>());
        }

        var followed = await _followService.GetFollowedPersonaIdsAsync(request.PersonaIds, userId);
        return Ok(followed);
    }

    [HttpGet("notifications")]
    public async Task<IActionResult> GetNotifications()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        return Ok(await _releaseNotificationService.GetForListenerAsync(userId));
    }

    [HttpPost("notifications/{notificationId:int}/read")]
    public async Task<IActionResult> MarkNotificationRead(int notificationId)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        return await _releaseNotificationService.MarkReadAsync(notificationId, userId)
            ? Ok()
            : NotFound();
    }

    [HttpGet("messages")]
    public async Task<IActionResult> GetMessages()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        return Ok(await _messageService.GetMessagesForListenerAsync(userId));
    }

    [HttpPost("messages/{messageId:int}/read")]
    public async Task<IActionResult> MarkMessageRead(int messageId)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        return await _messageService.MarkReadAsync(messageId, userId) ? Ok() : NotFound();
    }

    [HttpPost("messages/{messageId:int}/hide")]
    public async Task<IActionResult> HideMessage(int messageId)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        return await _messageService.HideAsync(messageId, userId) ? Ok() : NotFound();
    }

    [HttpPost("messages/{messageId:int}/report")]
    public async Task<IActionResult> ReportMessage(int messageId, [FromBody] ReportArtistMessageRequest request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request?.Reason))
        {
            return BadRequest(new { error = "A reason is required." });
        }

        // False here covers both an unknown reason and someone else's message. Either way it is a
        // permanent refusal, so 400 rather than a 404 the client would treat as a routing problem.
        return await _messageService.ReportAsync(messageId, userId, request.Reason)
            ? Ok()
            : BadRequest(new { error = "The message could not be reported." });
    }

    /// <summary>
    /// Mutes or unmutes one artist without unfollowing them.
    /// </summary>
    [HttpPut("{creatorPersonaId:int}/preferences")]
    public async Task<IActionResult> SetArtistPreferences(
        int creatorPersonaId,
        [FromBody] ArtistPreferencesRequest request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if (request is null)
        {
            return BadRequest(new { error = "A request body is required." });
        }

        var updated = await _followService.SetArtistNotificationPreferencesAsync(
            creatorPersonaId, userId, request.ReleaseNotificationsEnabled, request.ArtistMessagesEnabled);

        return updated ? Ok() : BadRequest(new { error = "You are not following this artist." });
    }

    [HttpPut("{creatorPersonaId:int}/blocked")]
    public async Task<IActionResult> SetBlocked(int creatorPersonaId, [FromBody] SetBlockedRequest request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if (request is null)
        {
            return BadRequest(new { error = "A request body is required." });
        }

        var updated = await _followService.SetBlockedAsync(creatorPersonaId, userId, request.Blocked);

        return updated ? Ok() : BadRequest(new { error = "You have no relationship with this artist." });
    }

    /// <summary>
    /// The account-level email toggles. The in-app channel has no switch: the notification row IS
    /// the in-app notification, and the per-artist mute above already silences it.
    /// </summary>
    [HttpGet("notification-preferences")]
    public async Task<IActionResult> GetNotificationPreferences()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var preferences = await _preferenceService.GetAsync(userId);
        return preferences is null ? Unauthorized() : Ok(preferences);
    }

    [HttpPut("notification-preferences")]
    public async Task<IActionResult> SetNotificationPreferences(
        [FromBody] ArtistNotificationPreferences request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if (request is null)
        {
            return BadRequest(new { error = "A request body is required." });
        }

        var updated = await _preferenceService.SetAsync(userId, request);
        return updated ? Ok() : BadRequest(new { error = "The preferences could not be saved." });
    }

    private bool TryGetUserId(out int userId)
    {
        userId = 0;
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out userId);
    }
}

public sealed class SetFollowStateRequest
{
    public bool Following { get; set; }

    /// <summary>
    /// The song the listener was on when they followed, if any. Ignored unless it really belongs
    /// to this persona.
    /// </summary>
    public int? SourceSongId { get; set; }
}

public sealed class FollowStatesRequest
{
    public List<int>? PersonaIds { get; set; }
}

public sealed record FollowStateResponse(int CreatorPersonaId, bool Following, string Outcome);

public sealed class ReportArtistMessageRequest
{
    public string? Reason { get; set; }
}

public sealed class ArtistPreferencesRequest
{
    public bool? ReleaseNotificationsEnabled { get; set; }
    public bool? ArtistMessagesEnabled { get; set; }
}

public sealed class SetBlockedRequest
{
    public bool Blocked { get; set; }
}
