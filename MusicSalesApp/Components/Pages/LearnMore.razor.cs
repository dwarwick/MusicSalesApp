using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class LearnMoreModel : BlazorBase
{
    protected string _streamPayRateDisplay = "0.005";
    protected int _streamQualifyingSeconds = 30;
    protected bool _isAuthenticated = false;
    protected bool _isActiveCreator = false;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var streamPayRate = await AppSettingsService.GetStreamPayRateAsync();
            _streamPayRateDisplay = streamPayRate.ToString("0.###");
            _streamQualifyingSeconds = await AppSettingsService.GetStreamQualifyingSecondsAsync();

            var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            if (authState.User?.Identity?.IsAuthenticated == true)
            {
                _isAuthenticated = true;
                var appUser = await UserManager.GetUserAsync(authState.User);
                if (appUser != null)
                {
                    _isActiveCreator = await CreatorService.IsActiveCreatorAsync(appUser.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading LearnMore page data");
        }
    }
}
