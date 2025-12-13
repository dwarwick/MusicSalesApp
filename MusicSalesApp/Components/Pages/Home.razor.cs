using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Pages;

public partial class HomeModel : BlazorBase, IDisposable
{
    [Microsoft.AspNetCore.Components.Inject]
    protected IConfiguration Configuration { get; set; }

    protected string _subscriptionPrice;
    protected bool _hasActiveSubscription = false;
    private bool _subscriptionStatusChecked;
    private bool _isDisposed;

    protected override void OnInitialized()
    {
        _subscriptionPrice = Configuration["PayPal:SubscriptionPrice"] ?? "3.99";
        AuthenticationStateProvider.AuthenticationStateChanged += HandleAuthenticationStateChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _subscriptionStatusChecked)
        {
            return;
        }

        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (authState.User?.Identity?.IsAuthenticated == true)
        {
            await LoadSubscriptionStatusAsync();
        }
    }

    private async void HandleAuthenticationStateChanged(Task<AuthenticationState> authenticationStateTask)
    {
        if (_subscriptionStatusChecked)
        {
            return;
        }

        try
        {
            var state = await authenticationStateTask;
            if (state.User?.Identity?.IsAuthenticated == true)
            {
                await LoadSubscriptionStatusAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to react to authentication state changes.");
        }
    }

    private async Task LoadSubscriptionStatusAsync()
    {
        if (_subscriptionStatusChecked)
        {
            return;
        }

        try
        {
            var subscriptionResponse = await Http.GetFromJsonAsync<SubscriptionStatusDto>("api/subscription/status");
            _hasActiveSubscription = subscriptionResponse?.HasSubscription ?? false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to retrieve subscription status.");
        }
        finally
        {
            _subscriptionStatusChecked = true;
            if (!_isDisposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        AuthenticationStateProvider.AuthenticationStateChanged -= HandleAuthenticationStateChanged;
        _isDisposed = true;
    }
}
