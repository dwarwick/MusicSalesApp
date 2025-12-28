using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class ResetPasswordModel : BlazorBase
{
    [SupplyParameterFromQuery(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [SupplyParameterFromQuery(Name = "token")]
    public string Token { get; set; } = string.Empty;

    [Required(ErrorMessage = "New password is required")]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Please confirm your password")]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match")]
    public string ConfirmPassword { get; set; } = string.Empty;

    protected bool isLoading = true;
    protected bool isTokenValid = false;
    protected bool isSuccess = false;
    protected bool isSubmitting = false;
    protected string errorMessage = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        // Check if already logged in
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (authState.User?.Identity?.IsAuthenticated == true)
        {
            NavigationManager.NavigateTo("/", forceLoad: true);
            return;
        }

        // Validate parameters
        if (string.IsNullOrEmpty(UserId) || string.IsNullOrEmpty(Token))
        {
            isLoading = false;
            isTokenValid = false;
            errorMessage = "Invalid password reset link. The link may be malformed or missing required parameters.";
            return;
        }

        // Verify the token is valid
        var (isValid, error) = await AuthenticationService.VerifyPasswordResetTokenAsync(UserId, Token);
        isLoading = false;
        isTokenValid = isValid;

        if (!isValid && !string.IsNullOrEmpty(error))
        {
            errorMessage = error;
        }
    }

    protected async Task HandleSubmit(EditContext context)
    {
        if (isSubmitting) return;

        errorMessage = string.Empty;
        isSubmitting = true;

        try
        {
            if (string.IsNullOrWhiteSpace(NewPassword))
            {
                errorMessage = "Please enter a new password.";
                return;
            }

            if (NewPassword != ConfirmPassword)
            {
                errorMessage = "Passwords do not match.";
                return;
            }

            var (success, error) = await AuthenticationService.ResetPasswordAsync(UserId, Token, NewPassword);

            if (success)
            {
                isSuccess = true;
            }
            else
            {
                errorMessage = error;
            }
        }
        finally
        {
            isSubmitting = false;
            StateHasChanged();
        }
    }
}
