#pragma warning disable CS0618, CS0619
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.Services;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class CounterTests
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
    public void Counter_RendersCorrectly()
    {
        // Act
        var cut = _testContext.Render<Counter>();

        // Assert
        Assert.That(cut.Find("h1").TextContent, Is.EqualTo("Counter"));
    }

    [Test]
    public void Counter_InitialCount_IsZero()
    {
        // Act
        var cut = _testContext.Render<Counter>();

        // Assert
        Assert.That(cut.Find("p[role='status']").TextContent, Is.EqualTo("Current count: 0"));
    }

    [Test]
    public void Counter_ClickButton_IncrementsCount()
    {
        // Arrange
        var cut = _testContext.Render<Counter>();
        var button = cut.Find("button");

        // Act
        button.Click();

        // Assert
        Assert.That(cut.Find("p[role='status']").TextContent, Is.EqualTo("Current count: 1"));
    }

    [Test]
    public void Counter_MultipleClicks_IncrementsCountMultipleTimes()
    {
        // Arrange
        var cut = _testContext.Render<Counter>();
        var button = cut.Find("button");

        // Act
        button.Click();
        button.Click();
        button.Click();

        // Assert
        Assert.That(cut.Find("p[role='status']").TextContent, Is.EqualTo("Current count: 3"));
    }

    [Test]
    public void Counter_Button_HasCorrectText()
    {
        // Act
        var cut = _testContext.Render<Counter>();

        // Assert
        Assert.That(cut.Find("button").TextContent, Is.EqualTo("Click me"));
    }

    [Test]
    public void Counter_Button_HasCorrectClass()
    {
        // Act
        var cut = _testContext.Render<Counter>();

        // Assert
        Assert.That(cut.Find("button").ClassList, Does.Contain("btn-primary"));
    }
}
#pragma warning restore CS0618, CS0619
