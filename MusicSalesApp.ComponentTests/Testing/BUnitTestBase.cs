using Bunit;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Services;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using MusicSalesApp.Common; // <-- Add this line

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

        // Register services required by BlazorBase
        TestContext.Services.AddSingleton<IAuthenticationService>(MockAuthService.Object);
        TestContext.Services.AddSingleton<AuthenticationStateProvider>(MockAuthStateProvider.Object);
        TestContext.Services.AddSingleton<IAntiforgery>(MockAntiforgery.Object);
        TestContext.Services.AddSingleton<IHttpContextAccessor>(MockHttpContextAccessor.Object);
        TestContext.Services.AddSingleton<IMusicUploadService>(MockMusicUploadService.Object);
        TestContext.Services.AddSingleton<IMusicService>(MockMusicService.Object);
        TestContext.Services.AddSingleton<IAzureStorageService>(MockAzureStorageService.Object);

        // Authorization for components using [Authorize]
        TestContext.Services.AddAuthorizationCore();

        // Provide a default HttpClient that returns empty list for api/music to prevent errors in components
        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), Array.Empty<object>());
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
