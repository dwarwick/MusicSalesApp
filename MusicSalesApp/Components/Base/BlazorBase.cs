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
using System.Runtime.CompilerServices;

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
    protected IMusicService MusicService { get; set; } = default!;

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

    /// <summary>
    /// Call after any page commits new cover art or a new persona image, so the pre-resized WebP
    /// renditions are rebuilt and the recorded width set stays in step with what is in storage.
    /// </summary>
    [Inject]
    protected IImageVariantCoordinator ImageVariantCoordinator { get; set; } = default!;

    /// <summary>
    /// Builds the URL set for a piece of cover art. Use this instead of hand-rolling an
    /// <c>api/music/...</c> string, so pages pick up the pre-resized renditions automatically.
    /// </summary>
    [Inject]
    protected ICoverArtUrlBuilder CoverArtUrlBuilder { get; set; } = default!;

    /// <summary>
    /// Builds the proxied URL for a persona avatar. Use this on public pages rather than
    /// <c>CreatorPersonaService.GetPersonaImageSasUrl</c>: a SAS is minted per call and its query
    /// string changes every render, so the browser can never reuse a cached copy.
    /// </summary>
    [Inject]
    protected IPersonaImageUrlBuilder PersonaImageUrlBuilder { get; set; } = default!;

    [Inject]
    protected IImageVariantBackfillService ImageVariantBackfillService { get; set; } = default!;

    [Inject]
    protected IHlsPackagingBackfillService HlsPackagingBackfillService { get; set; } = default!;

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
    protected IUploadProgressHubClient UploadProgressHubClient { get; set; } = default!;

    [Inject]
    protected ISongUploadJobService SongUploadJobService { get; set; } = default!;

    [Inject]
    protected ISongLyricsService LyricsService { get; set; } = default!;

    [Inject]
    protected IRecommendationService RecommendationService { get; set; } = default!;

    [Inject]
    protected IAppSettingsService AppSettingsService { get; set; } = default!;

    [Inject]
    protected IPayPalSubscriptionApiService PayPalSubscriptionApiService { get; set; } = default!;

    [Inject]
    protected IPayPalSubscriptionManagementService PayPalSubscriptionManagementService { get; set; } = default!;

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

    /// <summary>
    /// Builds the encrypted-HLS manifest URL a player should be pointed at.
    ///
    /// <para>
    /// Injected here rather than reached over HTTP, per the rule that Blazor Server calls services
    /// directly: the players used to fetch <c>api/music/url/{path}</c> to get a SAS, which was a
    /// round trip from the server to itself.
    /// </para>
    /// </summary>
    [Inject]
    protected IHlsStreamUrlFactory HlsStreamUrls { get; set; } = default!;

    [Inject]
    protected IAdminNotificationService AdminNotificationService { get; set; } = default!;

    [Inject]
    protected IAdminMessageService AdminMessageService { get; set; } = default!;

    [Inject]
    protected IAdminMessageHubClient AdminMessageHubClient { get; set; } = default!;

    [Inject]
    protected IFileMatchingService FileMatchingService { get; set; } = default!;

    [Inject]
    protected ICoverArtMatchService CoverArtMatchService { get; set; } = default!;

    [Inject]
    protected IUploadStagingSasService UploadStagingSasService { get; set; } = default!;

    [Inject]
    protected IStreamPayoutService StreamPayoutService { get; set; } = default!;

    [Inject]
    protected ITipService TipService { get; set; } = default!;

    [Inject]
    protected IReportedSongService ReportedSongService { get; set; } = default!;

    [Inject]
    protected IContactRequestAdminService ContactRequestAdminService { get; set; } = default!;

    [Inject]
    protected IStorageBackupService StorageBackupService { get; set; } = default!;

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
    /// Runs UI work on the renderer's dispatcher from a callback that is not already on it - a
    /// SignalR push, a timer tick, a C# event raised by a background service - and then repaints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The work goes inside, not just the repaint.</b> Blazor Server owns component state on one
    /// dispatcher. <c>StateHasChanged</c> called from anywhere else throws "The current thread is not
    /// associated with the Dispatcher" - but the quieter half of the problem is the lines before it:
    /// fields mutated on a hub thread race the renderer reading them, and for a Dictionary or the
    /// circuit's scoped DbContext that is a corruption bug, not a repaint bug.
    /// </para>
    /// <para>
    /// <b>Fire and forget by design</b>, because every caller is a <c>void</c> event handler with
    /// nobody to await it. That is exactly why the body is wrapped: an exception inside a discarded
    /// Task is unobserved, and these handlers are the paths nobody is watching.
    /// </para>
    /// </remarks>
    /// <param name="work">The state change to make. Runs on the dispatcher.</param>
    /// <param name="origin">Supplied by the compiler; names the handler in the log if it throws.</param>
    protected void DispatchUiUpdate(Func<Task> work, [CallerMemberName] string origin = "")
    {
        try
        {
            _ = InvokeAsync(async () =>
            {
                try
                {
                    await work();
                    StateHasChanged();
                }
                catch (ObjectDisposedException)
                {
                    // The component went away while this update was queued behind it. Expected.
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Background UI update from {Origin} failed.", origin);
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // The renderer had already gone when we tried to queue. Also expected: unsubscribing in
            // Dispose narrows this race, it cannot remove it.
        }
    }

    /// <summary>
    /// Runs UI work on the renderer's dispatcher and repaints. See
    /// <see cref="DispatchUiUpdate(Func{Task}, string)"/> for why this is not optional.
    /// </summary>
    protected void DispatchUiUpdate(Action work, [CallerMemberName] string origin = "")
        => DispatchUiUpdate(
            () =>
            {
                work();
                return Task.CompletedTask;
            },
            origin);

    /// <summary>
    /// Repaints from a background callback that has no state of its own to change - the no-work case
    /// of <see cref="DispatchUiUpdate(Action, string)"/>.
    /// </summary>
    protected void DispatchUiRefresh([CallerMemberName] string origin = "")
        => DispatchUiUpdate(() => { }, origin);

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
