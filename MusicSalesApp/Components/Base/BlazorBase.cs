using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MusicSalesApp.Services;

namespace MusicSalesApp.Components.Base;

public abstract class BlazorBase : ComponentBase
{
    [Inject]
    protected NavigationManager NavigationManager { get; set; }

    [Inject]
    protected IAuthenticationService AuthenticationService { get; set; }

    [Inject]
    protected AuthenticationStateProvider AuthenticationStateProvider { get; set; }
}
