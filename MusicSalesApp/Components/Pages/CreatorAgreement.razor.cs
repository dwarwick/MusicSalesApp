using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class CreatorAgreementModel : BlazorBase
{
    protected int _streamQualifyingSeconds = 30;
    protected decimal _streamPayRateDisplay = 5.00m;
    private bool _hasLoadedData = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                _streamQualifyingSeconds = await AppSettingsService.GetStreamQualifyingSecondsAsync();
                var streamPayRate = await AppSettingsService.GetStreamPayRateAsync();
                _streamPayRateDisplay = streamPayRate * 1000;
            }
            finally
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }
}
