using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Components.Layout;
using System.Net.Http.Json;
using System.Text.Json;

namespace MusicSalesApp.Components.Pages;

public class CheckoutModel : BlazorBase, IAsyncDisposable
{
    protected bool _loading = true;
    protected bool _isAuthenticated;
    protected List<CartItemDto> _cartItems = new List<CartItemDto>();
    protected decimal _cartTotal;
    protected bool _checkoutInProgress;
    protected bool _checkoutComplete;
    protected bool _checkoutError;
    protected bool _checkoutCancelled;
    protected string _errorMessage = string.Empty;
    protected int _purchasedCount;

    private IJSObjectReference _jsModule;
    private DotNetObjectReference<CheckoutModel> _dotNetRef;
    private bool startedPaypalInitialization;

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;

        if (_isAuthenticated)
        {
            await LoadCart();
        }

        _loading = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!startedPaypalInitialization && _isAuthenticated && _cartItems.Count > 0)
        {
            startedPaypalInitialization = true;
            await InitializePayPal();
        }
    }

    private async Task LoadCart()
    {
        try
        {
            var response = await Http.GetFromJsonAsync<CartResponse>("api/cart");
            if (response != null)
            {
                _cartItems = response.Items.ToList();
                _cartTotal = response.Total;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading cart");
        }
    }

    private async Task InitializePayPal()
    {
        try
        {
            var clientIdResponse = await Http.GetFromJsonAsync<PayPalClientIdResponse>("api/cart/paypal-client-id");
            var clientId = clientIdResponse?.ClientId;

            if (string.IsNullOrEmpty(clientId) || clientId == "__REPLACE_WITH_PAYPAL_CLIENT_ID__")
            {
                Logger.LogWarning("PayPal client ID is not configured; skipping PayPal initialization.");
                return;
            }

            _dotNetRef = DotNetObjectReference.Create(this);
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/Checkout.razor.js");
            await _jsModule.InvokeVoidAsync("initPayPal", clientId, _cartTotal.ToString("F2"), _dotNetRef);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error initializing PayPal");
        }
    }

    protected async Task RemoveItem(string songFileName)
    {
        try
        {
            var response = await Http.PostAsJsonAsync("api/cart/remove", new { SongFileName = songFileName });
            if (response.IsSuccessStatusCode)
            {
                _cartItems.RemoveAll(i => i.SongFileName == songFileName);
                _cartTotal = _cartItems.Sum(i => i.Price);
                CartService.NotifyCartUpdated();
                await InvokeAsync(StateHasChanged);

                // Reinitialize PayPal if there are still items
                if (_cartItems.Count > 0)
                {
                    await InitializePayPal();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error removing cart item {SongFileName}", songFileName);
        }
    }

    [JSInvokable]
    public async Task<string> CreateOrder()
    {
        try
        {
            Logger.LogInformation("CreateOrder invoked via JavaScript");
            var response = await Http.PostAsync("api/cart/create-order", null);
            Logger.LogInformation("CreateOrder response status: {StatusCode}", response.StatusCode);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CreateOrderResponse>();
                Logger.LogInformation("Created PayPal order {OrderId}", result?.OrderId);
                return result?.OrderId ?? string.Empty;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Logger.LogWarning("CreateOrder failed with status {StatusCode}: {Content}", response.StatusCode, errorContent);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error creating PayPal order");
        }

        return string.Empty;
    }

    [JSInvokable]
    public async Task SetProcessing(bool processing)
    {
        Logger.LogInformation("SetProcessing called with value {Processing}", processing);
        _checkoutInProgress = processing;
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task OnApprove(JsonElement payload)
    {
        string orderId = null;
        string payPalOrderId = null;

        try
        {
            if (payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("orderId", out var o)) orderId = o.GetString();
                if (payload.TryGetProperty("payPalOrderId", out var p)) payPalOrderId = p.GetString();
            }
            else if (payload.ValueKind == JsonValueKind.String)
            {
                orderId = payload.GetString();
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Unable to parse PayPal approval payload");
        }

        Logger.LogInformation("OnApprove invoked for orderId {OrderId} / PayPal order {PayPalOrderId}", orderId, payPalOrderId);
        
        try
        {
            var response = await Http.PostAsJsonAsync("api/cart/capture-order", new { OrderId = orderId, PayPalOrderId = payPalOrderId });
            Logger.LogInformation("capture-order response status: {StatusCode}", response.StatusCode);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CaptureOrderResponse>();
                Logger.LogInformation("Purchase completed, {Count} songs bought", result?.PurchasedCount);
                _purchasedCount = result?.PurchasedCount ?? 0;
                _checkoutComplete = true;
                _checkoutError = false;
                _checkoutCancelled = false;
                _errorMessage = string.Empty;
                _cartItems.Clear();
                _cartTotal = 0;
                CartService.NotifyCartUpdated();
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Logger.LogWarning("capture-order error: {Content}", errorContent);
                
                // Try to parse error response for user-friendly message
                string errorMessage = "Payment could not be processed. Please try again.";
                try
                {
                    var errorResult = await response.Content.ReadFromJsonAsync<CaptureOrderErrorResponse>();
                    if (!string.IsNullOrEmpty(errorResult?.Error))
                    {
                        errorMessage = errorResult.Error;
                    }
                }
                catch
                {
                    // If parsing fails, use default message
                }
                
                _checkoutError = true;
                _checkoutComplete = false;
                _errorMessage = errorMessage;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error capturing order");
            _checkoutError = true;
            _checkoutComplete = false;
            _errorMessage = "An unexpected error occurred. Please try again or contact support.";
        }
        finally
        {
            _checkoutInProgress = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    [JSInvokable]
    public async Task OnCancel()
    {
        Logger.LogInformation("OnCancel called - payment was cancelled");
        _checkoutInProgress = false;
        _checkoutCancelled = true;
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task OnError(string error)
    {
        Logger.LogError("OnError called - PayPal error: {Error}", error);
        _checkoutInProgress = false;
        _checkoutError = true;
        _errorMessage = !string.IsNullOrEmpty(error) 
            ? $"There was an error processing your payment: {error}" 
            : "There was an error processing your payment. Please try again.";
        await InvokeAsync(StateHasChanged);
    }

    protected async Task ResetCheckout()
    {
        _checkoutError = false;
        _checkoutCancelled = false;
        _errorMessage = string.Empty;
        await InvokeAsync(StateHasChanged);
        
        // Reinitialize PayPal buttons if there are still items
        if (_cartItems.Count > 0)
        {
            startedPaypalInitialization = false;
            await InitializePayPal();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_jsModule != null)
            {
                await _jsModule.DisposeAsync();
            }
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "JS runtime disconnected while disposing checkout module");
        }
        _dotNetRef?.Dispose();
    }
}

public class CartResponse
{
    public IEnumerable<CartItemDto> Items { get; set; } = new List<CartItemDto>();
    public decimal Total { get; set; }
}

public class CartItemDto
{
    public string SongFileName { get; set; }
    public string SongTitle { get; set; }
    public decimal Price { get; set; }
    public DateTime AddedAt { get; set; }
}

public class PayPalClientIdResponse
{
    public string ClientId { get; set; }
}

public class CreateOrderResponse
{
    public string OrderId { get; set; }
    public string Amount { get; set; }
}

public class CaptureOrderResponse
{
    public bool Success { get; set; }
    public int PurchasedCount { get; set; }
}

public class CaptureOrderErrorResponse
{
    public bool Success { get; set; }
    public string Error { get; set; }
}
