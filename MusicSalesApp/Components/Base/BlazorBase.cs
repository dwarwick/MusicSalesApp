using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MusicSalesApp.Services;
using System.Net.Http; // HttpClient
using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Models;

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
    protected IJSRuntime JS { get; set; } = default!;

    [Inject]
    protected IWebHostEnvironment Environment { get; set; } = default!;

    [Inject]
    protected ICartService CartService { get; set; } = default!;

    [Inject]
    protected ISongMetadataService SongMetadataService { get; set; } = default!;

    [Inject]
    protected IThemeService ThemeService { get; set; } = default!;

    [Inject]
    protected IPlaylistService PlaylistService { get; set; } = default!;

    // Ensure components can access the same scoped UserManager used by DI
    [Inject]
    protected UserManager<ApplicationUser> UserManager { get; set; } = default!;
}
