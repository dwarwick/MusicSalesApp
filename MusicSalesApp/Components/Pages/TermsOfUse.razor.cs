using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class TermsOfUseModel : BlazorBase
{
    protected int _streamQualifyingSeconds = 30;
    protected decimal _streamPayRatePerStream = 0.005m;
    protected decimal _streamPayRateDisplay = 5.00m;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _streamQualifyingSeconds = await AppSettingsService.GetStreamQualifyingSecondsAsync();
            _streamPayRatePerStream = await AppSettingsService.GetStreamPayRateAsync();
            _streamPayRateDisplay = _streamPayRatePerStream * 1000;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading TermsOfUse data");
        }
    }
}
