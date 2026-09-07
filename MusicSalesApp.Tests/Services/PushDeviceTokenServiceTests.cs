using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class PushDeviceTokenServiceTests
{
    private ArtistFollowTestHarness _harness;
    private PushDeviceTokenService _service;

    [SetUp]
    public void SetUp()
    {
        _harness = new ArtistFollowTestHarness();
        _service = new PushDeviceTokenService(
            _harness.ContextFactory.Object,
            Mock.Of<ILogger<PushDeviceTokenService>>());
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [Test]
    public async Task Register_StoresTheDevice()
    {
        var registered = await _service.RegisterAsync(
            _harness.ListenerUserId, PushPlatforms.Android, "token-abc", "device-1");

        await using var context = _harness.NewContext();
        var row = await context.PushDeviceTokens.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(registered, Is.True);
            Assert.That(row.UserId, Is.EqualTo(_harness.ListenerUserId));
            Assert.That(row.Platform, Is.EqualTo(PushPlatforms.Android));
            Assert.That(row.IsActive, Is.True);
        });
    }

    [Test]
    public async Task Register_IsIdempotentForTheSameDevice()
    {
        // The client re-registers on every launch and on every auth change, because a token can
        // rotate at any time and re-registering is the only way to notice.
        await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, "token-abc", "device-1");
        await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, "token-abc", "device-1");

        await using var context = _harness.NewContext();

        Assert.That(await context.PushDeviceTokens.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Register_ReplacesARotatedTokenForTheSameInstall()
    {
        // Otherwise the old token lingers as a second row that can never be delivered to, and the
        // dispatcher spends every run failing against it.
        await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, "token-old", "device-1");
        await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, "token-new", "device-1");

        await using var context = _harness.NewContext();
        var row = await context.PushDeviceTokens.SingleAsync();

        Assert.That(row.Token, Is.EqualTo("token-new"));
    }

    [Test]
    public async Task Register_ReassignsATokenThatMovedToAnotherAccount()
    {
        // The privacy case, and the reason Token is uniquely indexed rather than (UserId, Token).
        // A phone handed on, or an account signed out of and another signed in, must not keep
        // delivering the previous person's notifications.
        var second = _harness.AddListener("second@test.com");

        await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, "token-abc", "device-1");
        await _service.RegisterAsync(second, PushPlatforms.Android, "token-abc", "device-1");

        await using var context = _harness.NewContext();
        var row = await context.PushDeviceTokens.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(row.UserId, Is.EqualTo(second));
            Assert.That(row.IsActive, Is.True);
        });
    }

    [Test]
    public async Task Register_ReactivatesADeviceThatWasPreviouslyRetired()
    {
        await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, "token-abc", "device-1");
        await _service.DeactivateAsync(["token-abc"], "Rejected by the push service");

        await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, "token-abc", "device-1");

        await using var context = _harness.NewContext();
        var row = await context.PushDeviceTokens.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(row.IsActive, Is.True);
            Assert.That(row.DeactivatedAtUtc, Is.Null);
            Assert.That(row.DeactivationReason, Is.Null);
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public async Task Register_RefusesAnEmptyToken(string token)
    {
        Assert.That(
            await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, token),
            Is.False);
    }

    [Test]
    public async Task Register_RefusesAnUnknownPlatform()
    {
        // The platform decides which transport is used, and the two are not interchangeable - a
        // wrong value means every send is rejected as malformed.
        Assert.That(
            await _service.RegisterAsync(_harness.ListenerUserId, "Symbian", "token-abc"),
            Is.False);
    }

    [Test]
    public async Task Register_RefusesAnOverLongTokenRatherThanTruncatingIt()
    {
        // A truncated token is accepted and then fails silently forever, which is far harder to
        // diagnose than a rejected registration.
        var tooLong = new string('a', 513);

        Assert.That(
            await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Ios, tooLong),
            Is.False);
    }

    [Test]
    public async Task Register_NormalisesThePlatformCasing()
    {
        await _service.RegisterAsync(_harness.ListenerUserId, "android", "token-abc");

        await using var context = _harness.NewContext();
        var row = await context.PushDeviceTokens.SingleAsync();

        Assert.That(row.Platform, Is.EqualTo(PushPlatforms.Android));
    }

    [Test]
    public async Task Unregister_OnlyTouchesTheCallersOwnDevice()
    {
        // A client must not be able to silence someone else's phone by replaying a token.
        var second = _harness.AddListener("second@test.com");
        await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, "token-abc");

        var byStranger = await _service.UnregisterAsync(second, "token-abc");
        var byOwner = await _service.UnregisterAsync(_harness.ListenerUserId, "token-abc");

        await using var context = _harness.NewContext();
        var row = await context.PushDeviceTokens.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(byStranger, Is.False);
            Assert.That(byOwner, Is.True);
            Assert.That(row.IsActive, Is.False);
        });
    }

    [Test]
    public async Task GetActiveTokens_ExcludesRetiredDevices()
    {
        await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, "live");
        await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Ios, "dead");
        await _service.DeactivateAsync(["dead"], "Unregistered");

        var tokens = await _service.GetActiveTokensAsync([_harness.ListenerUserId]);

        Assert.Multiple(() =>
        {
            Assert.That(tokens[_harness.ListenerUserId], Has.Count.EqualTo(1));
            Assert.That(tokens[_harness.ListenerUserId][0].Token, Is.EqualTo("live"));
        });
    }

    [Test]
    public async Task Deactivate_RecordsWhy()
    {
        // "Why did this user stop getting notifications" is otherwise unanswerable.
        await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, "token-abc");

        await _service.DeactivateAsync(["token-abc"], "Rejected by the push service");

        await using var context = _harness.NewContext();
        var row = await context.PushDeviceTokens.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(row.IsActive, Is.False);
            Assert.That(row.DeactivationReason, Is.EqualTo("Rejected by the push service"));
            Assert.That(row.DeactivatedAtUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task DeletingTheUserTakesTheirDevicesWithThem()
    {
        // Cascade rather than a hand-written delete in AccountDeletionService, which is safe here
        // because PushDeviceToken has exactly one foreign key.
        await _service.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, "token-abc");

        await using (var context = _harness.NewContext())
        {
            var user = await context.Users.SingleAsync(u => u.Id == _harness.ListenerUserId);
            context.Users.Remove(user);
            await context.SaveChangesAsync();
        }

        await using var verify = _harness.NewContext();

        Assert.That(await verify.PushDeviceTokens.AnyAsync(), Is.False);
    }
}
