using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using Syncfusion.Blazor.Popups;

namespace MusicSalesApp.Components.Pages;

public partial class ManageAccountModel : BlazorBase
{
    protected bool _loading = true;
    protected bool _isAuthenticated = false;
    private bool _hasLoadedData = false;
    
    protected string _successMessage = string.Empty;
    protected string _errorMessage = string.Empty;
    
    // Password change fields
    protected string _currentPassword = string.Empty;
    protected string _newPassword = string.Empty;
    protected string _confirmPassword = string.Empty;
    
    // Passkey fields
    protected List<Passkey> _passkeys = new();
    protected string _newPasskeyName = string.Empty;
    protected string _renamePasskeyName = string.Empty;
    protected Passkey _selectedPasskey;
    
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
                        await LoadPasskeys();
                        await CheckPurchasedMusic();
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
            // Call JavaScript to initiate passkey creation
            await JS.InvokeVoidAsync("passkeyHelper.registerPasskey", _newPasskeyName, _currentUser.Id);
            await CloseAddPasskeyDialog();
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

        try
        {
            var result = await UserManager.DeleteAsync(_currentUser);
            
            if (result.Succeeded)
            {
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
}
