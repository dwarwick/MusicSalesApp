using Microsoft.AspNetCore.SignalR;

namespace MusicSalesApp.Hubs;

/// <summary>
/// SignalR hub for real-time like/dislike count updates across all connected clients.
/// The hub broadcasts updates when SongLikeService toggles a like or dislike.
/// Clients receive updates via the "ReceiveLikeCountUpdate" message.
/// </summary>
public class LikeCountHub : Hub
{
    // This hub primarily serves as a connection point for clients.
    // Like count updates are broadcast via IHubContext<LikeCountHub> from SongLikeService.
}
