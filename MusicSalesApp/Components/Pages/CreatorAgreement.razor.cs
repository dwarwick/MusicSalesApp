using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class CreatorAgreementModel : BlazorBase
{
    protected int _streamQualifyingSeconds = 30;
    protected decimal _streamPayRateDisplay = 5.00m;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _streamQualifyingSeconds = await AppSettingsService.GetStreamQualifyingSecondsAsync();
            var streamPayRate = await AppSettingsService.GetStreamPayRateAsync();
            _streamPayRateDisplay = streamPayRate * 1000;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading CreatorAgreement data");
        }
    }
}
