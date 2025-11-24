using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class CounterModel : BlazorBase
{
    protected int currentCount = 0;

    protected void IncrementCount()
    {
        currentCount++;
    }
}
