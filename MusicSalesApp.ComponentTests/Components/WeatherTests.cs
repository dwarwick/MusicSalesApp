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
    private Bunit.TestContext _testContext;

    [SetUp]
    public void Setup()
    {
        _testContext = new Bunit.TestContext();

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
        var cut = _testContext.RenderComponent<Weather>();

        // Assert
        Assert.That(cut.Find("h1").TextContent, Is.EqualTo("Weather"));
    }

    [Test]
    public void Weather_ShowsLoadingMessage_Initially()
    {
        // Act
        var cut = _testContext.RenderComponent<Weather>();

        // Assert
        var loading = cut.FindAll("em").FirstOrDefault(e => e.TextContent == "Loading...");
        Assert.That(loading, Is.Not.Null);
    }

    [Test]
    public void Weather_ShowsTable_AfterDataLoads()
    {
        // Act
        var cut = _testContext.RenderComponent<Weather>();
        
        // Wait for the component to finish loading
        cut.WaitForState(() => cut.FindAll("table").Count > 0, timeout: TimeSpan.FromSeconds(2));

        // Assert
        var table = cut.Find("table");
        Assert.That(table, Is.Not.Null);
    }

    [Test]
    public void Weather_TableHasCorrectHeaders()
    {
        // Act
        var cut = _testContext.RenderComponent<Weather>();
        
        // Wait for the component to finish loading
        cut.WaitForState(() => cut.FindAll("table").Count > 0, timeout: TimeSpan.FromSeconds(2));

        // Assert
        var headers = cut.FindAll("th");
        Assert.That(headers, Has.Count.EqualTo(4));
        Assert.That(headers[0].TextContent, Is.EqualTo("Date"));
        Assert.That(headers[1].GetAttribute("aria-label"), Is.EqualTo("Temperature in Celsius"));
        Assert.That(headers[2].GetAttribute("aria-label"), Is.EqualTo("Temperature in Fahrenheit"));
        Assert.That(headers[3].TextContent, Is.EqualTo("Summary"));
    }

    [Test]
    public void Weather_ShowsFiveForecasts()
    {
        // Act
        var cut = _testContext.RenderComponent<Weather>();
        
        // Wait for the component to finish loading
        cut.WaitForState(() => cut.FindAll("table").Count > 0, timeout: TimeSpan.FromSeconds(2));

        // Assert
        var rows = cut.FindAll("tbody tr");
        Assert.That(rows, Has.Count.EqualTo(5));
    }
}
