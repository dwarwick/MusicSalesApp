using Microsoft.Extensions.Configuration;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class HomeModel : BlazorBase
{
    [Microsoft.AspNetCore.Components.Inject]
    protected IConfiguration Configuration { get; set; }

    protected string _subscriptionPrice;

    protected override void OnInitialized()
    {
        _subscriptionPrice = Configuration["PayPal:SubscriptionPrice"] ?? "3.99";
    }
}
