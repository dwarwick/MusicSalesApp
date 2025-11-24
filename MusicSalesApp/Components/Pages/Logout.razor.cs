using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class LogoutModel : BlazorBase
{
    [Inject]
    private IAntiforgery Antiforgery { get; set; }

    [Inject]
    private IHttpContextAccessor HttpContextAccessor { get; set; }

    protected string antiForgeryToken = string.Empty;

    protected override void OnInitialized()
    {
        // Get antiforgery token
        var httpContext = HttpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            var tokens = Antiforgery.GetAndStoreTokens(httpContext);
            antiForgeryToken = tokens.RequestToken;
        }
    }
}
