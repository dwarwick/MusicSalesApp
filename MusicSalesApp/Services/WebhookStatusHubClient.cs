#nullable enable
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// Represents a webhook status update message.
/// </summary>
public record WebhookStatusMessage
{
    /// <summary>
    /// The user ID this update is for.
    /// </summary>
    public int UserId { get; init; }

    /// <summary>
    /// The type of webhook that completed (e.g., "PayPalOnboarding", "TaxBandits").
    /// </summary>
    public string WebhookType { get; init; } = string.Empty;

    /// <summary>
    /// Whether the webhook processing was successful.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// A message to display to the user.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Optional: The new status (e.g., "Completed", "Failed").
    /// </summary>
    public string? NewStatus { get; init; }
}

/// <summary>
/// Interface for the webhook status SignalR client that listens for real-time updates.
/// </summary>
public interface IWebhookStatusHubClient : IAsyncDisposable
{
    /// <summary>
    /// Event fired when a webhook status update is received.
    /// </summary>
    event Action<WebhookStatusMessage>? OnWebhookStatusReceived;

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
/// SignalR client service for receiving real-time webhook status updates.
/// </summary>
public class WebhookStatusHubClient : IWebhookStatusHubClient
{
    private readonly HubConnection _hubConnection;
    private bool _isStarted;

    public event Action<WebhookStatusMessage>? OnWebhookStatusReceived;

    public bool IsConnected => _hubConnection.State == HubConnectionState.Connected;

    public WebhookStatusHubClient(NavigationManager navigationManager)
    {
        var hubUrl = navigationManager.ToAbsoluteUri("/webhookstatushub");
        
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<WebhookStatusMessage>(SignalRMethodNames.ReceiveWebhookStatus, (message) =>
        {
            OnWebhookStatusReceived?.Invoke(message);
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
            // Connection failed - this is not critical, page refresh still works
            // Log for debugging purposes but don't throw
            System.Diagnostics.Debug.WriteLine($"WebhookStatus SignalR hub connection failed: {ex.Message}");
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
