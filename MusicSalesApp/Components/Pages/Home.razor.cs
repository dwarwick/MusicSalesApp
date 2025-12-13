using Microsoft.Extensions.Configuration;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Models;

namespace MusicSalesApp.Components.Pages;

public partial class HomeModel : BlazorBase
{
    [Microsoft.AspNetCore.Components.Inject]
    protected IConfiguration Configuration { get; set; }

    protected string _subscriptionPrice;
    protected bool _hasActiveSubscription = false;

    protected override void OnInitialized()
    {
        _subscriptionPrice = Configuration["PayPal:SubscriptionPrice"] ?? "3.99";
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Check subscription status
            var subscriptionResponse = await Http.GetFromJsonAsync<SubscriptionStatusDto>("api/subscription/status");
            _hasActiveSubscription = subscriptionResponse?.HasSubscription ?? false;
        }
    }
}
