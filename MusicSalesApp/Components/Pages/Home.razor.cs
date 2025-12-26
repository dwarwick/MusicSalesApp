using Microsoft.AspNetCore.Components.Authorization;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Pages;

public partial class HomeModel : BlazorBase, IDisposable
{
    protected string _subscriptionPrice;
    protected bool _hasActiveSubscription = false;
    protected bool _isAuthenticated = false;
    protected List<RecommendedPlaylist> _recommendedPlaylist = new();
    protected bool _loadingRecommendations = false;
    protected int _currentUserId;
    private bool _subscriptionStatusChecked;
    private bool _isDisposed;
    private bool _hasLoadedData = false;

    protected override void OnInitialized()
    {
        _subscriptionPrice = Configuration["PayPal:SubscriptionPrice"] ?? "3.99";
        AuthenticationStateProvider.AuthenticationStateChanged += HandleAuthenticationStateChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _hasLoadedData)
        {
            return;
        }

        _hasLoadedData = true;
        
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (authState.User?.Identity?.IsAuthenticated == true)
        {
            _isAuthenticated = true;
            var appUser = await UserManager.GetUserAsync(authState.User);
            if (appUser != null)
            {
                _currentUserId = appUser.Id;
            }
            await LoadSubscriptionStatusAsync();
            await LoadRecommendedPlaylistAsync();
        }
        
        if (!_isDisposed)
        {
            await InvokeAsync(StateHasChanged);
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
                _isAuthenticated = true;
                var appUser = await UserManager.GetUserAsync(state.User);
                if (appUser != null)
                {
                    _currentUserId = appUser.Id;
                }
                await LoadSubscriptionStatusAsync();
                await LoadRecommendedPlaylistAsync();
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
        }
    }

    private async Task LoadRecommendedPlaylistAsync()
    {
        if (_currentUserId == 0)
        {
            return;
        }

        try
        {
            _loadingRecommendations = true;
            _recommendedPlaylist = await RecommendationService.GetRecommendedPlaylistAsync(_currentUserId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load recommended playlist for user {UserId}", _currentUserId);
        }
        finally
        {
            _loadingRecommendations = false;
        }
    }

    protected void PlayRecommendedPlaylist()
    {
        // Navigate to play the recommended playlist
        NavigationManager.NavigateTo($"/recommended-playlist/{_currentUserId}");
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
