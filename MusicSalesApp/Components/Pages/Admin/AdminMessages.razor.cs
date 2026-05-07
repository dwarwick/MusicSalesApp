#nullable enable

using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Grids;

namespace MusicSalesApp.Components.Pages.Admin;

public class AdminMessagesModel : BlazorBase
{
    protected bool _isLoading = true;
    protected bool _isSaving;
    protected string _errorMessage = string.Empty;
    protected string? _successMessage;
    protected string _subject = string.Empty;
    protected string _messageText = string.Empty;
    protected string[] _selectedRoleNames = [];
    protected bool _sendEmail;
    protected bool _showDialog = true;
    protected bool _showViewDialog;
    protected List<string> _availableRoles = [];
    protected List<AdminMessageSummaryDto> _messages = [];
    protected List<string> _validationErrors = [];
    protected AdminMessageSummaryDto? _viewMessage;
    protected SfGrid<AdminMessageSummaryDto>? _grid;

    private bool _hasLoadedData;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await LoadPageAsync();
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load admin messages: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task CreateMessageAsync()
    {
        _validationErrors.Clear();
        _successMessage = null;

        ValidateForm();
        if (_validationErrors.Count > 0)
        {
            return;
        }

        _isSaving = true;
        try
        {
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var userId = GetUserId(authState.User);
            if (!userId.HasValue)
            {
                _validationErrors.Add("Unable to determine the current admin user.");
                return;
            }

            await AdminMessageService.CreateMessageAsync(new CreateAdminMessageRequest
            {
                Subject = _subject,
                MessageText = _messageText,
                RoleNames = _selectedRoleNames,
                SendEmail = _sendEmail,
                ShowDialog = _showDialog
            }, userId.Value);

            ResetForm();
            await ReloadMessagesAsync();
            _successMessage = "Admin message created successfully.";
        }
        catch (Exception ex)
        {
            _validationErrors.Add(ex.Message);
        }
        finally
        {
            _isSaving = false;
        }
    }

    protected async Task CancelMessageAsync(AdminMessageSummaryDto message)
    {
        _validationErrors.Clear();
        _successMessage = null;

        try
        {
            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var userId = GetUserId(authState.User);
            if (!userId.HasValue)
            {
                _validationErrors.Add("Unable to determine the current admin user.");
                return;
            }

            var canceledCount = await AdminMessageService.CancelMessageAsync(message.Id, userId.Value);
            await ReloadMessagesAsync();
            _successMessage = canceledCount == 0
                ? "No pending recipients remained for that message."
                : $"Canceled the message for {canceledCount} unacknowledged recipients.";
        }
        catch (Exception ex)
        {
            _validationErrors.Add(ex.Message);
        }
    }

    protected void OpenViewDialog(AdminMessageSummaryDto message)
    {
        _viewMessage = message;
        _showViewDialog = true;
    }

    protected void CloseViewDialog()
    {
        _showViewDialog = false;
    }

    protected void ResetForm()
    {
        _subject = string.Empty;
        _messageText = string.Empty;
        _selectedRoleNames = [];
        _sendEmail = false;
        _showDialog = true;
        _validationErrors.Clear();
    }

    protected static string GetPreviewText(string messageText)
    {
        var normalized = string.Join(" ", messageText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length <= 140 ? normalized : normalized[..140] + "...";
    }

    protected static string GetChannelText(AdminMessageSummaryDto message)
    {
        var channels = new List<string>();
        if (message.ShowDialog)
        {
            channels.Add("Dialogue");
        }

        if (message.SendEmail)
        {
            channels.Add("Email");
        }

        return channels.Count == 0 ? "-" : string.Join(", ", channels);
    }

    private void ValidateForm()
    {
        if (string.IsNullOrWhiteSpace(_subject))
        {
            _validationErrors.Add("Subject is required.");
        }

        if (string.IsNullOrWhiteSpace(_messageText))
        {
            _validationErrors.Add("Message text is required.");
        }

        if (_selectedRoleNames.Length == 0)
        {
            _validationErrors.Add("Select at least one role.");
        }

        if (!_sendEmail && !_showDialog)
        {
            _validationErrors.Add("Select at least one delivery channel.");
        }
    }

    private async Task LoadPageAsync()
    {
        _availableRoles = (await AdminMessageService.GetAvailableRoleNamesAsync()).ToList();
        await ReloadMessagesAsync();
    }

    private async Task ReloadMessagesAsync()
    {
        _messages = (await AdminMessageService.GetMessagesAsync()).ToList();
    }
}