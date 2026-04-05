using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// Interface for the like-count SignalR client that listens for real-time updates.
/// </summary>
public interface ILikeCountHubClient : IAsyncDisposable
{
    /// <summary>
    /// Event fired when a song's like/dislike counts are updated from another client/tab.
    /// Parameters: songMetadataId, likeCount, dislikeCount
    /// </summary>
    event Action<int, int, int> OnLikeCountReceived;

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
/// SignalR client service for receiving real-time like/dislike count updates.
/// </summary>
public class LikeCountHubClient : ILikeCountHubClient
{
    private readonly HubConnection _hubConnection;
    private bool _isStarted;

    public event Action<int, int, int> OnLikeCountReceived;

    public bool IsConnected => _hubConnection.State == HubConnectionState.Connected;

    public LikeCountHubClient(NavigationManager navigationManager)
    {
        var hubUrl = navigationManager.ToAbsoluteUri("/likecounthub");

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<int, int, int>(SignalRMethodNames.ReceiveLikeCountUpdate, (songMetadataId, likeCount, dislikeCount) =>
        {
            OnLikeCountReceived?.Invoke(songMetadataId, likeCount, dislikeCount);
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
            // Connection failed - this is not critical, local events still work
            System.Diagnostics.Debug.WriteLine($"LikeCountHub connection failed: {ex.Message}");
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
