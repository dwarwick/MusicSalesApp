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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

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
    protected ISongMetadataService SongMetadataService { get; set; } = default!;

    [Inject]
    protected IThemeService ThemeService { get; set; } = default!;

    [Inject]
    protected IPlaylistService PlaylistService { get; set; } = default!;

    [Inject]
    protected ISubscriptionService SubscriptionService { get; set; } = default!;

    [Inject]
    protected IPasskeyService PasskeyService { get; set; } = default!;

    [Inject]
    protected ISongLikeService SongLikeService { get; set; } = default!;

    [Inject]
    protected IOpenGraphService OpenGraphService { get; set; } = default!;

    [Inject]
    protected IStreamCountService StreamCountService { get; set; } = default!;

    [Inject]
    protected IStreamCountHubClient StreamCountHubClient { get; set; } = default!;

    [Inject]
    protected ILikeCountHubClient LikeCountHubClient { get; set; } = default!;

    [Inject]
    protected IWebhookStatusHubClient WebhookStatusHubClient { get; set; } = default!;

    [Inject]
    protected IMaintenanceHubClient MaintenanceHubClient { get; set; } = default!;

    [Inject]
    protected IRecommendationService RecommendationService { get; set; } = default!;

    [Inject]
    protected IAppSettingsService AppSettingsService { get; set; } = default!;

    [Inject]
    protected IAccountEmailService AccountEmailService { get; set; } = default!;

    [Inject]
    protected ICreatorService CreatorService { get; set; } = default!;

    [Inject]
    protected ICreatorPersonaService CreatorPersonaService { get; set; } = default!;

    [Inject]
    protected IDashboardService DashboardService { get; set; } = default!;

    [Inject]
    protected IGenreService GenreService { get; set; } = default!;

    [Inject]
    protected IEmailService EmailService { get; set; } = default!;

    [Inject]
    protected IAzureStorageService AzureStorageService { get; set; } = default!;

    [Inject]
    protected IAdminNotificationService AdminNotificationService { get; set; } = default!;

    [Inject]
    protected IAdminMessageService AdminMessageService { get; set; } = default!;

    [Inject]
    protected IAdminMessageHubClient AdminMessageHubClient { get; set; } = default!;

    [Inject]
    protected IFileMatchingService FileMatchingService { get; set; } = default!;

    [Inject]
    protected IStreamPayoutService StreamPayoutService { get; set; } = default!;

    [Inject]
    protected ITipService TipService { get; set; } = default!;

    [Inject]
    protected IReportedSongService ReportedSongService { get; set; } = default!;

    // Ensure components can access the same scoped UserManager used by DI
    [Inject]
    protected UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    protected ILoggerFactory LoggerFactory { get; set; } = default!;

    [Inject]
    protected IConfiguration Configuration { get; set; } = default!;

    [Inject]
    protected IOptions<MobileAppInstallOptions> MobileAppInstallOptions { get; set; } = default!;

    [Inject]
    protected TimeProvider TimeProvider { get; set; } = default!;

    private ILogger _logger;
    protected ILogger Logger => _logger ??= LoggerFactory.CreateLogger(GetType());

    /// <summary>
    /// Gets the current user's integer ID from claims without making a database call.
    /// Returns null if the user is not authenticated or the ID cannot be parsed.
    /// </summary>
    protected int? GetUserId(System.Security.Claims.ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
            return null;
        var userIdStr = UserManager.GetUserId(user);
        if (!string.IsNullOrEmpty(userIdStr) && int.TryParse(userIdStr, out var userId))
            return userId;
        return null;
    }
}
