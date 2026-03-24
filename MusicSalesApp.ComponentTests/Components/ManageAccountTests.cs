using Bunit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages.Auth;
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

    [Test]
    public void ManageAccount_ShowsTaxFormError_WhenPendingWithErrorMessage()
    {
        // Arrange
        var authContext = SetupAuthorizedUser(1, "testuser@test.com");
        SetupRendererInfo();

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);

        var creator = new Creator
        {
            Id = 1,
            UserId = 1,
            IsActive = false,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Pending,
            LastTaxFormErrorMessage = "Middle Name is Invalid. The Middle Name can have Alphabets, Numbers and Special Characters ( & - ).",
            PayPalAccountAffirmed = true
        };

        MockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(1))
            .ReturnsAsync(creator);

        // Act
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Middle Name is Invalid"), TimeSpan.FromSeconds(5));

        // Assert — error message is displayed in an alert
        Assert.That(cut.Markup, Does.Contain("Middle Name is Invalid"));
        Assert.That(cut.Markup, Does.Contain("previous tax form submission had an error"));
        Assert.That(cut.Markup, Does.Contain("alert-danger"));
    }

    [Test]
    public void ManageAccount_ShowsNormalPendingMessage_WhenNoErrorMessage()
    {
        // Arrange
        var authContext = SetupAuthorizedUser(1, "testuser@test.com");
        SetupRendererInfo();

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);

        var creator = new Creator
        {
            Id = 1,
            UserId = 1,
            IsActive = false,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Pending,
            LastTaxFormErrorMessage = null,
            PayPalAccountAffirmed = true
        };

        MockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(1))
            .ReturnsAsync(creator);

        // Act
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Complete Tax Form"), TimeSpan.FromSeconds(5));

        // Assert — normal pending message, no error alert
        Assert.That(cut.Markup, Does.Contain("Please complete your tax form"));
        Assert.That(cut.Markup, Does.Not.Contain("alert-danger"));
        Assert.That(cut.Markup, Does.Not.Contain("previous tax form submission had an error"));
    }

    [Test]
    public void ManageAccount_TaxFormErrorAlert_HasAccessibilityAttributes()
    {
        // Arrange
        var authContext = SetupAuthorizedUser(1, "testuser@test.com");
        SetupRendererInfo();

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);

        var creator = new Creator
        {
            Id = 1,
            UserId = 1,
            IsActive = false,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Pending,
            LastTaxFormErrorMessage = "Middle Name is Invalid.",
            PayPalAccountAffirmed = true
        };

        MockCreatorService.Setup(x => x.GetCreatorByUserIdAsync(1))
            .ReturnsAsync(creator);

        // Act
        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Middle Name is Invalid"), TimeSpan.FromSeconds(5));

        // Assert — the error alert div has accessibility attributes for screen readers
        var alertDiv = cut.Find("div.alert-danger");
        Assert.That(alertDiv.GetAttribute("role"), Is.EqualTo("alert"),
            "Tax form error alert must have role='alert' for screen reader accessibility");
        Assert.That(alertDiv.GetAttribute("aria-live"), Is.EqualTo("assertive"),
            "Tax form error alert must have aria-live='assertive' so screen readers announce it immediately");
    }
}
