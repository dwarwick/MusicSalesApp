using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using Syncfusion.Blazor.Popups;
using System.Net.Http.Json;

namespace MusicSalesApp.Components.Pages;

public partial class ManageAccountModel : BlazorBase
{
    protected bool _loading = true;
    protected bool _isAuthenticated = false;
    private bool _hasLoadedData = false;
    
    protected string _successMessage = string.Empty;
    protected string _errorMessage = string.Empty;
    
    // User email for display
    protected string _userEmail = string.Empty;
    
    // Password change fields
    protected string _currentPassword = string.Empty;
    protected string _newPassword = string.Empty;
    protected string _confirmPassword = string.Empty;
    
    // Passkey fields
    protected List<Passkey> _passkeys = new();
    protected string _newPasskeyName = string.Empty;
    protected string _renamePasskeyName = string.Empty;
    protected Passkey _selectedPasskey;
    
    // Subscription fields
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
    
    // Account closure
    protected bool _hasPurchasedMusic = false;
    protected string _accountActionConfirmEmail = string.Empty;
    
    // Dialogs
    protected SfDialog _addPasskeyDialog;
    protected SfDialog _renamePasskeyDialog;
    protected SfDialog _deletePasskeyDialog;
    protected SfDialog _accountClosureDialog;
    protected SfDialog _suspendAccountDialog;
    protected SfDialog _deleteAccountDialog;
    
    private ApplicationUser _currentUser;

    [Inject]
    private IDbContextFactory<AppDbContext> DbContextFactory { get; set; }

