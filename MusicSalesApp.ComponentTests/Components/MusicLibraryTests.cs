using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.Services;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class MusicLibraryTests
{
    private BunitContext _testContext;
    private Mock<IAuthenticationService> _mockAuthService;
    private Mock<AuthenticationStateProvider> _mockAuthStateProvider;

    [SetUp]
    public void Setup()
    {
        _testContext = new BunitContext();

        // Register mock services
        _mockAuthService = new Mock<IAuthenticationService>();
        _mockAuthStateProvider = new Mock<AuthenticationStateProvider>();

        _testContext.Services.AddSingleton(_mockAuthService.Object);
        _testContext.Services.AddSingleton<AuthenticationStateProvider>(_mockAuthStateProvider.Object);

        // Register a mocked HttpClient
        var handler = new StubHttpMessageHandler();
        var files = new List<StorageFileInfo>
        {
            new StorageFileInfo { Name = "file1.mp3", Length = 1024, LastModified = DateTimeOffset.Now },
            new StorageFileInfo { Name = "file2.mp3", Length = 2048, LastModified = DateTimeOffset.Now }
        };
        handler.SetupJsonResponse(new Uri("http://localhost/api/music"), files);

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        _testContext.Services.AddSingleton<HttpClient>(httpClient);
    }

    [TearDown]
    public void TearDown()
    {
        _testContext?.Dispose();
    }

    [Test]
    public void MusicLibrary_HasCorrectTitle()
    {
        // Act
        var cut = _testContext.Render<MusicLibrary>();

        // Assert
        Assert.That(cut.Find("h3").TextContent, Is.EqualTo("Music Library"));
    }

    [Test]
    public void MusicLibrary_HasTableHeaders()
    {
        // Act
        var cut = _testContext.Render<MusicLibrary>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("File Name"));
        Assert.That(cut.Markup, Does.Contain("Size (KB)"));
        Assert.That(cut.Markup, Does.Contain("Last Modified"));
    }

    private class StubHttpMessageHandler : HttpMessageHandler
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.TryGetValue(request.RequestUri, out var response))
            {
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
