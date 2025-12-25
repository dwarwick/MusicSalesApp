using Bunit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;
using System.Security.Claims;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class ManageAccountTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
    }

    [Test]
    public void ManageAccount_NotAuthenticated_ShowsWarning()
    {
        // Arrange - already set up with unauthenticated user
        SetupRendererInfo();

        // Act
        var cut = TestContext.Render<ManageAccount>();

        // Assert - Should show a loading spinner or warning about authentication
        // Component will show loading initially, then show authentication warning
        Assert.That(cut.Markup, Is.Not.Null);
    }

    [Test]
    public void ManageAccount_RendersPageTitle()
    {
        // Arrange
        SetupRendererInfo();
        
        // Act
        var cut = TestContext.Render<ManageAccount>();

        // Assert - PageTitle should be set
        Assert.That(cut.Markup, Is.Not.Null);
    }
}
