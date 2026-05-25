#nullable enable

using MusicSalesApp.Components.Base;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Grids;

namespace MusicSalesApp.Components.Pages.Admin;

public class ContactMessagesModel : BlazorBase
{
    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected bool _showViewDialog;
    protected List<ContactRequestSubmissionDto> _messages = [];
    protected ContactRequestSubmissionDto? _viewMessage;
    protected SfGrid<ContactRequestSubmissionDto>? _grid;

    private bool _hasLoadedData;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                await ReloadMessagesAsync();
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load contact messages: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected void OpenViewDialog(ContactRequestSubmissionDto message)
    {
        _viewMessage = message;
        _showViewDialog = true;
    }

    protected void CloseViewDialog()
    {
        _showViewDialog = false;
    }

    protected static string FormatNullableUtc(DateTime? value)
    {
        return value.HasValue ? FormatUtc(value.Value) : "-";
    }

    protected static string FormatUtc(DateTime value)
    {
        return $"{value:g} UTC";
    }

    private async Task ReloadMessagesAsync()
    {
        _messages = (await ContactRequestAdminService.GetSubmissionsAsync()).ToList();
    }
}