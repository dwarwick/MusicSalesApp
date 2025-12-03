using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Components.Layout;

public class NavMenuModel : BlazorBase, IDisposable
{
    private static event Action OnCartUpdated;

    protected int _cartCount = 0;

    private bool _disposed;

    protected override async Task OnInitializedAsync()
    {
        CartService.OnCartUpdated += HandleCartUpdate;
        await LoadCartCount();
    }

    private async void HandleCartUpdate()
    {
        await LoadCartCount();
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadCartCount()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity?.IsAuthenticated == true)
        {
            var appUser = await UserManager.GetUserAsync(user);
            if (appUser != null)
            {
                // Use the injected CartService instance to ensure the same event subscription instance is used
                _cartCount = await CartService.GetCartItemCountAsync(appUser.Id);
            }
        }
        else
        {
            _cartCount = 0;
        }
    }   

    public void Dispose()
    {
        if (!_disposed)
        {
            CartService.OnCartUpdated -= HandleCartUpdate;
            _disposed = true;
        }
    }
}
