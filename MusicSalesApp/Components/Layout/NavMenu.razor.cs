using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Navigations;

namespace MusicSalesApp.Components.Layout;

public class NavMenuModel : BlazorBase, IDisposable
{
    protected int _cartCount = 0;
    protected bool _isMenuOpen = false;
    protected SfSidebar _sidebar;

    private bool _disposed;

    protected override async Task OnInitializedAsync()
    {
        CartService.OnCartUpdated += HandleCartUpdate;
        await LoadCartCount();
    }

    protected void ToggleMenu()
    {
        _isMenuOpen = !_isMenuOpen;
    }

    protected void CloseMenu()
    {
        _isMenuOpen = false;
    }

    protected void NavigateToCart()
    {
        NavigationManager.NavigateTo("/checkout");
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
