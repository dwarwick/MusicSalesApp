using Bunit;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class CounterTests : BUnitTestBase
{
    [Test]
    public void Counter_RendersCorrectly()
    {
        // Act
        var cut = TestContext.Render<Counter>();

        // Assert
        Assert.That(cut.Find("h1").TextContent, Is.EqualTo("Counter"));
    }

    [Test]
    public void Counter_InitialCount_IsZero()
    {
        // Act
        var cut = TestContext.Render<Counter>();

        // Assert
        Assert.That(cut.Find("p[role='status']").TextContent, Is.EqualTo("Current count: 0"));
    }

    [Test]
    public void Counter_ClickButton_IncrementsCount()
    {
        // Arrange
        var cut = TestContext.Render<Counter>();
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
        var cut = TestContext.Render<Counter>();
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
        var cut = TestContext.Render<Counter>();

        // Assert
        Assert.That(cut.Find("button").TextContent, Is.EqualTo("Click me"));
    }

    [Test]
    public void Counter_Button_HasCorrectClass()
    {
        // Act
        var cut = TestContext.Render<Counter>();

        // Assert - SfButton renders with e-primary class instead of btn-primary
        Assert.That(cut.Find("button").ClassList, Does.Contain("e-primary"));
    }
}
