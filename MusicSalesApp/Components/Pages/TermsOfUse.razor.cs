using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class TermsOfUseModel : BlazorBase
{
    protected int _streamQualifyingSeconds = 30;
    private bool _hasLoadedData = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                _streamQualifyingSeconds = await AppSettingsService.GetStreamQualifyingSecondsAsync();
            }
            finally
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }
}
