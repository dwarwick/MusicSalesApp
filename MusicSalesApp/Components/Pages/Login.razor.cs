using System.ComponentModel.DataAnnotations;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class LoginModel : BlazorBase
{
    protected LoginFormModel loginModel = new();
    protected string errorMessage = string.Empty;
    protected bool isLoggingIn = false;

    protected override async Task OnInitializedAsync()
    {
        // Check if already logged in
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (authState.User?.Identity?.IsAuthenticated == true)
        {
            NavigationManager.NavigateTo("/");
        }
    }

    protected async Task HandleLogin()
    {
        errorMessage = string.Empty;
        isLoggingIn = true;

        try
        {
            var success = await AuthenticationService.LoginAsync(loginModel.Username, loginModel.Password);
            
            if (success)
            {
                NavigationManager.NavigateTo("/", forceLoad: true);
            }
            else
            {
                errorMessage = "Invalid username or password.";
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"An error occurred: {ex.Message}";
        }
        finally
        {
            isLoggingIn = false;
        }
    }

    protected class LoginFormModel
    {
        [Required]
        public string Username { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }
}
