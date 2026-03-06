using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;
using Syncfusion.Blazor.Popups;

namespace MusicSalesApp.Components.Shared;

public partial class TipDialogModel : BlazorBase
{
    protected bool _showDialog;
    protected bool _tipProcessing;
    protected bool _tipSuccess;
    protected bool _showCustomInput;
    protected decimal _tipAmount;
    protected decimal _customAmount;
    protected string _errorMessage = string.Empty;
    private int _currentUserId;

    [Parameter]
    public int CreatorId { get; set; }

    [Parameter]
    public int? SongMetadataId { get; set; }

    [Parameter]
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Shows the tip dialog.
    /// </summary>
    public async Task ShowAsync()
    {
        if (!IsAuthenticated)
        {
            NavigationManager.NavigateTo("/login?returnUrl=" + Uri.EscapeDataString(NavigationManager.Uri), forceLoad: true);
            return;
        }

        // Get current user ID
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var user = await UserManager.GetUserAsync(authState.User);
        if (user == null) return;
        _currentUserId = user.Id;

        _errorMessage = string.Empty;
        _tipSuccess = false;
        _tipProcessing = false;
        _showCustomInput = false;
        _customAmount = 0;
        _showDialog = true;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Shows the tip success state (called after returning from PayPal).
    /// </summary>
    public async Task ShowSuccessAsync(decimal amount)
    {
        _tipAmount = amount;
        _tipSuccess = true;
        _tipProcessing = false;
        _errorMessage = string.Empty;
        _showDialog = true;
        await InvokeAsync(StateHasChanged);
    }

    protected void OnDialogClosing(BeforeCloseEventArgs args)
    {
        _showDialog = false;
    }

    protected void ShowCustomInput()
    {
        _showCustomInput = true;
    }

    protected bool IsCustomAmountValid()
    {
        return _customAmount >= 1.00m && _customAmount <= 50.00m;
    }

    protected async Task SubmitPresetTip(decimal amount)
    {
        await ProcessTip(amount);
    }

    protected async Task SubmitCustomTip()
    {
        if (!IsCustomAmountValid())
        {
            _errorMessage = "Please enter an amount between $1.00 and $50.00.";
            return;
        }
        await ProcessTip(Math.Round(_customAmount, 2));
    }

    private async Task ProcessTip(decimal amount)
    {
        _errorMessage = string.Empty;
        _tipAmount = amount;
        _tipProcessing = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            // Get the current page URL to use as the return URL after PayPal approval
            var currentUrl = NavigationManager.Uri;

            // Create a PayPal order and get the approval URL
            var (success, errorMessage, approvalUrl) = await TipService.CreateTipOrderAsync(
                _currentUserId,
                CreatorId,
                SongMetadataId,
                amount,
                ipAddress: null,
                fingerprint: null,
                returnUrl: currentUrl);

            if (!success || string.IsNullOrEmpty(approvalUrl))
            {
                _errorMessage = errorMessage ?? "Unable to create tip payment. Please try again.";
                _tipProcessing = false;
                await InvokeAsync(StateHasChanged);
                return;
            }

            // Redirect to PayPal for buyer approval
            NavigationManager.NavigateTo(approvalUrl, forceLoad: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error creating tip order");
            _errorMessage = "An unexpected error occurred. Please try again.";
            _tipProcessing = false;
            await InvokeAsync(StateHasChanged);
        }
    }
}
