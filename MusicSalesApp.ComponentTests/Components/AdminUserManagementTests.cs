using Bunit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class AdminUserManagementTests : BUnitTestBase
{
    private Mock<RoleManager<IdentityRole<int>>> _mockRoleManager = default!;

    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        // Setup RoleManager mock
        var roleStore = new Mock<IRoleStore<IdentityRole<int>>>();
        _mockRoleManager = new Mock<RoleManager<IdentityRole<int>>>(
            roleStore.Object, null!, null!, null!, null!);

        TestContext.Services.AddSingleton<RoleManager<IdentityRole<int>>>(_mockRoleManager.Object);
        
        // Setup RendererInfo required for SfDialog components
        SetupRendererInfo();
    }

    [Test]
    public void AdminUserManagement_RendersGrid()
    {
        // Act
        var cut = TestContext.Render<AdminUserManagement>();

        // Assert - the grid should be rendered with no records
        Assert.That(cut.Markup, Does.Contain("No records to display"));
    }

    [Test]
    public void AdminUserManagement_HasPageTitle()
    {
        // Act
        var cut = TestContext.Render<AdminUserManagement>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("User Management"));
    }

    [Test]
    public void AdminUserManagement_GridHasExpectedColumns()
    {
        // Act
        var cut = TestContext.Render<AdminUserManagement>();

        // Assert - verify all expected column headers
        Assert.That(cut.Markup, Does.Contain("Username"));
        Assert.That(cut.Markup, Does.Contain("Email Confirmed"));
        Assert.That(cut.Markup, Does.Contain("Phone Number"));
        Assert.That(cut.Markup, Does.Contain("Phone Confirmed"));
        Assert.That(cut.Markup, Does.Contain("Lockout End"));
        Assert.That(cut.Markup, Does.Contain("Lockout Enabled"));
        Assert.That(cut.Markup, Does.Contain("Failed Logins"));
        Assert.That(cut.Markup, Does.Contain("Last Verification Sent"));
        Assert.That(cut.Markup, Does.Contain("Theme"));
        Assert.That(cut.Markup, Does.Contain("Suspended"));
        Assert.That(cut.Markup, Does.Contain("Suspended At"));
        Assert.That(cut.Markup, Does.Contain("Roles"));
    }
}
