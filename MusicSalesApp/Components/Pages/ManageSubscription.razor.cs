using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Pages;

public class ManageSubscriptionModel : BlazorBase
{
    protected bool _loading = true;
    protected bool _isAuthenticated;
    protected bool _hasSubscription;
    protected string _subscriptionStatus;
    protected decimal _monthlyPrice;
    protected DateTime? _startDate;
    protected DateTime? _endDate;
    protected DateTime? _nextBillingDate;
    protected string _paypalSubscriptionId;
    protected string _subscriptionPrice = "3.99";
    protected bool _agreeToTerms = false;
    protected bool _subscribing = false;
    protected bool _cancelling = false;
    protected string _successMessage;
    protected string _errorMessage;

    [SupplyParameterFromQuery(Name = "success")]
    public bool? Success { get; set; }

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        _isAuthenticated = authState.User.Identity?.IsAuthenticated == true;

        if (_isAuthenticated)
        {
            await LoadSubscriptionStatus();
            
            // Handle return from PayPal
            if (Success.HasValue)
            {
                if (Success.Value)
                {
                    // Activate the subscription and fetch details from PayPal
                    // The subscription is already in the database, we just need to update it with PayPal details
                    try
                    {
                        var activateResponse = await Http.PostAsync("api/subscription/activate-current", null);
                        if (activateResponse.IsSuccessStatusCode)
                        {
                            _successMessage = "Your subscription has been activated successfully!";
                            await LoadSubscriptionStatus(); // Refresh status
                        }
                        else
                        {
                            _errorMessage = "Failed to activate subscription. Please contact support.";
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error activating subscription: {ex.Message}");
                        _errorMessage = "An error occurred while activating your subscription.";
                    }
                }
                else
                {
                    _errorMessage = "Subscription setup was cancelled.";
                }
            }
        }

        _loading = false;
    }

    private async Task LoadSubscriptionStatus()
    {
        try
        {
            var response = await Http.GetFromJsonAsync<SubscriptionStatusResponse>("api/subscription/status");
            if (response != null)
            {
                _hasSubscription = response.HasSubscription;
                _subscriptionStatus = response.Status ?? "N/A";
                _monthlyPrice = response.MonthlyPrice;
                _startDate = response.StartDate;
                _endDate = response.EndDate;
                _nextBillingDate = response.NextBillingDate;
                _paypalSubscriptionId = response.PaypalSubscriptionId;
                _subscriptionPrice = response.SubscriptionPrice ?? "3.99";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading subscription status: {ex.Message}");
        }
    }

    protected async Task Subscribe()
    {
        if (!_agreeToTerms)
        {
            _errorMessage = "You must agree to the terms and conditions to subscribe.";
            return;
        }

        _subscribing = true;
        _errorMessage = null;
        _successMessage = null;

        try
        {
            var response = await Http.PostAsJsonAsync("api/subscription/create", new { AgreeToTerms = _agreeToTerms });
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CreateSubscriptionResponse>();
                
                if (!string.IsNullOrEmpty(result?.ApprovalUrl))
                {
                    // Redirect to PayPal for approval
                    NavigationManager.NavigateTo(result.ApprovalUrl, forceLoad: true);
                }
                else
                {
                    _errorMessage = "Failed to create subscription. Please try again.";
                    _subscribing = false;
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _errorMessage = $"Failed to create subscription: {errorContent}";
                _subscribing = false;
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Error creating subscription: {ex.Message}";
            _subscribing = false;
        }

        await InvokeAsync(StateHasChanged);
    }

    protected async Task CancelSubscription()
    {
        if (!await JS.InvokeAsync<bool>("confirm", "Are you sure you want to cancel your subscription? You will have access until the end of your current billing period."))
        {
            return;
        }

        _cancelling = true;
        _errorMessage = null;
        _successMessage = null;

        try
        {
            var response = await Http.PostAsync("api/subscription/cancel", null);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CancelSubscriptionResponse>();
                
                if (result?.Success == true)
                {
                    await LoadSubscriptionStatus();
                    _successMessage = $"Your subscription has been cancelled. You can continue to listen to unlimited music until {_endDate?.ToLocalTime().ToString("MMMM dd, yyyy h:mm tt")}.";
                }
                else
                {
                    _errorMessage = "Failed to cancel subscription. Please try again.";
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _errorMessage = $"Failed to cancel subscription: {errorContent}";
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Error cancelling subscription: {ex.Message}";
        }
        finally
        {
            _cancelling = false;
            await InvokeAsync(StateHasChanged);
        }
    }
}

public class SubscriptionStatusResponse
{
    public bool HasSubscription { get; set; }
    public string Status { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? NextBillingDate { get; set; }
    public decimal MonthlyPrice { get; set; }
    public string PaypalSubscriptionId { get; set; }
    public string SubscriptionPrice { get; set; }
}

public class CreateSubscriptionResponse
{
    public bool Success { get; set; }
    public string SubscriptionId { get; set; }
    public string ApprovalUrl { get; set; }
}

public class CancelSubscriptionResponse
{
    public bool Success { get; set; }
    public DateTime? EndDate { get; set; }
}
