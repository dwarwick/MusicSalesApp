#pragma warning disable CS0618, CS0619
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.Services;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class WeatherTests
{
    private BunitContext _testContext;

    [SetUp]
    public void Setup()
    {
        _testContext = new BunitContext();

        // Register mock services
        var mockAuthService = new Mock<IAuthenticationService>();
        var mockAuthStateProvider = new Mock<AuthenticationStateProvider>();

        _testContext.Services.AddSingleton(mockAuthService.Object);
        _testContext.Services.AddSingleton<AuthenticationStateProvider>(mockAuthStateProvider.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _testContext?.Dispose();
    }

    [Test]
    public void Weather_RendersCorrectly()
    {
        // Act
        var cut = _testContext.Render<Weather>();

        // Assert
        Assert.That(cut.Find("h1").TextContent, Is.EqualTo("Weather"));
    }

    [Test]
    public void Weather_ShowsLoadingMessage_Initially()
    {
        // Act
        var cut = _testContext.Render<Weather>();

        // Assert
        var loading = cut.FindAll("em").FirstOrDefault(e => e.TextContent == "Loading...");
        Assert.That(loading, Is.Not.Null);
    }
}
#pragma warning restore CS0618, CS0619
