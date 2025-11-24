using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class ErrorModel : BlazorBase
{
    [CascadingParameter]
    private HttpContext HttpContext { get; set; }

    protected string RequestId { get; set; }
    protected bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    protected override void OnInitialized()
    {
        RequestId = Activity.Current?.Id ?? HttpContext?.TraceIdentifier;
    }
}
