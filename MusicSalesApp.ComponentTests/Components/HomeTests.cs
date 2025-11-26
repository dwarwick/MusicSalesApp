using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.Services;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class HomeTests
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
    public void Home_RendersCorrectly()
    {
        // Act
        var cut = _testContext.Render<Home>();

        // Assert
        Assert.That(cut.Find("h1").TextContent, Is.EqualTo("Hello, world!"));
    }

    [Test]
    public void Home_WelcomeMessage_IsDisplayed()
    {
        // Act
        var cut = _testContext.Render<Home>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Welcome to your new app"));
    }
}
