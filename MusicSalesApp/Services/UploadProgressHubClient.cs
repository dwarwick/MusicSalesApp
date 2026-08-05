#nullable enable

using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Services;

/// <summary>
/// Receives live upload progress for the signed-in creator's own songs.
/// </summary>
public interface IUploadProgressHubClient : IAsyncDisposable
{
    /// <summary>Fired for each update. The payload's JobId identifies which song it belongs to.</summary>
    event Func<AudioProcessingProgress, Task>? OnProgress;

    /// <summary>Connects and joins this creator's group. Safe to call more than once.</summary>
    Task StartAsync();

    bool IsConnected { get; }
}

/// <inheritdoc />
public class UploadProgressHubClient : IUploadProgressHubClient
{
    private readonly HubConnection _hubConnection;
    private readonly ILogger<UploadProgressHubClient> _logger;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private bool _isStarted;

    public event Func<AudioProcessingProgress, Task>? OnProgress;

    public bool IsConnected => _hubConnection.State == HubConnectionState.Connected;

    public UploadProgressHubClient(
        NavigationManager navigationManager,
        IHttpContextAccessor httpContextAccessor,
        ILogger<UploadProgressHubClient> logger)
    {
        _logger = logger;
        var hubUrl = navigationManager.ToAbsoluteUri("/uploadprogresshub");

        // UploadProgressHub is [Authorize]. This connection originates on the *server*, from the
        // Blazor circuit back to the same site, so it carries none of the browser's context - the
        // request arrives anonymous and the hub rejects it. Forwarding the caller's cookies is what
        // makes the connection authenticate as the creator, which the hub then uses to decide which
        // group to join. Same approach as AdminMessageHubClient.
        var cookies = CreateCookieContainer(hubUrl, httpContextAccessor.HttpContext);

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (cookies != null)
                {
                    options.Cookies = cookies;
                }
            })
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<AudioProcessingProgress>(
            SignalRMethodNames.ReceiveUploadProgress,
            async progress =>
            {
                var handler = OnProgress;
                if (handler is not null)
                {
                    await handler(progress);
                }
            });

        // Group membership is per-connection, so a reconnect has to re-join or the creator's bars
        // silently stop moving while their songs carry on processing.
        _hubConnection.Reconnected += async _ =>
        {
            _logger.LogInformation("Upload progress hub reconnected; re-joining the creator group");
            await SubscribeAsync();
        };

        _hubConnection.Closed += exception =>
        {
            _logger.LogWarning(exception, "Upload progress hub connection closed");
            _isStarted = false;
            return Task.CompletedTask;
        };
    }

    public async Task StartAsync()
    {
        await _startLock.WaitAsync();

        try
        {
            if (_isStarted || _hubConnection.State != HubConnectionState.Disconnected)
            {
                return;
            }

            await _hubConnection.StartAsync();
            _isStarted = true;
            await SubscribeAsync();
            _logger.LogInformation("Upload progress hub connected");
        }
        catch (Exception ex)
        {
            // Logged rather than swallowed to Debug: when this fails the upload still completes, so
            // the only symptom is a progress bar frozen at "Waiting to be processed" forever. That
            // is indistinguishable from a stuck pipeline unless the failure says so here.
            _logger.LogWarning(
                ex,
                "Upload progress hub connection failed; upload progress bars will not advance, "
                + "though the uploads themselves continue processing normally");
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task SubscribeAsync()
    {
        try
        {
            // No creator id is sent - the hub resolves it from the authenticated connection, so a
            // client cannot subscribe to somebody else's uploads.
            await _hubConnection.InvokeAsync(nameof(Hubs.UploadProgressHub.SubscribeToMyUploads));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not join the upload progress group; progress bars will not advance");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private static CookieContainer? CreateCookieContainer(Uri hubUrl, HttpContext? httpContext)
    {
        if (httpContext?.Request.Cookies.Count is not > 0)
        {
            return null;
        }

        var container = new CookieContainer();
        foreach (var cookie in httpContext.Request.Cookies)
        {
            container.Add(hubUrl, new Cookie(cookie.Key, cookie.Value, "/"));
        }

        return container;
    }
}
