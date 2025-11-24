using Bunit;
using MusicSalesApp.Components.Pages;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class HomeTests : Bunit.TestContext
{
    [Test]
    public void Home_RendersCorrectly()
    {
        // Act
        var cut = RenderComponent<Home>();

        // Assert
        Assert.That(cut.Find("h1").TextContent, Is.EqualTo("Hello, world!"));
    }

    [Test]
    public void Home_WelcomeMessage_IsDisplayed()
    {
        // Act
        var cut = RenderComponent<Home>();
        var paragraphs = cut.FindAll("p");

        // Assert
        Assert.That(paragraphs, Has.Count.GreaterThan(0));
        Assert.That(paragraphs[0].TextContent, Does.Contain("Welcome to your new app"));
    }
}
