using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MusicSalesApp.Hubs;

/// <summary>
/// Authenticated hub used to notify individual users that their pending admin-message
/// queue changed and should be refreshed.
/// </summary>
[Authorize(AuthenticationSchemes = "Identity.Application,Bearer")]
public class AdminMessageHub : Hub
{
}