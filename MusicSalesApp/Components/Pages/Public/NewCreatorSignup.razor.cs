using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages.Public;

public partial class NewCreatorSignUpModel : BlazorBase
{
    protected bool _isAuthenticated = false;
    protected bool _isEmailVerified = false;
    protected bool _isActiveCreator = false;
    protected string CreatorLoginUrl => BuildReturnUrl(AppPageRoutes.Login, AppPageRoutes.CreatorSettings);
    protected string CreatorRegisterUrl => BuildReturnUrl(AppPageRoutes.Register, AppPageRoutes.CreatorSettings);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading LearnMore page data");
        }
    }

    private async Task LoadDataAsync()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (authState.User?.Identity?.IsAuthenticated == true)
        {
            _isAuthenticated = true;
            var appUser = await UserManager.GetUserAsync(authState.User);
            if (appUser != null)
            {
                _isEmailVerified = !await UserManager.IsInRoleAsync(appUser, Roles.NonValidatedUser);
                _isActiveCreator = await CreatorService.IsActiveCreatorAsync(appUser.Id);
            }
        }
    }

    private static string BuildReturnUrl(string route, string returnUrl)
        => $"{route}?{ExternalAuthFormFields.ReturnUrl}={Uri.EscapeDataString(returnUrl)}";
}
