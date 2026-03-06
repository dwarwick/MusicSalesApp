using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;
using Syncfusion.Blazor.Popups;
using System.Net.Http.Json;

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

        _errorMessage = string.Empty;
        _tipSuccess = false;
        _tipProcessing = false;
        _showCustomInput = false;
        _customAmount = 0;
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
            // Step 1: Validate
            var validateResponse = await Http.PostAsJsonAsync("/api/tip/validate", new
            {
                CreatorId = CreatorId,
                Amount = amount
            });

            if (!validateResponse.IsSuccessStatusCode)
            {
                var error = await validateResponse.Content.ReadFromJsonAsync<ErrorResponse>();
                _errorMessage = error?.Error ?? "Unable to process tip. Please try again.";
                _tipProcessing = false;
                await InvokeAsync(StateHasChanged);
                return;
            }

            // Step 2: Create PayPal order
            var createResponse = await Http.PostAsJsonAsync("/api/tip/create-order", new
            {
                CreatorId = CreatorId,
                Amount = amount
            });

            if (!createResponse.IsSuccessStatusCode)
            {
                var error = await createResponse.Content.ReadFromJsonAsync<ErrorResponse>();
                _errorMessage = error?.Error ?? "Failed to create payment. Please try again.";
                _tipProcessing = false;
                await InvokeAsync(StateHasChanged);
                return;
            }

            var orderResult = await createResponse.Content.ReadFromJsonAsync<CreateOrderResponse>();
            if (orderResult == null || string.IsNullOrEmpty(orderResult.OrderId))
            {
                _errorMessage = "Failed to create payment order.";
                _tipProcessing = false;
                await InvokeAsync(StateHasChanged);
                return;
            }

            // Step 3: Capture the order
            var captureResponse = await Http.PostAsJsonAsync("/api/tip/capture-order", new
            {
                CreatorId = CreatorId,
                SongMetadataId = SongMetadataId,
                Amount = amount,
                PayPalOrderId = orderResult.OrderId
            });

            if (!captureResponse.IsSuccessStatusCode)
            {
                var error = await captureResponse.Content.ReadFromJsonAsync<ErrorResponse>();
                _errorMessage = error?.Error ?? "Payment failed. Please try again.";
                _tipProcessing = false;
                await InvokeAsync(StateHasChanged);
                return;
            }

            // Success!
            _tipProcessing = false;
            _tipSuccess = true;
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing tip");
            _errorMessage = "An unexpected error occurred. Please try again.";
            _tipProcessing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private class ErrorResponse
    {
        public string Error { get; set; } = string.Empty;
    }

    private class CreateOrderResponse
    {
        public string OrderId { get; set; } = string.Empty;
    }
}
