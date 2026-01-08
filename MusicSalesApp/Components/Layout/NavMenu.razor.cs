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
    protected bool _isMenuOpen = false;
    protected bool _isDarkTheme = false;
    protected SfSidebar _sidebar;

    private bool _disposed;

    protected override async Task OnInitializedAsync()
    {
        ThemeService.OnThemeChanged += HandleThemeChanged;
        
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

    public void Dispose()
    {
        if (!_disposed)
        {
            ThemeService.OnThemeChanged -= HandleThemeChanged;
            _disposed = true;
        }
    }
}
