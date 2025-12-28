using Bunit;
using Bunit.TestDoubles;
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

    protected Mock<IAuthenticationService> MockAuthService { get; private set; } = default!;
    protected Mock<AuthenticationStateProvider> MockAuthStateProvider { get; private set; } = default!;
    protected Mock<IAntiforgery> MockAntiforgery { get; private set; } = default!;
    protected Mock<IHttpContextAccessor> MockHttpContextAccessor { get; private set; } = default!;
    protected Mock<IMusicUploadService> MockMusicUploadService { get; private set; } = default!;
    protected Mock<IMusicService> MockMusicService { get; private set; } = default!;
    protected Mock<IAzureStorageService> MockAzureStorageService { get; private set; } = default!;
    protected Mock<ICartService> MockCartService { get; private set; } = default!;
    protected Mock<IWebHostEnvironment> MockWebHostEnvironment { get; private set; } = default!;
    protected Mock<ISongMetadataService> MockSongMetadataService { get; private set; } = default!;
    protected Mock<IThemeService> MockThemeService { get; private set; } = default!;
    protected Mock<IPlaylistService> MockPlaylistService { get; private set; } = default!;
    protected Mock<ISubscriptionService> MockSubscriptionService { get; private set; } = default!;
    protected Mock<IAppSettingsService> MockAppSettingsService { get; private set; } = default!;
    protected Mock<UserManager<ApplicationUser>> MockUserManager { get; private set; } = default!;
    protected Mock<IPasskeyService> MockPasskeyService { get; private set; } = default!;
    protected Mock<IOpenGraphService> MockOpenGraphService { get; private set; } = default!;
    protected Mock<ISongLikeService> MockSongLikeService { get; private set; } = default!;
    protected Mock<IStreamCountService> MockStreamCountService { get; private set; } = default!;
    protected Mock<IStreamCountHubClient> MockStreamCountHubClient { get; private set; } = default!;
    protected Mock<IRecommendationService> MockRecommendationService { get; private set; } = default!;
    protected Mock<IPurchaseEmailService> MockPurchaseEmailService { get; private set; } = default!;
    protected Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<MusicSalesApp.Data.AppDbContext>> MockDbContextFactory { get; private set; } = default!;

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
        MockCartService = new Mock<ICartService>();
        MockWebHostEnvironment = new Mock<IWebHostEnvironment>();
        MockSongMetadataService = new Mock<ISongMetadataService>();
        MockThemeService = new Mock<IThemeService>();
        MockPlaylistService = new Mock<IPlaylistService>();
        MockSubscriptionService = new Mock<ISubscriptionService>();
        MockAppSettingsService = new Mock<IAppSettingsService>();
        MockPasskeyService = new Mock<IPasskeyService>();
        MockOpenGraphService = new Mock<IOpenGraphService>();
        MockSongLikeService = new Mock<ISongLikeService>();
        MockStreamCountService = new Mock<IStreamCountService>();
        MockStreamCountHubClient = new Mock<IStreamCountHubClient>();
        MockRecommendationService = new Mock<IRecommendationService>();
        MockPurchaseEmailService = new Mock<IPurchaseEmailService>();
        MockDbContextFactory = new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<MusicSalesApp.Data.AppDbContext>>();
        
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

        // Setup default returns for ICartService methods
        MockCartService.Setup(x => x.GetCartItemsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<MusicSalesApp.Models.CartItem>());
        MockCartService.Setup(x => x.GetCartItemCountAsync(It.IsAny<int>()))
            .ReturnsAsync(0);
        MockCartService.Setup(x => x.GetOwnedSongsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<string>());

        // Setup default returns for ISongMetadataService methods
        MockSongMetadataService.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<MusicSalesApp.Models.SongMetadata>());

        // Setup default returns for IThemeService methods
        MockThemeService.Setup(x => x.CurrentTheme).Returns("Light");
        MockThemeService.Setup(x => x.IsDarkTheme).Returns(false);
        MockThemeService.Setup(x => x.SyncfusionCssUrl).Returns("https://cdn.syncfusion.com/blazor/31.2.2/styles/bootstrap5.3.css");
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

        // Setup default returns for IStreamCountService methods
        MockStreamCountService.Setup(x => x.GetStreamCountAsync(It.IsAny<int>()))
            .ReturnsAsync(0);
        MockStreamCountService.Setup(x => x.IncrementStreamCountAsync(It.IsAny<int>()))
            .ReturnsAsync(1);

        // Setup default returns for IStreamCountHubClient methods
        MockStreamCountHubClient.Setup(x => x.StartAsync())
            .Returns(Task.CompletedTask);
        MockStreamCountHubClient.Setup(x => x.IsConnected)
            .Returns(true);

        // Setup default returns for IRecommendationService methods
        MockRecommendationService.Setup(x => x.GetRecommendedPlaylistAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<RecommendedPlaylist>());
        MockRecommendationService.Setup(x => x.GenerateRecommendationsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<RecommendedPlaylist>());
        MockRecommendationService.Setup(x => x.HasFreshRecommendationsAsync(It.IsAny<int>()))
            .ReturnsAsync(false);

        // Setup default returns for IPurchaseEmailService methods
        MockPurchaseEmailService.Setup(x => x.SendSongPurchaseConfirmationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IEnumerable<CartItemWithMetadata>>(), It.IsAny<decimal>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        MockPurchaseEmailService.Setup(x => x.SendSubscriptionConfirmationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Subscription>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

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
        TestContext.Services.AddSingleton<ICartService>(MockCartService.Object);
        TestContext.Services.AddSingleton<IWebHostEnvironment>(MockWebHostEnvironment.Object);
        TestContext.Services.AddSingleton<ISongMetadataService>(MockSongMetadataService.Object);
        TestContext.Services.AddSingleton<IThemeService>(MockThemeService.Object);
        TestContext.Services.AddSingleton<IPlaylistService>(MockPlaylistService.Object);
        TestContext.Services.AddSingleton<ISubscriptionService>(MockSubscriptionService.Object);
        TestContext.Services.AddSingleton<IAppSettingsService>(MockAppSettingsService.Object);
        TestContext.Services.AddSingleton<UserManager<ApplicationUser>>(MockUserManager.Object);
        TestContext.Services.AddSingleton<IPasskeyService>(MockPasskeyService.Object);
        TestContext.Services.AddSingleton<IOpenGraphService>(MockOpenGraphService.Object);
        TestContext.Services.AddSingleton<ISongLikeService>(MockSongLikeService.Object);
        TestContext.Services.AddSingleton<IStreamCountService>(MockStreamCountService.Object);
        TestContext.Services.AddSingleton<IStreamCountHubClient>(MockStreamCountHubClient.Object);
        TestContext.Services.AddSingleton<IRecommendationService>(MockRecommendationService.Object);
        TestContext.Services.AddSingleton<IPurchaseEmailService>(MockPurchaseEmailService.Object);
        TestContext.Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<MusicSalesApp.Data.AppDbContext>>(MockDbContextFactory.Object);

        // Add IConfiguration for components that need it
        var configData = new Dictionary<string, string>
        {
            ["Facebook:AppId"] = "test-facebook-app-id",
            ["PayPal:SubscriptionPrice"] = "3.99"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData!)
            .Build();
        TestContext.Services.AddSingleton<IConfiguration>(configuration);

        // Authorization for components using [Authorize] and AuthorizeView
        // Using bUnit's TestAuthorizationContext for proper auth testing
        TestContext.AddAuthorization();

        // Add Syncfusion Blazor services for component testing
        TestContext.Services.AddSyncfusionBlazor();

        // Provide a default HttpClient for components
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        TestContext.Services.AddSingleton<HttpClient>(httpClient);
        // NavigationManager is provided by bUnit automatically.
        
        // NOTE: Cannot set RendererInfo here because it triggers service retrieval
        // which prevents adding more services. This causes SfDialog components to fail
        // in tests. See: https://github.com/bUnit-dev/bUnit/issues/XXX
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
