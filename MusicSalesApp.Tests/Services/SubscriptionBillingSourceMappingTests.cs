#nullable enable

using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// A subscription that does not say which provider bills it is dangerous, not untidy: EF gives
/// "BillingSource != x" C# null semantics, so an unlabelled row differs from every provider
/// including its own, and reconciliation cancels the agreement as a redundant overlap. These pin
/// the two things that keep NULL out.
/// </summary>
[TestFixture]
public class SubscriptionBillingSourceMappingTests
{
    [Test]
    public void BillingSource_IsMappedAsRequired()
    {
        // The project has nullable reference types disabled, so EF maps a plain string to a
        // nullable column unless it is explicitly required - which is how the column came to allow
        // NULL despite the property never being assigned one.
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"BillingSourceMappingTestDb_{Guid.NewGuid()}")
                .Options);

        var property = context.Model
            .FindEntityType(typeof(Subscription))!
            .FindProperty(nameof(Subscription.BillingSource))!;

        Assert.That(
            property.IsNullable,
            Is.False,
            "the database must reject a subscription with no billing source");
    }

    [Test]
    public void NewSubscription_DefaultsToPayPal()
    {
        // Second line of defence behind the database constraint: an insert path that forgets to
        // set a provider stores a real one rather than NULL. PayPal because it was the only
        // provider when the column was introduced.
        Assert.That(new Subscription().BillingSource, Is.EqualTo(BillingSources.PayPal));
    }
}
