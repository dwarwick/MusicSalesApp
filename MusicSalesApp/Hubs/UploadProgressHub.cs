using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Hubs;

/// <summary>
/// Carries live upload progress to the creator who started the upload.
///
/// <para>
/// FFmpeg runs in an Azure Function now, so the progress updates arrive on a completely different
/// HTTP request from the Blazor circuit that is rendering the upload page - and potentially in a
/// different worker process. An in-process event could not reach that circuit, which is why this
/// goes through SignalR rather than a simple C# event.
/// </para>
///
/// <para>
/// Unlike the other hubs here, this one fans out to a group rather than broadcasting: upload
/// activity is private to the creator doing it.
/// </para>
/// </summary>
[Authorize(AuthenticationSchemes = "Identity.Application,Bearer")]
public class UploadProgressHub : Hub
{
    private readonly ICreatorService _creatorService;
    private readonly ILogger<UploadProgressHub> _logger;

    public UploadProgressHub(ICreatorService creatorService, ILogger<UploadProgressHub> logger)
    {
        _creatorService = creatorService;
        _logger = logger;
    }

    /// <summary>
    /// Joins the caller to their own creator group.
    ///
    /// <para>
    /// Deliberately takes no parameter. The creator id is resolved from the authenticated
    /// connection, so a client cannot ask to join a group belonging to somebody else - which a
    /// <c>JoinCreatorGroup(int creatorId)</c> signature would have allowed, and which would have
    /// leaked one creator's upload activity to another.
    /// </para>
    /// </summary>
    public async Task SubscribeToMyUploads()
    {
        var creatorId = await ResolveCreatorIdAsync();
        if (creatorId is null)
        {
            // Authenticated, but not a creator - nothing to subscribe to. Not an error.
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRGroups.ForCreator(creatorId.Value));
    }

    private async Task<int?> ResolveCreatorIdAsync()
    {
        var userIdClaim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(userIdClaim, out var userId))
        {
            _logger.LogWarning("Upload progress subscription rejected: no usable user id on the connection.");
            return null;
        }

        var creator = await _creatorService.GetCreatorByUserIdAsync(userId);
        return creator?.Id;
    }
}