    [SupplyParameterFromQuery(Name = "success")]
    public bool? Success { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity?.IsAuthenticated == true)
                {
                    _isAuthenticated = true;
                    _currentUser = await UserManager.GetUserAsync(user);
                    if (_currentUser != null)
                    {
                        _userEmail = _currentUser.Email ?? string.Empty;
                        await LoadPasskeys();
                        await CheckPurchasedMusic();
                        await LoadSubscriptionStatus();
                        
                        // Handle return from PayPal
                        if (Success.HasValue)
                        {
                            if (Success.Value)
                            {
                                try
                                {
                                    var activateResponse = await Http.PostAsync("api/subscription/activate-current", null);
                                    if (activateResponse.IsSuccessStatusCode)
                                    {
                                        _successMessage = "Your subscription has been activated successfully!";
                                        await LoadSubscriptionStatus();
                                    }
                                    else
                                    {
                                        _errorMessage = "Failed to activate subscription. Please contact support.";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError(ex, "Error activating subscription");
                                    _errorMessage = "An error occurred while activating your subscription.";
                                }
                            }
                            else
                            {
                                try
                                {
                                    var deleteResponse = await Http.PostAsync("api/subscription/delete-pending", null);
                                    if (deleteResponse.IsSuccessStatusCode)
                                    {
                                        _errorMessage = "Subscription setup was cancelled.";
                                    }
                                    else
                                    {
                                        _errorMessage = "Subscription setup was cancelled. Please try again if you wish to subscribe.";
                                    }
                                    await LoadSubscriptionStatus();
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogError(ex, "Error deleting pending subscription");
                                    _errorMessage = "Subscription setup was cancelled.";
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading account data");
                _errorMessage = "Error loading account data.";
            }
            finally
            {
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected async Task LoadPasskeys()
    {
        try
        {
            _passkeys = await PasskeyService.GetUserPasskeysAsync(_currentUser.Id);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading passkeys");
        }
    }

    protected async Task CheckPurchasedMusic()
    {
        try
        {
            using var context = await DbContextFactory.CreateDbContextAsync();
            _hasPurchasedMusic = await context.OwnedSongs
                .AnyAsync(os => os.UserId == _currentUser.Id && !string.IsNullOrEmpty(os.PayPalOrderId));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error checking purchased music");
        }
    }

    protected async Task ChangePassword()
    {
        _successMessage = string.Empty;
        _errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(_currentPassword) || 
            string.IsNullOrWhiteSpace(_newPassword) || 
            string.IsNullOrWhiteSpace(_confirmPassword))
        {
            _errorMessage = "All password fields are required.";
            return;
        }

        if (_newPassword != _confirmPassword)
        {
            _errorMessage = "New password and confirmation do not match.";
            return;
        }

        try
        {
            var result = await UserManager.ChangePasswordAsync(_currentUser, _currentPassword, _newPassword);
            
            if (result.Succeeded)
            {
                _successMessage = "Password changed successfully.";
                _currentPassword = string.Empty;
                _newPassword = string.Empty;
                _confirmPassword = string.Empty;
                
                // Send password changed email notification (only if email is available)
                var userEmail = _currentUser.Email;
                if (!string.IsNullOrEmpty(userEmail))
                {
                    try
                    {
                        var baseUrl = NavigationManager.BaseUri;
                        var userName = _currentUser.UserName ?? userEmail;
                        await AccountEmailService.SendPasswordChangedEmailAsync(
                            userEmail,
                            userName,
                            baseUrl);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to send password changed email to user {UserId}", _currentUser.Id);
                        // Don't fail the password change if email sending fails
                    }
                }
            }
            else
            {
                _errorMessage = string.Join(", ", result.Errors.Select(e => e.Description));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error changing password");
            _errorMessage = "An error occurred while changing your password.";
        }
    }

    protected async Task ShowAddPasskeyDialog()
    {
        _newPasskeyName = string.Empty;
        await _addPasskeyDialog.ShowAsync();
    }

    protected async Task CloseAddPasskeyDialog()
    {
        await _addPasskeyDialog.HideAsync();
    }

    protected async Task AddPasskey()
    {
        _errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(_newPasskeyName))
        {
            _errorMessage = "Please enter a name for your passkey.";
            return;
        }

        try
        {
            // Call JavaScript to initiate passkey creation with extended timeout (3 minutes)
            // Cloud password managers like Google Password Manager may need extra time
            // Note: If using Google Password Manager, ensure you have a stable internet connection
            // and that Google's services are accessible
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await JS.InvokeVoidAsync("passkeyHelper.registerPasskey", cts.Token, _newPasskeyName, _currentUser.Id);
            await CloseAddPasskeyDialog();
        }
        catch (TaskCanceledException)
        {
            Logger.LogWarning("Passkey registration timed out after 3 minutes");
            _errorMessage = "Passkey registration timed out. If using a cloud password manager (e.g., Google Password Manager), please check your internet connection and try again. Alternatively, try Windows Hello or a security key.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error adding passkey");
            _errorMessage = "Failed to add passkey. Please try again.";
        }
    }

    protected async Task ShowRenameDialog(Passkey passkey)
    {
        _selectedPasskey = passkey;
        _renamePasskeyName = passkey.Name;
        await _renamePasskeyDialog.ShowAsync();
    }

    protected async Task CloseRenameDialog()
    {
        await _renamePasskeyDialog.HideAsync();
    }

    protected async Task RenamePasskey()
    {
        _errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(_renamePasskeyName))
        {
            _errorMessage = "Please enter a new name.";
            return;
        }

        try
        {
            var success = await PasskeyService.RenamePasskeyAsync(_currentUser.Id, _selectedPasskey.Id, _renamePasskeyName);
            
            if (success)
            {
                _successMessage = "Passkey renamed successfully.";
                await LoadPasskeys();
                await CloseRenameDialog();
            }
            else
            {
                _errorMessage = "Failed to rename passkey.";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error renaming passkey");
            _errorMessage = "An error occurred while renaming the passkey.";
        }
    }

    protected async Task ShowDeleteConfirmDialog(Passkey passkey)
    {
        _selectedPasskey = passkey;
        await _deletePasskeyDialog.ShowAsync();
    }

    protected async Task CloseDeletePasskeyDialog()
    {
        await _deletePasskeyDialog.HideAsync();
    }

    protected async Task DeletePasskey()
    {
        _errorMessage = string.Empty;

        try
        {
            var success = await PasskeyService.DeletePasskeyAsync(_currentUser.Id, _selectedPasskey.Id);
            
            if (success)
            {
                _successMessage = "Passkey deleted successfully.";
                await LoadPasskeys();
                await CloseDeletePasskeyDialog();
            }
            else
            {
                _errorMessage = "Failed to delete passkey.";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error deleting passkey");
            _errorMessage = "An error occurred while deleting the passkey.";
        }
    }

    protected async Task ShowAccountClosureDialog()
    {
        _accountActionConfirmEmail = string.Empty;
        await _accountClosureDialog.ShowAsync();
    }

    protected async Task CloseAccountClosureDialog()
    {
        await _accountClosureDialog.HideAsync();
    }

    protected async Task ShowSuspendConfirmDialog()
    {
        await _accountClosureDialog.HideAsync();
        await _suspendAccountDialog.ShowAsync();
    }

    protected async Task CloseSuspendAccountDialog()
    {
        await _suspendAccountDialog.HideAsync();
    }

    protected async Task SuspendAccount()
    {
        _errorMessage = string.Empty;

        if (_accountActionConfirmEmail != _currentUser.Email)
        {
            _errorMessage = "Email does not match. Please enter your exact email address to confirm.";
            return;
        }

        try
        {
            _currentUser.IsSuspended = true;
            _currentUser.SuspendedAt = DateTime.UtcNow;
            
            var result = await UserManager.UpdateAsync(_currentUser);
            
            if (result.Succeeded)
            {
                // Send account suspended email notification (only if email is available)
                var userEmail = _currentUser.Email;
                if (!string.IsNullOrEmpty(userEmail))
                {
                    try
                    {
                        var baseUrl = NavigationManager.BaseUri;
                        var userName = _currentUser.UserName ?? userEmail;
                        await AccountEmailService.SendAccountClosedEmailAsync(
                            userEmail,
                            userName,
                            baseUrl);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to send account suspended email to user {UserId}", _currentUser.Id);
                        // Don't fail the suspension if email sending fails
                    }
                }
                
                await CloseSuspendAccountDialog();
                NavigationManager.NavigateTo("/logout", forceLoad: true);
            }
            else
            {
                _errorMessage = "Failed to suspend account: " + string.Join(", ", result.Errors.Select(e => e.Description));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error suspending account");
            _errorMessage = "An error occurred while suspending your account.";
        }
    }

    protected async Task ShowDeleteConfirmDialog()
    {
        await _accountClosureDialog.HideAsync();
        await _deleteAccountDialog.ShowAsync();
    }

    protected async Task CloseDeleteAccountDialog()
    {
        await _deleteAccountDialog.HideAsync();
    }

    protected async Task DeleteAccount()
    {
        _errorMessage = string.Empty;

        if (_accountActionConfirmEmail != _currentUser.Email)
        {
            _errorMessage = "Email does not match. Please enter your exact email address to confirm.";
            return;
        }

        // Capture user info before deletion
        var userEmail = _currentUser.Email;
        var userName = _currentUser.UserName ?? userEmail ?? "User";
        var baseUrl = NavigationManager.BaseUri;

        try
        {
            var result = await UserManager.DeleteAsync(_currentUser);
            
            if (result.Succeeded)
            {
                // Send account deleted email notification (only if email is available)
                if (!string.IsNullOrEmpty(userEmail))
                {
                    try
                    {
                        await AccountEmailService.SendAccountDeletedEmailAsync(
                            userEmail,
                            userName,
                            baseUrl);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to send account deleted email");
                        // Don't fail the deletion if email sending fails
                    }
                }
                
                await CloseDeleteAccountDialog();
                NavigationManager.NavigateTo("/logout", forceLoad: true);
            }
            else
            {
                _errorMessage = "Failed to delete account: " + string.Join(", ", result.Errors.Select(e => e.Description));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error deleting account");
            _errorMessage = "An error occurred while deleting your account.";
        }
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
            Logger.LogError(ex, "Error loading subscription status");
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
            Logger.LogError(ex, "Error creating subscription");
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
            Logger.LogError(ex, "Error cancelling subscription");
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
