using Bunit;
using MusicSalesApp.Components.Pages;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class WeatherTests : Bunit.TestContext
{
    [Test]
    public void Weather_RendersCorrectly()
    {
        // Act
        var cut = RenderComponent<Weather>();

        // Assert
        Assert.That(cut.Find("h1").TextContent, Is.EqualTo("Weather"));
    }

    [Test]
    public void Weather_ShowsLoadingMessage_Initially()
    {
        // Act
        var cut = RenderComponent<Weather>();

        // Assert
        var loading = cut.FindAll("em").FirstOrDefault(e => e.TextContent == "Loading...");
        Assert.That(loading, Is.Not.Null);
    }

    [Test]
    public void Weather_ShowsTable_AfterDataLoads()
    {
        // Act
        var cut = RenderComponent<Weather>();
        
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
        var cut = RenderComponent<Weather>();
        
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
        var cut = RenderComponent<Weather>();
        
        // Wait for the component to finish loading
        cut.WaitForState(() => cut.FindAll("table").Count > 0, timeout: TimeSpan.FromSeconds(2));

        // Assert
        var rows = cut.FindAll("tbody tr");
        Assert.That(rows, Has.Count.EqualTo(5));
    }
}
