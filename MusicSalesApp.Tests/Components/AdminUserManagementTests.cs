using System.Reflection;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Pages.Admin;
using MusicSalesApp.Models;

namespace MusicSalesApp.Tests.Components;

[TestFixture]
public class AdminUserManagementTests
{
    [Test]
    public void ResolveSubscriptionStatus_ReturnsTrialActive_WhenTrialIsCurrentlyEntitled()
    {
        var status = ResolveSubscriptionStatus(new ApplicationUser(), new Subscription
        {
            Status = SubscriptionStatuses.Cancelled,
            EndDate = DateTime.UtcNow.AddDays(2),
            TrialEndDate = DateTime.UtcNow.AddDays(2)
        });

        Assert.That(status, Is.EqualTo("Trial Active"));
    }

    [Test]
    public void ResolveSubscriptionStatus_ReturnsExpired_WhenTrialHasEndedAndSubscriptionIsExpired()
    {
        var status = ResolveSubscriptionStatus(new ApplicationUser(), new Subscription
        {
            Status = SubscriptionStatuses.Expired,
            EndDate = DateTime.UtcNow.AddMinutes(-5),
            TrialEndDate = DateTime.UtcNow.AddMinutes(-5)
        });

        Assert.That(status, Is.EqualTo("Expired"));
    }

    [Test]
    public void ResolveSubscriptionStatus_ReturnsCancelled_WhenSubscriptionIsCancelled()
    {
        var status = ResolveSubscriptionStatus(new ApplicationUser(), new Subscription
        {
            Status = SubscriptionStatuses.Cancelled,
            EndDate = DateTime.UtcNow.AddMinutes(-5)
        });

        Assert.That(status, Is.EqualTo("Cancelled"));
    }

    private static string ResolveSubscriptionStatus(ApplicationUser user, Subscription subscription)
    {
        var method = typeof(AdminUserManagementModel).GetMethod(
            "ResolveSubscriptionStatus",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(method, Is.Not.Null);
        return (string)method!.Invoke(null, [user, subscription])!;
    }
}