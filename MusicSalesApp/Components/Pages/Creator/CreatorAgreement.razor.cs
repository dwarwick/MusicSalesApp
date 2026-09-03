using MusicSalesApp.Components.Base;
using MusicSalesApp.Helpers;

namespace MusicSalesApp.Components.Pages.Creator;

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
        catch (Exception ex) when (CircuitTeardown.IsExpected(ex))
        {
            // The visitor left, or the circuit dropped, while this was still awaiting.
            // Nothing is wrong and there is nobody to tell, so it must not reach the
            // Error sink - that is what emailed the admin five times on 2026-09-02.
            Logger.LogDebug(ex, "Error loading CreatorAgreement data");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading CreatorAgreement data");
        }
    }
}
