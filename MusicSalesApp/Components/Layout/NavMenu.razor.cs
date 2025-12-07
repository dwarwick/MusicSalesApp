using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Buttons;
using Syncfusion.Blazor.Navigations;

namespace MusicSalesApp.Components.Layout;

public class NavMenuModel : BlazorBase, IDisposable
{
    protected int _cartCount = 0;
    protected bool _isMenuOpen = false;
    protected bool _isDarkTheme = false;
    protected SfSidebar _sidebar;

    private bool _disposed;

    protected override async Task OnInitializedAsync()
    {
        CartService.OnCartUpdated += HandleCartUpdate;
        ThemeService.OnThemeChanged += HandleThemeChanged;
        
        await LoadCartCount();
        await InitializeTheme();
    }

    private async Task InitializeTheme()
    {
        await ThemeService.InitializeThemeAsync();
        _isDarkTheme = ThemeService.IsDarkTheme;
    }

    protected async Task OnThemeChanged(ChangeEventArgs<bool> args)
    {
        var theme = args.Checked ? "Dark" : "Light";
        await ThemeService.SetThemeAsync(theme);
    }

    private void HandleThemeChanged()
    {
        _isDarkTheme = ThemeService.IsDarkTheme;
        InvokeAsync(StateHasChanged);
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
            ThemeService.OnThemeChanged -= HandleThemeChanged;
            _disposed = true;
        }
    }
}
