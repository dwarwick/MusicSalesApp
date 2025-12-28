using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Components.Forms;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class ForgotPasswordModel : BlazorBase
{
    [Required(ErrorMessage = "Email address is required")]
    [EmailAddress(ErrorMessage = "Please enter a valid email address")]
    public string Email { get; set; } = string.Empty;

    protected string errorMessage = string.Empty;
    protected bool isSubmitting = false;
    protected bool isSubmitted = false;

    protected override async Task OnInitializedAsync()
    {
        // Check if already logged in
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (authState.User?.Identity?.IsAuthenticated == true)
        {
            NavigationManager.NavigateTo("/", forceLoad: true);
        }
    }

    protected async Task HandleSubmit(EditContext context)
    {
        if (isSubmitting) return;

        errorMessage = string.Empty;
        isSubmitting = true;

        try
        {
            if (string.IsNullOrWhiteSpace(Email))
            {
                errorMessage = "Please enter your email address.";
                return;
            }

            if (!new EmailAddressAttribute().IsValid(Email))
            {
                errorMessage = "Please enter a valid email address.";
                return;
            }

            var baseUrl = NavigationManager.BaseUri;
            var (success, error) = await AuthenticationService.SendPasswordResetEmailAsync(Email, baseUrl);

            // Always show success message to not reveal if account exists
            isSubmitted = true;
        }
        finally
        {
            isSubmitting = false;
            StateHasChanged();
        }
    }
}
