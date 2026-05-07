#nullable enable

using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using MusicSalesApp.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace MusicSalesApp.Services;

public interface IAdminMessageHubClient : IAsyncDisposable
{
    event Action? OnAdminMessagesUpdated;

    Task StartAsync();

    bool IsConnected { get; }
}

public class AdminMessageHubClient : IAdminMessageHubClient
{
    private readonly HubConnection _hubConnection;
    private readonly ILogger<AdminMessageHubClient> _logger;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private bool _isStarted;

    public event Action? OnAdminMessagesUpdated;

    public bool IsConnected => _hubConnection.State == HubConnectionState.Connected;

    public AdminMessageHubClient(
        NavigationManager navigationManager,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AdminMessageHubClient> logger)
    {
        _logger = logger;
        var hubUrl = navigationManager.ToAbsoluteUri("/adminmessagehub");
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

        _hubConnection.On(SignalRMethodNames.ReceiveAdminMessageRefresh, () =>
        {
            OnAdminMessagesUpdated?.Invoke();
        });

        _hubConnection.Reconnected += connectionId =>
        {
            _logger.LogInformation("Admin message hub reconnected with connection ID {ConnectionId}", connectionId);
            OnAdminMessagesUpdated?.Invoke();
            return Task.CompletedTask;
        };

        _hubConnection.Reconnecting += exception =>
        {
            _logger.LogWarning(exception, "Admin message hub reconnecting");
            return Task.CompletedTask;
        };

        _hubConnection.Closed += exception =>
        {
            _logger.LogWarning(exception, "Admin message hub connection closed");
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
            _logger.LogInformation("Admin message hub connected for live web notifications");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Admin message hub connection failed; live web popups will retry on the next refresh opportunity");
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _hubConnection.DisposeAsync();
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