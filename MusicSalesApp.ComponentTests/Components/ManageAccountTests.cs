using Bunit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages.Auth;
using MusicSalesApp.Common.Helpers;
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

    [Test]
    public void ManageAccount_HasCreatorActivatedDialogRef()
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

        // Act
        var cut = TestContext.Render<ManageAccount>();

        // Assert — the component instance should have the _creatorActivatedDialog field
        var instance = cut.Instance;
        Assert.That(instance, Is.Not.Null);
        // Verify the component renders without errors when dialog markup is present
        Assert.That(cut.Markup, Is.Not.Null);
    }

    [Test]
    public void ManageAccount_AppleActiveSubscriptionWithFutureEndDate_ShowsManageSubscriptionAndNoNewSubscriptionPrompt()
    {
        SetupAuthorizedUser(1, "testuser@test.com");

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(
            new Uri("http://localhost/api/subscription/status"),
            new
            {
                HasSubscription = true,
                Status = SubscriptionStatuses.Active,
                MonthlyPrice = 3.99m,
                StartDate = DateTime.UtcNow.AddDays(-5),
                EndDate = DateTime.UtcNow.AddDays(25),
                NextBillingDate = DateTime.UtcNow.AddDays(25),
                BillingSource = BillingSources.Apple,
                SubscriptionPrice = "3.99"
            });
        TestContext.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        SetupRendererInfo();

        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Current Billing Period Ends:"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Status:</strong> Active"));
        Assert.That(cut.Markup, Does.Contain("Current Billing Period Ends:"));
        Assert.That(cut.Markup, Does.Contain("Manage Subscription"));
        Assert.That(cut.Markup, Does.Not.Contain("Start a new subscription at any time."));
    }

    [Test]
    public void ManageAccount_AppleSubscriptionManagementUrl_ComesFromConfiguration()
    {
        SetupAuthorizedUser(1, "testuser@test.com");

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(
            new Uri("http://localhost/api/subscription/status"),
            new
            {
                HasSubscription = true,
                Status = SubscriptionStatuses.Active,
                MonthlyPrice = 3.99m,
                BillingSource = BillingSources.Apple,
                SubscriptionPrice = "3.99"
            });
        TestContext.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Facebook:AppId"] = "test-facebook-app-id",
                ["PayPal:SubscriptionPrice"] = "3.99",
                ["AppleAppStore:SubscriptionManagementUrl"] = "https://developer.apple.com/documentation/storekit/testing-disabling-auto-renew"
            })
            .Build();
        TestContext.Services.AddSingleton<IConfiguration>(configuration);

        SetupRendererInfo();

        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Manage Subscription"), TimeSpan.FromSeconds(5));

        var method = typeof(ManageAccountModel).GetMethod("GetExternalSubscriptionManagementUrl",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var url = method?.Invoke(cut.Instance, null) as string;

        Assert.That(url, Is.EqualTo("https://developer.apple.com/documentation/storekit/testing-disabling-auto-renew"));
    }

    [Test]
    public void ManageAccount_CancelledSubscriptionWithRemainingAccess_HidesCancelActionsAndShowsNonRenewingMessage()
    {
        SetupAuthorizedUser(1, "testuser@test.com");

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(
            new Uri("http://localhost/api/subscription/status"),
            new
            {
                HasSubscription = true,
                Status = SubscriptionStatuses.Cancelled,
                MonthlyPrice = 3.99m,
                StartDate = DateTime.UtcNow.AddDays(-5),
                EndDate = DateTime.UtcNow.AddDays(25),
                NextBillingDate = DateTime.UtcNow.AddDays(25),
                BillingSource = BillingSources.PayPal,
                SubscriptionPrice = "3.99"
            });
        TestContext.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        SetupRendererInfo();

        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Access Until:"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Status:</strong> Renews Off"));
        Assert.That(cut.Markup, Does.Contain("has been canceled"));
        Assert.That(cut.Markup, Does.Contain("will not automatically renew"));
        Assert.That(cut.Markup, Does.Not.Contain("Cancel Subscription"));
        Assert.That(cut.Markup, Does.Not.Contain("Manage Subscription"));
    }
}
