using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using MusicSalesApp.Common;
using Syncfusion.Blazor;

namespace MusicSalesApp.ComponentTests.Testing;

public abstract class BUnitTestBase
{
    protected BunitContext TestContext { get; private set; } = default!;
    protected BunitAuthorizationContext AuthorizationContext { get; private set; } = default!;

    protected Mock<IAuthenticationService> MockAuthService { get; private set; } = default!;
    protected Mock<AuthenticationStateProvider> MockAuthStateProvider { get; private set; } = default!;
    protected Mock<IAntiforgery> MockAntiforgery { get; private set; } = default!;
    protected Mock<IHttpContextAccessor> MockHttpContextAccessor { get; private set; } = default!;
    protected Mock<IMusicUploadService> MockMusicUploadService { get; private set; } = default!;
    protected Mock<IMusicService> MockMusicService { get; private set; } = default!;
    protected Mock<IAzureStorageService> MockAzureStorageService { get; private set; } = default!;
    protected Mock<IWebHostEnvironment> MockWebHostEnvironment { get; private set; } = default!;
    protected Mock<ISongMetadataService> MockSongMetadataService { get; private set; } = default!;
    protected Mock<IThemeService> MockThemeService { get; private set; } = default!;
    protected Mock<IPlaylistService> MockPlaylistService { get; private set; } = default!;
    protected Mock<ISubscriptionService> MockSubscriptionService { get; private set; } = default!;
    protected Mock<IAppSettingsService> MockAppSettingsService { get; private set; } = default!;
    protected Mock<IPayPalSubscriptionApiService> MockPayPalSubscriptionApiService { get; private set; } = default!;
    protected Mock<IPayPalSubscriptionManagementService> MockPayPalSubscriptionManagementService { get; private set; } = default!;
    protected Mock<UserManager<ApplicationUser>> MockUserManager { get; private set; } = default!;
    protected Mock<IPasskeyService> MockPasskeyService { get; private set; } = default!;
    protected Mock<IOpenGraphService> MockOpenGraphService { get; private set; } = default!;
    protected Mock<ISongLikeService> MockSongLikeService { get; private set; } = default!;
    protected Mock<IStreamCountService> MockStreamCountService { get; private set; } = default!;
    protected Mock<IStreamCountHubClient> MockStreamCountHubClient { get; private set; } = default!;
    protected Mock<ILikeCountHubClient> MockLikeCountHubClient { get; private set; } = default!;
    protected Mock<IWebhookStatusHubClient> MockWebhookStatusHubClient { get; private set; } = default!;
    protected Mock<IMaintenanceHubClient> MockMaintenanceHubClient { get; private set; } = default!;
    protected Mock<IRecommendationService> MockRecommendationService { get; private set; } = default!;
    protected Mock<IPurchaseEmailService> MockPurchaseEmailService { get; private set; } = default!;
    protected Mock<IAccountEmailService> MockAccountEmailService { get; private set; } = default!;
    protected Mock<IAccountDeletionService> MockAccountDeletionService { get; private set; } = default!;
    protected Mock<IEmailService> MockEmailService { get; private set; } = default!;
    protected Mock<ICreatorService> MockCreatorService { get; private set; } = default!;
    protected Mock<ICreatorPersonaService> MockCreatorPersonaService { get; private set; } = default!;
    protected Mock<IDashboardService> MockDashboardService { get; private set; } = default!;
    protected Mock<ISongStatusService> MockSongStatusService { get; private set; } = default!;
    protected Mock<IGenreService> MockGenreService { get; private set; } = default!;
    protected Mock<IFileMatchingService> MockFileMatchingService { get; private set; } = default!;
    protected Mock<IStreamPayoutService> MockStreamPayoutService { get; private set; } = default!;
    protected Mock<ITipService> MockTipService { get; private set; } = default!;
    protected Mock<IReportedSongService> MockReportedSongService { get; private set; } = default!;
    protected Mock<IAdminNotificationService> MockAdminNotificationService { get; private set; } = default!;
    protected Mock<IAdminMessageService> MockAdminMessageService { get; private set; } = default!;
    protected Mock<IAdminMessageHubClient> MockAdminMessageHubClient { get; private set; } = default!;
    protected Mock<IContactRequestAdminService> MockContactRequestAdminService { get; private set; } = default!;
    protected Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<MusicSalesApp.Data.AppDbContext>> MockDbContextFactory { get; private set; } = default!;
    protected FakeTimeProvider FakeTimeProvider { get; private set; } = default!;

