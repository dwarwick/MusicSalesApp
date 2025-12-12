using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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
    protected Mock<UserManager<ApplicationUser>> MockUserManager { get; private set; } = default!;

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
        TestContext.Services.AddSingleton<UserManager<ApplicationUser>>(MockUserManager.Object);

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
