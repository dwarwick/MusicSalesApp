using Microsoft.AspNetCore.SignalR;

namespace MusicSalesApp.Hubs;

/// <summary>
/// SignalR hub for real-time stream count updates across all connected clients.
/// The hub broadcasts updates when StreamCountService increments a stream count.
/// Clients receive updates via the "ReceiveStreamCountUpdate" message.
/// </summary>
public class StreamCountHub : Hub
{
    // This hub primarily serves as a connection point for clients.
    // Stream count updates are broadcast via IHubContext<StreamCountHub> from StreamCountService.
}
