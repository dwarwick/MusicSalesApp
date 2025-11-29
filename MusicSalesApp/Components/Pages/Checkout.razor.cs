using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Components.Layout;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Pages;

public class CheckoutModel : BlazorBase, IAsyncDisposable
{
    protected bool _loading = true;
    protected bool _isAuthenticated;
    protected List<CartItemDto> _cartItems = new List<CartItemDto>();
    protected decimal _cartTotal;
    protected bool _checkoutInProgress;
    protected bool _checkoutComplete;
    protected int _purchasedCount;

    private IJSObjectReference _jsModule;
    private DotNetObjectReference<CheckoutModel> _dotNetRef;

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
        if (firstRender && _isAuthenticated && _cartItems.Count > 0)
        {
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
            Console.WriteLine($"Error loading cart: {ex.Message}");
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
                // PayPal not configured, skip initialization
                return;
            }

            _dotNetRef = DotNetObjectReference.Create(this);
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/Checkout.razor.js");
            await _jsModule.InvokeVoidAsync("initPayPal", clientId, _cartTotal.ToString("F2"), _dotNetRef);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error initializing PayPal: {ex.Message}");
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
                NavMenuModel.NotifyCartUpdated();
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
            Console.WriteLine($"Error removing item: {ex.Message}");
        }
    }

    [JSInvokable]
    public async Task<string> CreateOrder()
    {
        _checkoutInProgress = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var response = await Http.PostAsync("api/cart/create-order", null);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CreateOrderResponse>();
                return result?.OrderId ?? "";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating order: {ex.Message}");
        }

        _checkoutInProgress = false;
        await InvokeAsync(StateHasChanged);
        return "";
    }

    [JSInvokable]
    public async Task OnApprove(string orderId)
    {
        try
        {
            var response = await Http.PostAsJsonAsync("api/cart/capture-order", new { OrderId = orderId });
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CaptureOrderResponse>();
                _purchasedCount = result?.PurchasedCount ?? 0;
                _checkoutComplete = true;
                _cartItems.Clear();
                _cartTotal = 0;
                NavMenuModel.NotifyCartUpdated();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error capturing order: {ex.Message}");
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
        _checkoutInProgress = false;
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task OnError(string error)
    {
        Console.WriteLine($"PayPal error: {error}");
        _checkoutInProgress = false;
        await InvokeAsync(StateHasChanged);
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
        catch (JSDisconnectedException)
        {
            // Circuit is already disconnected, safe to ignore
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