    [SetUp]
    public virtual void BaseSetup()
    {
        TestContext = new BunitContext();

        MockAuthService = new Mock<IAuthenticationService>();
        MockAuthStateProvider = new Mock<AuthenticationStateProvider>();
        MockAntiforgery = new Mock<IAntiforgery>();
        MockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        MockMusicUploadService = new Mock<IMusicUploadService>();
        MockMusicService = new Mock<IMusicService>();
        MockAzureStorageService = new Mock<IAzureStorageService>();
        MockWebHostEnvironment = new Mock<IWebHostEnvironment>();
        MockSongMetadataService = new Mock<ISongMetadataService>();
        MockThemeService = new Mock<IThemeService>();
        MockPlaylistService = new Mock<IPlaylistService>();
        MockSubscriptionService = new Mock<ISubscriptionService>();
        MockAppSettingsService = new Mock<IAppSettingsService>();
        MockPayPalSubscriptionApiService = new Mock<IPayPalSubscriptionApiService>();
        MockPayPalSubscriptionManagementService = new Mock<IPayPalSubscriptionManagementService>();
        MockPasskeyService = new Mock<IPasskeyService>();
        MockOpenGraphService = new Mock<IOpenGraphService>();
        MockSongLikeService = new Mock<ISongLikeService>();
        MockStreamCountService = new Mock<IStreamCountService>();
        MockStreamCountHubClient = new Mock<IStreamCountHubClient>();
        MockLikeCountHubClient = new Mock<ILikeCountHubClient>();
        MockWebhookStatusHubClient = new Mock<IWebhookStatusHubClient>();
        MockMaintenanceHubClient = new Mock<IMaintenanceHubClient>();
        MockRecommendationService = new Mock<IRecommendationService>();
        MockPurchaseEmailService = new Mock<IPurchaseEmailService>();
        MockAccountEmailService = new Mock<IAccountEmailService>();
        MockAccountDeletionService = new Mock<IAccountDeletionService>();
        MockEmailService = new Mock<IEmailService>();
        MockCreatorService = new Mock<ICreatorService>();
        MockCreatorPersonaService = new Mock<ICreatorPersonaService>();
        MockDashboardService = new Mock<IDashboardService>();
        MockSongStatusService = new Mock<ISongStatusService>();
        MockGenreService = new Mock<IGenreService>();
        MockFileMatchingService = new Mock<IFileMatchingService>();
        MockStreamPayoutService = new Mock<IStreamPayoutService>();
        MockTipService = new Mock<ITipService>();
        MockReportedSongService = new Mock<IReportedSongService>();
        MockAdminNotificationService = new Mock<IAdminNotificationService>();
        MockAdminMessageService = new Mock<IAdminMessageService>();
        MockAdminMessageHubClient = new Mock<IAdminMessageHubClient>();
        MockContactRequestAdminService = new Mock<IContactRequestAdminService>();
        MockDbContextFactory = new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<MusicSalesApp.Data.AppDbContext>>();
        FakeTimeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        
        // UserManager requires IUserStore in its constructor
        var mockUserStore = new Mock<IUserStore<ApplicationUser>>();
        MockUserManager = new Mock<UserManager<ApplicationUser>>(
            mockUserStore.Object,
            null!, // IOptions<IdentityOptions>
            null!, // IPasswordHasher<ApplicationUser>
            null!, // IEnumerable<IUserValidator<ApplicationUser>>
            null!, // IEnumerable<IPasswordValidator<ApplicationUser>>
            null!, // ILookupNormalizer
            null!, // IdentityErrorDescriber
            null!, // IServiceProvider
            null!  // ILogger<UserManager<ApplicationUser>>
        );

        // GetUserId reads from claims (no DB call) — set up to extract NameIdentifier claim
        MockUserManager
            .Setup(x => x.GetUserId(It.IsAny<ClaimsPrincipal>()))
            .Returns((ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier));

        // Configure WebHostEnvironment mock
        MockWebHostEnvironment.Setup(x => x.EnvironmentName).Returns("Development");
        MockWebHostEnvironment.Setup(x => x.ApplicationName).Returns("MusicSalesApp");
        MockWebHostEnvironment.Setup(x => x.ContentRootPath).Returns(Directory.GetCurrentDirectory());
        MockWebHostEnvironment.Setup(x => x.WebRootPath).Returns(Directory.GetCurrentDirectory());

        // Configure AuthenticationStateProvider mock to return unauthenticated user
        var anonymousUser = new ClaimsPrincipal(new ClaimsIdentity());
        var authState = new AuthenticationState(anonymousUser);
        MockAuthStateProvider
            .Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        // Setup default returns for IAuthenticationService methods
        MockAuthService.Setup(x => x.RegisterAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((true, string.Empty));
        MockAuthService.Setup(x => x.RegisterAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((true, string.Empty));
        MockAuthService.Setup(x => x.SendVerificationEmailAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((true, string.Empty));
        MockAuthService.Setup(x => x.CanResendVerificationEmailAsync(It.IsAny<string>()))
            .ReturnsAsync((true, 0));
        MockAuthService.Setup(x => x.IsEmailVerifiedAsync(It.IsAny<string>()))
            .ReturnsAsync(false);
        MockAuthService.Setup(x => x.SendPasswordResetEmailAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((true, string.Empty));
        MockAuthService.Setup(x => x.VerifyPasswordResetTokenAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((true, string.Empty));
        MockAuthService.Setup(x => x.ResetPasswordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((true, string.Empty));

        // Setup default returns for ISongMetadataService methods
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());
        MockSongMetadataService.Setup(x => x.FindExistingSongTitlesAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<string>());
        MockSongMetadataService.Setup(x => x.GetByCreatorIdAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Setup default returns for IThemeService methods
        MockThemeService.Setup(x => x.CurrentTheme).Returns("Light");
        MockThemeService.Setup(x => x.IsDarkTheme).Returns(false);
        MockThemeService.Setup(x => x.SyncfusionCssUrl).Returns("https://cdn.syncfusion.com/blazor/33.1.44/styles/bootstrap5.3.css");
        MockThemeService.Setup(x => x.CustomCssFile).Returns("light.css");
        MockThemeService.Setup(x => x.SetThemeAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        MockThemeService.Setup(x => x.InitializeThemeAsync())
            .Returns(Task.CompletedTask);

        // Setup default returns for IPlaylistService methods
        MockPlaylistService.Setup(x => x.GetUserPlaylistsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<Playlist>());
        MockPlaylistService.Setup(x => x.GetPlaylistSongsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<UserPlaylist>());

        // Setup default returns for ISubscriptionService methods
        MockSubscriptionService.Setup(x => x.HasActiveSubscriptionAsync(It.IsAny<int>()))
            .ReturnsAsync(false);
        MockSubscriptionService.Setup(x => x.GetActiveSubscriptionAsync(It.IsAny<int>()))
            .ReturnsAsync((Subscription)null);

        // Setup default returns for IAppSettingsService methods
        MockAppSettingsService.Setup(x => x.GetSubscriptionPriceAsync())
            .ReturnsAsync(3.99m);
        MockAppSettingsService.Setup(x => x.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((string)null);
        MockAppSettingsService.Setup(x => x.SetSubscriptionPriceAsync(It.IsAny<decimal>()))
            .Returns(Task.CompletedTask);
        MockAppSettingsService.Setup(x => x.SetSettingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        MockAppSettingsService.Setup(x => x.GetStreamPayRateAsync())
            .ReturnsAsync(0.005m);
        MockAppSettingsService.Setup(x => x.SetStreamPayRateAsync(It.IsAny<decimal>()))
            .Returns(Task.CompletedTask);
        MockAppSettingsService.Setup(x => x.GetStreamQualifyingSecondsAsync())
            .ReturnsAsync(30);
        MockAppSettingsService.Setup(x => x.SetStreamQualifyingSecondsAsync(It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        MockAppSettingsService.Setup(x => x.GetMaxImageUploadSizeMBAsync())
            .ReturnsAsync(10);
        MockAppSettingsService.Setup(x => x.GetAppVersionAsync())
            .ReturnsAsync(default(string));
        MockAppSettingsService.Setup(x => x.SetAppVersionAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        MockAppSettingsService.Setup(x => x.GetPayPalWebSubscriptionOfferAsync())
            .ReturnsAsync((PayPalWebSubscriptionOffer)null);
        MockAppSettingsService.Setup(x => x.SetPayPalWebSubscriptionOfferAsync(It.IsAny<PayPalWebSubscriptionOffer>()))
            .ReturnsAsync((PayPalWebSubscriptionOffer offer) => offer);

        MockPayPalSubscriptionApiService
            .Setup(x => x.GetActivePlansAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PayPalPlan>());
        MockPayPalSubscriptionApiService
            .Setup(x => x.GetPlanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PayPalPlan)null);

        // Setup default returns for IPasskeyService methods
        MockPasskeyService.Setup(x => x.GetUserPasskeysAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<Passkey>());

        // Setup default returns for IOpenGraphService methods
        MockOpenGraphService.Setup(x => x.GenerateSongMetaTagsAsync(It.IsAny<string>()))
            .ReturnsAsync(string.Empty);
        MockOpenGraphService.Setup(x => x.GenerateAlbumMetaTagsAsync(It.IsAny<string>()))
            .ReturnsAsync(string.Empty);

        // Setup default returns for ISongLikeService methods
        MockSongLikeService.Setup(x => x.GetLikeCountsAsync(It.IsAny<int>()))
            .ReturnsAsync((0, 0));
        MockSongLikeService.Setup(x => x.GetUserLikeStatusAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((bool?)null);
        MockSongLikeService.Setup(x => x.GetBulkLikeCountsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<int, int>());

        // Setup default returns for IStreamCountService methods
        MockStreamCountService.Setup(x => x.GetStreamCountAsync(It.IsAny<int>()))
            .ReturnsAsync(0);
        MockStreamCountService.Setup(x => x.IncrementStreamCountAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>()))
            .ReturnsAsync(1);

        // Setup default returns for IStreamCountHubClient methods
        MockStreamCountHubClient.Setup(x => x.StartAsync())
            .Returns(Task.CompletedTask);
        MockStreamCountHubClient.Setup(x => x.IsConnected)
            .Returns(true);

        // Setup default returns for ILikeCountHubClient methods
        MockLikeCountHubClient.Setup(x => x.StartAsync())
            .Returns(Task.CompletedTask);
        MockLikeCountHubClient.Setup(x => x.IsConnected)
            .Returns(true);

        // Setup default returns for IWebhookStatusHubClient methods
        MockWebhookStatusHubClient.Setup(x => x.StartAsync())
            .Returns(Task.CompletedTask);
        MockWebhookStatusHubClient.Setup(x => x.IsConnected)
            .Returns(true);

        // Setup default returns for IRecommendationService methods
        MockRecommendationService.Setup(x => x.GetRecommendedPlaylistAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<RecommendedPlaylist>());
        MockRecommendationService.Setup(x => x.GenerateRecommendationsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<RecommendedPlaylist>());
        MockRecommendationService.Setup(x => x.GenerateAllRecommendationsAsync())
            .Returns(Task.CompletedTask);

        // Setup default returns for IPurchaseEmailService methods
        MockPurchaseEmailService.Setup(x => x.SendSongPurchaseConfirmationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IEnumerable<CartItemWithMetadata>>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        MockPurchaseEmailService.Setup(x => x.SendSubscriptionConfirmationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Subscription>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        // Setup default returns for IAccountEmailService methods
        MockAccountEmailService.Setup(x => x.SendAccountCreatedEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        MockAccountEmailService.Setup(x => x.SendAccountClosedEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        MockAccountEmailService.Setup(x => x.SendPasswordChangedEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        MockAccountEmailService.Setup(x => x.SendAccountDeletedEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        MockAccountEmailService.Setup(x => x.SendSubscriptionCancelledEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        MockAccountEmailService.Setup(x => x.SendAccountReactivatedEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        // Setup default return for IAccountDeletionService
        MockAccountDeletionService.Setup(x => x.DeleteAccountAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(IdentityResult.Success);

        // Setup default returns for IEmailService methods
        MockEmailService.Setup(x => x.SendEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        MockEmailService.Setup(x => x.GetEmailLogoHtml())
            .Returns("<div>Logo</div>");
        MockEmailService.Setup(x => x.GetLogoUrl())
            .Returns("https://streamtunes.net/images/logo-light-small.png");
        MockEmailService.Setup(x => x.GetAppBaseUrl())
            .Returns("https://streamtunes.net");

        MockAdminMessageService.Setup(x => x.GetAvailableRoleNamesAsync())
            .ReturnsAsync(new List<string>());
        MockAdminMessageService.Setup(x => x.GetMessagesAsync())
            .ReturnsAsync(new List<AdminMessageSummaryDto>());
        MockAdminMessageService.Setup(x => x.GetPendingDialogMessagesAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<PendingAdminMessageDto>());
        MockAdminMessageService.Setup(x => x.AcknowledgeMessageAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(true);
        MockAdminMessageService.Setup(x => x.CancelMessageAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(0);
        MockAdminMessageService.Setup(x => x.SendPendingEmailsAsync())
            .ReturnsAsync(new AdminMessageEmailJobResult());

        MockAdminMessageHubClient.Setup(x => x.StartAsync())
            .Returns(Task.CompletedTask);
        MockAdminMessageHubClient.Setup(x => x.IsConnected)
            .Returns(true);

        // Setup default returns for ICreatorService methods
        MockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(It.IsAny<int>()))
            .ReturnsAsync((Creator)null);
        MockCreatorService.Setup(x => x.IsActiveCreatorAsync(It.IsAny<int>()))
            .ReturnsAsync(false);
        MockCreatorService.Setup(x => x.GetCreatorIdForUserAsync(It.IsAny<int>()))
            .ReturnsAsync((int?)null);

        // Setup default returns for ICreatorPersonaService methods
        MockCreatorPersonaService.Setup(x => x.GetPersonasByCreatorIdAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<CreatorPersona>());
        MockCreatorPersonaService.Setup(x => x.GetAllPersonasAdminAsync())
            .ReturnsAsync(new List<CreatorPersona>());
        MockCreatorPersonaService.Setup(x => x.GetPersonaByIdAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((CreatorPersona)null);
        MockCreatorPersonaService.Setup(x => x.DeletePersonaAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(true);
        MockCreatorPersonaService.Setup(x => x.DeleteAllPersonasForCreatorAsync(It.IsAny<int>()))
            .ReturnsAsync(0);
        MockCreatorPersonaService.Setup(x => x.DisablePersonaAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        MockCreatorPersonaService.Setup(x => x.EnablePersonaAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        MockCreatorPersonaService.Setup(x => x.GetPersonaImageSasUrl(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(string.Empty);
        MockCreatorPersonaService.Setup(x => x.GetPersonaSongCountAsync(It.IsAny<int>()))
            .ReturnsAsync(0);

        // Setup default returns for IDashboardService methods
        MockDashboardService.Setup(x => x.GetStreamDataAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<StreamInterval>(),
                It.IsAny<HashSet<string>>(), It.IsAny<HashSet<string>>(), It.IsAny<HashSet<string>>()))
            .ReturnsAsync(new List<StreamDataPoint>());
        MockDashboardService.Setup(x => x.GetStreamFilterOptionsAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<HashSet<string>>(), It.IsAny<HashSet<string>>(), It.IsAny<HashSet<string>>()))
            .ReturnsAsync(new StreamFilterOptions());

        // Setup default returns for ISongStatusService methods
        MockSongStatusService.Setup(x => x.GetAllStatusHistoryAsync())
            .ReturnsAsync(new List<SongStatusHistory>());
        MockSongStatusService.Setup(x => x.GetSongStatusHistoryAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<SongStatusHistory>());
        MockSongStatusService.Setup(x => x.DisableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        MockSongStatusService.Setup(x => x.EnableSongAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        // Setup default returns for IGenreService methods
        MockGenreService.Setup(x => x.GetActiveGenresAsync())
            .ReturnsAsync(new List<Genre>());
        MockGenreService.Setup(x => x.GetAllGenresAsync())
            .ReturnsAsync(new List<Genre>());
        MockGenreService.Setup(x => x.AddGenreAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((Genre)null);
        MockGenreService.Setup(x => x.DisableGenreAsync(It.IsAny<int>()))
            .ReturnsAsync(true);
        MockGenreService.Setup(x => x.EnableGenreAsync(It.IsAny<int>()))
            .ReturnsAsync(true);

        // Setup default returns for IFileMatchingService methods
        MockFileMatchingService
            .Setup(x => x.MatchFilesAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new FileMatchingResult());
        MockFileMatchingService
            .Setup(x => x.MatchFilesAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IReadOnlyDictionary<string, (byte[] Data, string ContentType)>>()))
            .ReturnsAsync(new FileMatchingResult());

        // Setup default returns for IStreamPayoutService methods
        MockStreamPayoutService.Setup(x => x.GetPayoutHistoryAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<MusicSalesApp.Models.StreamPayout>());
        MockStreamPayoutService.Setup(x => x.GetUnpaidEarningsAsync(It.IsAny<int>()))
            .ReturnsAsync(0m);

        // Setup default returns for ITipService methods
        MockTipService.Setup(x => x.ValidateTipAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((true, (string)null!));
        MockTipService.Setup(x => x.CreateTipOrderAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((true, (string)null!, "https://paypal.com/approve"));
        MockTipService.Setup(x => x.CaptureTipAsync(It.IsAny<string>()))
            .ReturnsAsync((true, (string)null!, 5.00m));
        MockTipService.Setup(x => x.GetTipsForCreatorAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<MusicSalesApp.Models.Tip>());
        MockTipService.Setup(x => x.GetClearedTipsForPayoutAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<MusicSalesApp.Models.Tip>());
        MockTipService.Setup(x => x.ProcessPendingToClearedAsync())
            .ReturnsAsync(0);
        MockTipService.Setup(x => x.GetAllTipsAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.Tip>());
        MockTipService.Setup(x => x.CancelTipAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        MockStreamPayoutService.Setup(x => x.GetAllPayoutsAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.StreamPayout>());

        MockContactRequestAdminService.Setup(x => x.GetSubmissionsAsync())
            .ReturnsAsync(new List<ContactRequestSubmissionDto>());

        // Setup DbContextFactory mock - use in-memory database for testing
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<MusicSalesApp.Data.AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;
        
        MockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MusicSalesApp.Data.AppDbContext(options));

        // Register services required by BlazorBase
        TestContext.Services.AddSingleton<IAuthenticationService>(MockAuthService.Object);
        TestContext.Services.AddSingleton<AuthenticationStateProvider>(MockAuthStateProvider.Object);
        TestContext.Services.AddSingleton<IAntiforgery>(MockAntiforgery.Object);
        TestContext.Services.AddSingleton<IHttpContextAccessor>(MockHttpContextAccessor.Object);
        TestContext.Services.AddSingleton<IMusicUploadService>(MockMusicUploadService.Object);
        TestContext.Services.AddSingleton<IMusicService>(MockMusicService.Object);
        TestContext.Services.AddSingleton<IAzureStorageService>(MockAzureStorageService.Object);
        TestContext.Services.AddSingleton<IWebHostEnvironment>(MockWebHostEnvironment.Object);
        TestContext.Services.AddSingleton<ISongMetadataService>(MockSongMetadataService.Object);
        TestContext.Services.AddSingleton<IThemeService>(MockThemeService.Object);
        TestContext.Services.AddSingleton<IPlaylistService>(MockPlaylistService.Object);
        TestContext.Services.AddSingleton<ISubscriptionService>(MockSubscriptionService.Object);
        TestContext.Services.AddSingleton<IAppSettingsService>(MockAppSettingsService.Object);
        TestContext.Services.AddSingleton<IPayPalSubscriptionApiService>(MockPayPalSubscriptionApiService.Object);
        TestContext.Services.AddSingleton<IPayPalSubscriptionManagementService>(MockPayPalSubscriptionManagementService.Object);
        TestContext.Services.AddSingleton<UserManager<ApplicationUser>>(MockUserManager.Object);
        TestContext.Services.AddSingleton<IPasskeyService>(MockPasskeyService.Object);
        TestContext.Services.AddSingleton<IOpenGraphService>(MockOpenGraphService.Object);
        TestContext.Services.AddSingleton<ISongLikeService>(MockSongLikeService.Object);
        TestContext.Services.AddSingleton<IStreamCountService>(MockStreamCountService.Object);
        TestContext.Services.AddSingleton<IStreamCountHubClient>(MockStreamCountHubClient.Object);
        TestContext.Services.AddSingleton<ILikeCountHubClient>(MockLikeCountHubClient.Object);
        TestContext.Services.AddSingleton<IWebhookStatusHubClient>(MockWebhookStatusHubClient.Object);
        TestContext.Services.AddSingleton<IMaintenanceHubClient>(MockMaintenanceHubClient.Object);
        TestContext.Services.AddSingleton<IRecommendationService>(MockRecommendationService.Object);
        TestContext.Services.AddSingleton<IPurchaseEmailService>(MockPurchaseEmailService.Object);
        TestContext.Services.AddSingleton<IAccountEmailService>(MockAccountEmailService.Object);
        TestContext.Services.AddSingleton<IAccountDeletionService>(MockAccountDeletionService.Object);
        TestContext.Services.AddSingleton<IEmailService>(MockEmailService.Object);
        TestContext.Services.AddSingleton<ICreatorService>(MockCreatorService.Object);
        TestContext.Services.AddSingleton<ICreatorPersonaService>(MockCreatorPersonaService.Object);
        TestContext.Services.AddSingleton<IDashboardService>(MockDashboardService.Object);
        TestContext.Services.AddSingleton<ISongStatusService>(MockSongStatusService.Object);
        TestContext.Services.AddSingleton<IGenreService>(MockGenreService.Object);
        TestContext.Services.AddSingleton<IFileMatchingService>(MockFileMatchingService.Object);
        TestContext.Services.AddSingleton<IStreamPayoutService>(MockStreamPayoutService.Object);
        TestContext.Services.AddSingleton<ITipService>(MockTipService.Object);
        TestContext.Services.AddSingleton<IReportedSongService>(MockReportedSongService.Object);
        TestContext.Services.AddSingleton<IAdminNotificationService>(MockAdminNotificationService.Object);
        TestContext.Services.AddSingleton<IAdminMessageService>(MockAdminMessageService.Object);
        TestContext.Services.AddSingleton<IAdminMessageHubClient>(MockAdminMessageHubClient.Object);
        TestContext.Services.AddSingleton<IContactRequestAdminService>(MockContactRequestAdminService.Object);
        TestContext.Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<MusicSalesApp.Data.AppDbContext>>(MockDbContextFactory.Object);
        TestContext.Services.AddSingleton<IOptions<MobileAppInstallOptions>>(Options.Create(new MobileAppInstallOptions()));
        TestContext.Services.AddSingleton<TimeProvider>(FakeTimeProvider);

        // Add IConfiguration for components that need it
        var configData = new Dictionary<string, string>
        {
            ["Facebook:AppId"] = "test-facebook-app-id"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData!)
            .Build();
        TestContext.Services.AddSingleton<IConfiguration>(configuration);

        // Authorization for components using [Authorize] and AuthorizeView
        // Using bUnit's TestAuthorizationContext for proper auth testing
        AuthorizationContext = TestContext.AddAuthorization();

        // Add Syncfusion Blazor services for component testing
        TestContext.Services.AddSyncfusionBlazor();

        // Set JSInterop to Loose mode to handle Syncfusion component JS calls
        TestContext.JSInterop.Mode = JSRuntimeMode.Loose;

        // Provide a default HttpClient for components
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);
        // NavigationManager is provided by bUnit automatically.
        
        // NOTE: Cannot set RendererInfo here because it triggers service retrieval
        // which prevents adding more services. Tests that render Syncfusion components
        // requiring RendererInfo should call SetupRendererInfo after service setup.
    }

    /// <summary>
    /// Sets the RendererInfo for tests that need it (e.g., tests with SfDialog components).
    /// Call this method AFTER BaseSetup and BEFORE rendering any components.
    /// </summary>
    protected void SetupRendererInfo()
    {
        try
        {
            TestContext.Renderer.SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Server", true));
        }
        catch (InvalidOperationException)
        {
            // RendererInfo already set or services already retrieved - ignore
        }
    }

    /// <summary>
    /// Sets up an authorized user with a <see cref="ClaimTypes.NameIdentifier"/> claim so that
    /// <c>GetUserId(ClaimsPrincipal)</c> (claims-based, no DB call) returns the correct ID.
    /// Also configures bUnit's authorization context so [Authorize] and AuthorizeView work.
    /// </summary>
    /// <param name="userId">The integer user ID to place in the NameIdentifier claim.</param>
    /// <param name="userName">The user's name/email (used for the Name claim and bUnit auth context).</param>
    /// <param name="roles">Optional roles to assign.</param>
    /// <returns>The configured <see cref="BunitAuthorizationContext"/> for further customization.</returns>
    protected BunitAuthorizationContext SetupAuthorizedUser(int userId, string userName = "testuser", params string[] roles)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, userName)
        };

        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(claimsPrincipal);
        MockAuthStateProvider
            .Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(authState);

        var authContext = AuthorizationContext;
        authContext.SetAuthorized(userName);
        authContext.SetClaims(claims.ToArray());
        if (roles.Length > 0)
            authContext.SetRoles(roles);

        return authContext;
    }

    [TearDown]
    public virtual void BaseTearDown()
    {
        TestContext?.Dispose();
    }

    protected class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, HttpResponseMessage> _responses = new();

        public void SetupJsonResponse(Uri uri, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            _responses[uri] = response;
        }

        public void SetupResponse(Uri uri, HttpResponseMessage response)
        {
            _responses[uri] = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.TryGetValue(request.RequestUri!, out var response))
            {
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
