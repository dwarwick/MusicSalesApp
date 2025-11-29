using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Components.Layout;

public partial class NavMenuModel : BlazorBase, IDisposable
{
    protected int _cartCount = 0;

    private static event Action OnCartUpdated;
    private bool _disposed;

    protected override async Task OnInitializedAsync()
    {
        OnCartUpdated += HandleCartUpdate;
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
            using var scope = ScopeFactory.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var cartService = scope.ServiceProvider.GetRequiredService<ICartService>();

            var appUser = await userManager.GetUserAsync(user);
            if (appUser != null)
            {
                _cartCount = await cartService.GetCartItemCountAsync(appUser.Id);
            }
        }
        else
        {
            _cartCount = 0;
        }
    }

    public static void NotifyCartUpdated()
    {
        OnCartUpdated?.Invoke();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            OnCartUpdated -= HandleCartUpdate;
            _disposed = true;
        }
    }
}
