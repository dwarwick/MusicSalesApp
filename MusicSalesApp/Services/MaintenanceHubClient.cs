#nullable enable

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace MusicSalesApp.Services;

/// <summary>
/// Interface for the maintenance notification SignalR client that listens for real-time updates.
/// </summary>
public interface IMaintenanceHubClient : IAsyncDisposable
{
    /// <summary>
    /// Event fired when site maintenance settings are updated by an admin.
    /// </summary>
    event Action? OnMaintenanceUpdated;

    /// <summary>
    /// Starts the SignalR connection if not already started.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Returns true if the connection is established.
    /// </summary>
    bool IsConnected { get; }
}

/// <summary>
/// SignalR client service for receiving real-time site maintenance notifications.
/// </summary>
public class MaintenanceHubClient : IMaintenanceHubClient
{
    private readonly HubConnection _hubConnection;
    private bool _isStarted;

    public event Action? OnMaintenanceUpdated;

    public bool IsConnected => _hubConnection.State == HubConnectionState.Connected;

    public MaintenanceHubClient(NavigationManager navigationManager)
    {
        var hubUrl = navigationManager.ToAbsoluteUri("/maintenancehub");

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On("ReceiveMaintenanceUpdate", () =>
        {
            OnMaintenanceUpdated?.Invoke();
        });
    }

    public async Task StartAsync()
    {
        if (_isStarted || _hubConnection.State != HubConnectionState.Disconnected)
            return;

        try
        {
            await _hubConnection.StartAsync();
            _isStarted = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Maintenance hub connection failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }
    }
}
