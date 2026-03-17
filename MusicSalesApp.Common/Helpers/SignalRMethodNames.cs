namespace MusicSalesApp.Common.Helpers;

/// <summary>
/// String constants for SignalR method names used by hub publishers (SendAsync) and
/// subscribers (HubConnection.On). Always use these constants instead of inline strings to
/// avoid silent mismatches between writers and readers.
/// </summary>
public static class SignalRMethodNames
{
    /// <summary>
    /// Broadcast by the server when site maintenance window settings change so connected
    /// clients can refresh their maintenance banner/dialog in real time.
    /// </summary>
    public const string ReceiveMaintenanceUpdate = "ReceiveMaintenanceUpdate";
}
