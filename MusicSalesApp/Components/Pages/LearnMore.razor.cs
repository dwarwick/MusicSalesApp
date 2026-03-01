using Microsoft.JSInterop;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class LearnMoreModel : BlazorBase
{
    protected string _streamPayRateDisplay = "0.005";
    protected int _streamQualifyingSeconds = 30;
    private bool _hasLoadedData = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JS.InvokeVoidAsync("eval", "document.querySelector('main').scrollTop = 0");

            if (!_hasLoadedData)
            {
                _hasLoadedData = true;
                try
                {
                    var streamPayRate = await AppSettingsService.GetStreamPayRateAsync();
                    _streamPayRateDisplay = streamPayRate.ToString("0.###");
                    _streamQualifyingSeconds = await AppSettingsService.GetStreamQualifyingSecondsAsync();
                }
                finally
                {
                    await InvokeAsync(StateHasChanged);
                }
            }
        }
    }
}
