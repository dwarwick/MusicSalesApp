using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MusicSalesApp.Services;
using System.Net.Http; // HttpClient

namespace MusicSalesApp.Components.Base;

public abstract class BlazorBase : ComponentBase
{
    [Inject]
    protected NavigationManager NavigationManager { get; set; } // shared NavigationManager

    [Inject]
    protected HttpClient Http { get; set; } // shared HttpClient (BaseAddress configured in Program.cs)

    [Inject]
    protected IAuthenticationService AuthenticationService { get; set; }

    [Inject]
    protected AuthenticationStateProvider AuthenticationStateProvider { get; set; }

    [Inject] 
    protected IAntiforgery Antiforgery { get; set; } = default!;
    [Inject] 
    protected IHttpContextAccessor HttpContextAccessor { get; set; } = default!;

    [Inject]
    protected IMusicUploadService MusicUploadService { get; set; } = default!;

    [Inject]
    protected IServiceScopeFactory ScopeFactory { get; set; } = default!;

    [Inject]
    protected IJSRuntime JS { get; set; } = default!;
}
