using Microsoft.AspNetCore.SignalR;

namespace MusicSalesApp.Hubs;

/// <summary>
/// SignalR hub for real-time site maintenance notifications across all connected clients.
/// Updates are broadcast via IHubContext&lt;MaintenanceHub&gt; from AdminSettings when maintenance windows are saved.
/// Clients receive updates via the "ReceiveMaintenanceUpdate" message.
/// </summary>
public class MaintenanceHub : Hub
{
}
