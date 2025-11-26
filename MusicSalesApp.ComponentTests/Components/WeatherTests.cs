using Bunit;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class WeatherTests : BUnitTestBase
{
    [Test]
    public void Weather_RendersCorrectly()
    {
        // Act
        var cut = TestContext.Render<Weather>();

        // Assert
        Assert.That(cut.Find("h1").TextContent, Is.EqualTo("Weather"));
    }

    [Test]
    public void Weather_ShowsLoadingMessage_Initially()
    {
        // Act
        var cut = TestContext.Render<Weather>();

        // Assert
        var loading = cut.FindAll("em").FirstOrDefault(e => e.TextContent == "Loading...");
        Assert.That(loading, Is.Not.Null);
    }
}
