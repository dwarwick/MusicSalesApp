using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Forms;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class RegisterModel : BlazorBase
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
    [Required]
    public string Password { get; set; } = string.Empty;
    [Required]
    public string ConfirmPassword { get; set; } = string.Empty;

    protected string errorMessage = string.Empty;
    protected string successMessage = string.Empty;
    protected bool isSubmitting = false;

    protected override async Task OnInitializedAsync()
    {
        var auth = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (auth.User.Identity != null && auth.User.Identity.IsAuthenticated)
        {
            NavigationManager.NavigateTo("/", forceLoad: true);
            return;
        }
    }

    protected async Task HandleSubmit(EditContext context)
    {
        errorMessage = string.Empty;
        successMessage = string.Empty;

        if (isSubmitting) return;
        isSubmitting = true;
        try
        {
            if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password) || string.IsNullOrWhiteSpace(ConfirmPassword))
            {
                errorMessage = "All fields are required";
                return;
            }
            if (!Regex.IsMatch(Email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                errorMessage = "Invalid email format";
                return;
            }
            if (Password != ConfirmPassword)
            {
                errorMessage = "Passwords do not match";
                return;
            }
            // Call registration service
            var (success, err) = await AuthenticationService.RegisterAsync(Email, Password);
            if (!success)
            {
                errorMessage = err;
                return;
            }
            successMessage = "Registration successful. You are now logged in.";
            await Task.Delay(1000);
            NavigationManager.NavigateTo("/login", forceLoad: true);
        }
        finally
        {
            isSubmitting = false;
            StateHasChanged();
        }
    }
}
