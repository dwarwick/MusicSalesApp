using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace MusicSalesApp.Services;

/// <summary>
/// Interface for the stream count SignalR client that listens for real-time updates.
/// </summary>
public interface IStreamCountHubClient : IAsyncDisposable
{
    /// <summary>
    /// Event fired when a stream count is updated from another client/tab.
    /// </summary>
    event Action<int, int> OnStreamCountReceived;

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
/// SignalR client service for receiving real-time stream count updates.
/// </summary>
public class StreamCountHubClient : IStreamCountHubClient
{
    private readonly HubConnection _hubConnection;
    private bool _isStarted;

    public event Action<int, int> OnStreamCountReceived;

    public bool IsConnected => _hubConnection.State == HubConnectionState.Connected;

    public StreamCountHubClient(NavigationManager navigationManager)
    {
        var hubUrl = navigationManager.ToAbsoluteUri("/streamcounthub");
        
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<int, int>("ReceiveStreamCountUpdate", (songMetadataId, newCount) =>
        {
            OnStreamCountReceived?.Invoke(songMetadataId, newCount);
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
        catch (Exception)
        {
            // Connection failed - this is not critical, local events still work
            // The service will try again on next request
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
