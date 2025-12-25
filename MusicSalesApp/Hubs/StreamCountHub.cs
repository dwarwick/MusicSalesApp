using Microsoft.AspNetCore.SignalR;

namespace MusicSalesApp.Hubs;

/// <summary>
/// SignalR hub for real-time stream count updates across all connected clients.
/// </summary>
public class StreamCountHub : Hub
{
    /// <summary>
    /// Broadcasts a stream count update to all connected clients.
    /// </summary>
    /// <param name="songMetadataId">The ID of the song metadata.</param>
    /// <param name="newCount">The new stream count.</param>
    public async Task BroadcastStreamCountUpdate(int songMetadataId, int newCount)
    {
        await Clients.All.SendAsync("ReceiveStreamCountUpdate", songMetadataId, newCount);
    }
}
