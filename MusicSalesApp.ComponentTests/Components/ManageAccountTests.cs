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

        // Act
        var cut = TestContext.Render<ManageAccount>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("You must be logged in to manage your account"));
    }

    [Test]
    public void ManageAccount_Authenticated_RendersCorrectly()
    {
        // Arrange
        var userId = 1;
        var user = new ApplicationUser { Id = userId, UserName = "testuser", Email = "test@test.com" };
        
        var authenticatedUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "testuser")
        }, "TestAuth"));

        MockAuthStateProvider
            .Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(authenticatedUser));

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(user);

        MockPasskeyService.Setup(x => x.GetUserPasskeysAsync(userId))
            .ReturnsAsync(new List<Passkey>());

        // Act
        var cut = TestContext.Render<ManageAccount>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Manage Account"));
        Assert.That(cut.Markup, Does.Contain("Change Password"));
        Assert.That(cut.Markup, Does.Contain("Passkeys"));
        Assert.That(cut.Markup, Does.Contain("Delete Account"));
    }

    [Test]
    public void ManageAccount_HasPasswordChangeSection()
    {
        // Arrange
        var userId = 1;
        var user = new ApplicationUser { Id = userId, UserName = "testuser", Email = "test@test.com" };
        
        var authenticatedUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "testuser")
        }, "TestAuth"));

        MockAuthStateProvider
            .Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(authenticatedUser));

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(user);

        // Act
        var cut = TestContext.Render<ManageAccount>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Current Password"));
        Assert.That(cut.Markup, Does.Contain("New Password"));
        Assert.That(cut.Markup, Does.Contain("Confirm New Password"));
    }

    [Test]
    public void ManageAccount_HasPasskeySection()
    {
        // Arrange
        var userId = 1;
        var user = new ApplicationUser { Id = userId, UserName = "testuser", Email = "test@test.com" };
        
        var authenticatedUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "testuser")
        }, "TestAuth"));

        MockAuthStateProvider
            .Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(authenticatedUser));

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(user);

        // Act
        var cut = TestContext.Render<ManageAccount>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Passkeys"));
        Assert.That(cut.Markup, Does.Contain("Add Passkey"));
    }

    [Test]
    public void ManageAccount_ShowsPasskeysList()
    {
        // Arrange
        var userId = 1;
        var user = new ApplicationUser { Id = userId, UserName = "testuser", Email = "test@test.com" };
        
        var passkeys = new List<Passkey>
        {
            new Passkey
            {
                Id = 1,
                UserId = userId,
                Name = "My Laptop",
                CreatedAt = DateTime.UtcNow.AddDays(-5),
                LastUsedAt = DateTime.UtcNow.AddHours(-2)
            },
            new Passkey
            {
                Id = 2,
                UserId = userId,
                Name = "iPhone",
                CreatedAt = DateTime.UtcNow.AddDays(-10),
                LastUsedAt = DateTime.UtcNow.AddDays(-1)
            }
        };
        
        var authenticatedUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "testuser")
        }, "TestAuth"));

        MockAuthStateProvider
            .Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(authenticatedUser));

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(user);

        MockPasskeyService.Setup(x => x.GetUserPasskeysAsync(userId))
            .ReturnsAsync(passkeys);

        // Act
        var cut = TestContext.Render<ManageAccount>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("My Laptop"));
        Assert.That(cut.Markup, Does.Contain("iPhone"));
    }

    [Test]
    public void ManageAccount_HasDeleteAccountSection()
    {
        // Arrange
        var userId = 1;
        var user = new ApplicationUser { Id = userId, UserName = "testuser", Email = "test@test.com" };
        
        var authenticatedUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "testuser")
        }, "TestAuth"));

        MockAuthStateProvider
            .Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(authenticatedUser));

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(user);

        // Act
        var cut = TestContext.Render<ManageAccount>();

        // Assert
        Assert.That(cut.Markup, Does.Contain("Delete Account"));
        Assert.That(cut.Markup, Does.Contain("Delete My Account"));
    }
}
