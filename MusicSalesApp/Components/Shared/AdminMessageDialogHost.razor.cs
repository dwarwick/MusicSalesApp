#nullable enable

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Popups;

namespace MusicSalesApp.Components.Shared;

public class AdminMessageDialogHostModel : BlazorBase, IAsyncDisposable
{
    private const string DefaultDialogTitle = "Message from StreamTunes";

    protected bool _showDialog;
    protected PendingAdminMessageDto? _currentMessage;
    protected string _createdDateText = string.Empty;
    protected string _dialogTitle = DefaultDialogTitle;
    protected bool _showSourceLabel;

    private readonly List<PendingAdminMessageDto> _pendingMessages = [];
    private bool _hasLoadedData;
    private bool _disposed;
    private int? _currentUserId;

    protected bool _isAcknowledging;

    protected override void OnInitialized()
    {
        AdminMessageHubClient.OnAdminMessagesUpdated += HandleAdminMessagesUpdated;
        AuthenticationStateProvider.AuthenticationStateChanged += HandleAuthenticationStateChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            await LoadForCurrentUserAsync();
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task AcknowledgeCurrentDialogAsync()
    {
        if (_isAcknowledging || _currentMessage == null || !_currentUserId.HasValue)
        {
            return;
        }

        _isAcknowledging = true;
        var messageId = _currentMessage.MessageId;

        try
        {
            await AdminMessageService.AcknowledgeMessageAsync(_currentUserId.Value, messageId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to acknowledge admin message {MessageId}", messageId);
        }
        finally
        {
            _isAcknowledging = false;
            await AdvancePastMessageAsync(messageId);
            await InvokeAsync(StateHasChanged);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            AdminMessageHubClient.OnAdminMessagesUpdated -= HandleAdminMessagesUpdated;
            AuthenticationStateProvider.AuthenticationStateChanged -= HandleAuthenticationStateChanged;
            _disposed = true;
        }

        await ValueTask.CompletedTask;
    }

    private void HandleAdminMessagesUpdated() => DispatchUiUpdate(async () =>
    {
        await AdminMessageHubClient.StartAsync();
        await RefreshPendingMessagesAsync();
    });

    private void HandleAuthenticationStateChanged(Task<AuthenticationState> authenticationStateTask)
        => DispatchUiUpdate(async () =>
        {
            try
            {
                var authState = await authenticationStateTask;
                await LoadForUserAsync(authState);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to refresh admin message dialog host after auth state changed");
            }
        });

    private async Task LoadForCurrentUserAsync()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        await LoadForUserAsync(authState);
    }

    private async Task LoadForUserAsync(AuthenticationState authState)
    {
        _currentUserId = GetUserId(authState.User);
        if (!_currentUserId.HasValue)
        {
            await ClearCurrentMessageAsync();
            return;
        }

        await AdminMessageHubClient.StartAsync();
        await RefreshPendingMessagesAsync();
    }

    private async Task RefreshPendingMessagesAsync()
    {
        if (!_currentUserId.HasValue)
        {
            await ClearCurrentMessageAsync();
            return;
        }

        var messages = await AdminMessageService.GetPendingDialogMessagesAsync(_currentUserId.Value);
        _pendingMessages.Clear();
        _pendingMessages.AddRange(messages.OrderBy(message => message.CreatedAtUtc));

        if (_currentMessage != null && _pendingMessages.Any(message => message.MessageId == _currentMessage.MessageId))
        {
            if (!_showDialog && !_isAcknowledging)
            {
                _showDialog = true;
            }

            return;
        }

        await ShowNextPendingMessageAsync();
    }

    private async Task AdvancePastMessageAsync(int messageId)
    {
        _pendingMessages.RemoveAll(message => message.MessageId == messageId);
        await ShowNextPendingMessageAsync();
    }

    private async Task ShowNextPendingMessageAsync()
    {
        var nextMessage = _pendingMessages.FirstOrDefault();
        if (nextMessage == null)
        {
            await ClearCurrentMessageAsync();
            return;
        }

        _currentMessage = nextMessage;
        _dialogTitle = GetDialogTitle(nextMessage.Subject);
        _showSourceLabel = !string.Equals(_dialogTitle, DefaultDialogTitle, StringComparison.Ordinal);
        _createdDateText = await FormatCreatedDateAsync(nextMessage.CreatedAtUtc);
        _showDialog = true;
    }

    private async Task ClearCurrentMessageAsync()
    {
        _currentMessage = null;
        _createdDateText = string.Empty;
        _dialogTitle = DefaultDialogTitle;
        _showSourceLabel = false;
        _showDialog = false;
        await InvokeAsync(StateHasChanged);
    }

    private async Task<string> FormatCreatedDateAsync(DateTime createdAtUtc)
    {
        try
        {
            return await JS.InvokeAsync<string>("dashboardHelper.formatAdminMessageDate", createdAtUtc.ToString("O"));
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Falling back to UTC formatting for admin message date");
            return createdAtUtc.ToString("d");
        }
    }

    private static string GetDialogTitle(string? subject)
    {
        return string.IsNullOrWhiteSpace(subject)
            ? DefaultDialogTitle
            : subject.Trim();
    }
}