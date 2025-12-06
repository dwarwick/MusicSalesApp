using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor.Navigations;
using MusicSalesApp.Common.Helpers;

namespace MusicSalesApp.Components.Layout;

public class NavMenuModel : BlazorBase, IDisposable
{
    protected int _cartCount = 0;
    protected bool _isMenuOpen = false;
    protected SfSidebar _sidebar;
    protected List<NavMenuItem> _menuItems = new();

    private bool _disposed;

    protected override async Task OnInitializedAsync()
    {
        CartService.OnCartUpdated += HandleCartUpdate;
        await LoadCartCount();
        await BuildMenuItems();
    }

    protected async Task BuildMenuItems()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        _menuItems = new List<NavMenuItem>
        {
            new NavMenuItem { Text = "Home", Url = "/", IconCss = "e-icons e-home" },
            new NavMenuItem { Text = "Counter", Url = "/counter", IconCss = "e-icons e-add-icon" },
            new NavMenuItem { Text = "Weather", Url = "/weather", IconCss = "e-icons e-list-unordered" },
            new NavMenuItem { Text = "Music Library", Url = "/music-library", IconCss = "e-icons e-music" }
        };

        if (user.Identity?.IsAuthenticated == true)
        {
            if (user.HasClaim(c => c.Type == "Permission" && c.Value == Permissions.UploadFiles))
            {
                _menuItems.Add(new NavMenuItem { Text = "Upload Files", Url = "/upload-files", IconCss = "e-icons e-upload" });
            }

            if (user.HasClaim(c => c.Type == "Permission" && c.Value == Permissions.ManageUsers))
            {
                _menuItems.Add(new NavMenuItem { Text = "Song Management", Url = "/admin/songs", IconCss = "e-icons e-settings" });
            }

            _menuItems.Add(new NavMenuItem { Text = "Logout", Url = "/logout", IconCss = "e-icons e-log-out" });
        }
        else
        {
            _menuItems.Add(new NavMenuItem { Text = "Login", Url = "/login", IconCss = "e-icons e-login" });
            _menuItems.Add(new NavMenuItem { Text = "Register", Url = "/register", IconCss = "e-icons e-user-add" });
        }

        await InvokeAsync(StateHasChanged);
    }

    protected void ToggleMenu()
    {
        _isMenuOpen = !_isMenuOpen;
    }

    protected void NavigateToCart()
    {
        NavigationManager.NavigateTo("/checkout");
    }

    protected void OnMenuItemSelected(MenuEventArgs<NavMenuItem> args)
    {
        if (!string.IsNullOrEmpty(args.Item.Url))
        {
            NavigationManager.NavigateTo(args.Item.Url);
            _isMenuOpen = false;
        }
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

    public class NavMenuItem
    {
        public string Text { get; set; }
        public string Url { get; set; }
        public string IconCss { get; set; }
    }
}
