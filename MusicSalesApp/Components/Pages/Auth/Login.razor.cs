using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages.Auth;

public partial class LoginModel : BlazorBase
{   
    [SupplyParameterFromQuery(Name = ExternalAuthFormFields.Error)]
    public string Error { get; set; }

    [SupplyParameterFromQuery(Name = ExternalAuthFormFields.ReturnUrl)]
    public string ReturnUrl { get; set; } = AppPageRoutes.Home;

    protected string errorMessage = string.Empty;
    protected string antiForgeryToken = string.Empty;
    protected bool isDevelopment = false;
    protected string usernameValue = string.Empty;
    protected bool reactivateAccount = false;
    protected bool showReactivateCheckbox = false;
    protected string LoginReturnUrl => NormalizeLocalReturnUrl(ReturnUrl);

    protected override async Task OnInitializedAsync()
    {
        // HttpContext is only available during SSR/prerender, not during interactive rendering.
        var httpContext = HttpContextAccessor.HttpContext;

        // Get antiforgery token (SSR only)
        if (httpContext != null)
        {
            var tokens = Antiforgery.GetAndStoreTokens(httpContext);
            antiForgeryToken = tokens.RequestToken;
        }

        // Check if in development environment
        isDevelopment = Environment.IsDevelopment();

        // Display error message if present
        if (!string.IsNullOrEmpty(Error))
        {
            errorMessage = Error;
            // Show reactivate checkbox only if the error message indicates account suspension
            showReactivateCheckbox = Error.Contains("suspended", StringComparison.OrdinalIgnoreCase) || 
                                     Error.Contains("reactivate", StringComparison.OrdinalIgnoreCase);
        }
    }

    protected async Task LoginWithPasskey()
    {
        if (string.IsNullOrWhiteSpace(usernameValue))
        {
            errorMessage = "Please enter your username or email first.";
            return;
        }

        try
        {
            // Call JavaScript to initiate passkey login with extended timeout (2 minutes)
            // Google Password Manager may take longer than Windows Hello
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await JS.InvokeVoidAsync("passkeyHelper.loginWithPasskey", cts.Token, usernameValue);
        }
        catch (TaskCanceledException)
        {
            Logger.LogWarning("Passkey login timed out after 2 minutes");
            errorMessage = "Passkey login timed out. Please try again and complete the process more quickly.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error initiating passkey login");
            errorMessage = "Failed to login with passkey.";
        }
    }

    protected void ContinueWithGoogle()
    {
        var googleStartUrl = $"{GoogleAuthRoutes.WebStartPath}?{ExternalAuthFormFields.ReturnUrl}={Uri.EscapeDataString(LoginReturnUrl)}";
        NavigationManager.NavigateTo(googleStartUrl, forceLoad: true);
    }

    private static string NormalizeLocalReturnUrl(string returnUrl)
        => string.IsNullOrWhiteSpace(returnUrl) || !IsLocalUrl(returnUrl)
            ? AppPageRoutes.Home
            : returnUrl;

    private static bool IsLocalUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        if (url[0] == '/')
        {
            return url.Length == 1 || (url[1] != '/' && url[1] != '\\');
        }

        if (url[0] == '~' && url.Length > 1 && url[1] == '/')
        {
            return url.Length == 2 || (url[2] != '/' && url[2] != '\\');
        }

        return false;
    }
}
