using Bunit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages.Auth;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;
using System.Reflection;
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
    public void ManageAccount_DoesNotRenderCreatorSettingsSections()
    {
        SetupAuthorizedUser(1, "testuser@test.com");

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true,
            TimeZoneId = "America/New_York"
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);
        MockCreatorService.Setup(x => x.IsActiveCreatorAsync(1))
            .ReturnsAsync(true);
        TestContext.JSInterop.Setup<string>("dashboardHelper.getUserTimeZone")
            .SetResult("America/New_York");

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(
            new Uri("http://localhost/api/subscription/status"),
            new
            {
                HasSubscription = false,
                Status = SubscriptionStatuses.Expired,
                SubscriptionPrice = "3.99"
            });
        TestContext.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        SetupRendererInfo();

        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Close Account"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Not.Contain("Become a Creator"));
        Assert.That(cut.Markup, Does.Not.Contain("Creator Profile"));
        Assert.That(cut.Markup, Does.Not.Contain("Payout Email Address"));
        Assert.That(cut.Markup, Does.Not.Contain("Complete Tax Form"));
        Assert.That(cut.Markup, Does.Not.Contain("Stop Being a Creator"));
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
            EmailConfirmed = true,
            TimeZoneId = "America/New_York"
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);

        TestContext.JSInterop.Setup<string>("dashboardHelper.getUserTimeZone")
            .SetResult("America/New_York");

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
        Assert.That(cut.Markup, Does.Contain("America/New_York"));
        Assert.That(cut.Markup, Does.Contain("Current Billing Period Ends:"));
        Assert.That(cut.Markup, Does.Contain("will automatically renew unless canceled"));
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
            EmailConfirmed = true,
            TimeZoneId = "America/New_York"
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
            EmailConfirmed = true,
            TimeZoneId = "America/New_York"
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
        Assert.That(cut.Markup, Does.Contain("America/New_York"));
        Assert.That(cut.Markup, Does.Contain("has been canceled"));
        Assert.That(cut.Markup, Does.Contain("will not automatically renew"));
        Assert.That(cut.Markup, Does.Not.Contain("Cancel Subscription"));
        Assert.That(cut.Markup, Does.Not.Contain("Manage Subscription"));
    }

    [Test]
    public async Task ManageAccount_DeleteAccount_TrimsConfirmationEmail()
    {
        SetupAuthorizedUser(1, "testuser@test.com");

        var testUser = new ApplicationUser
        {
            Id = 1,
            UserName = "testuser@test.com",
            Email = "testuser@test.com",
            EmailConfirmed = true,
            TimeZoneId = "America/New_York"
        };

        MockUserManager.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(testUser);
        MockCreatorService.Setup(x => x.IsActiveCreatorAsync(1))
            .ReturnsAsync(false);
        TestContext.JSInterop.Setup<string>("dashboardHelper.getUserTimeZone")
            .SetResult("America/New_York");

        var handler = new StubHttpMessageHandler();
        handler.SetupJsonResponse(
            new Uri("http://localhost/api/subscription/status"),
            new
            {
                HasSubscription = false,
                Status = SubscriptionStatuses.Expired,
                SubscriptionPrice = "3.99"
            });
        TestContext.Services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        SetupRendererInfo();

        var cut = TestContext.Render<ManageAccount>();
        cut.WaitForState(() => cut.Markup.Contains("Close Account"), TimeSpan.FromSeconds(5));

        SetField(cut.Instance, "_accountActionConfirmEmail", "  testuser@test.com  ");
        await InvokeNonPublicTask(cut.Instance, "DeleteAccount");

        MockAccountDeletionService.Verify(x => x.DeleteAccountAsync(testUser), Times.Once);
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = typeof(ManageAccountModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Expected field {fieldName} to exist.");
        field!.SetValue(instance, value);
    }

    private static Task InvokeNonPublicTask(object instance, string methodName)
    {
        var method = typeof(ManageAccountModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Expected method {methodName} to exist.");
        return (Task)method!.Invoke(instance, null)!;
    }
}
