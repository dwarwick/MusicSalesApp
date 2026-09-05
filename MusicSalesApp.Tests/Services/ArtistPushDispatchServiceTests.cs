using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The dispatcher's stamping rules, which are where this feature can actually lose or duplicate a
/// notification. A settled row must never be sent again; a deferred row must never be forgotten.
/// </summary>
[TestFixture]
public class ArtistPushDispatchServiceTests
{
    private ArtistFollowTestHarness _harness;
    private FakePushSender _sender;
    private ArtistFollowService _followService;
    private ArtistFollowerMessageService _messageService;
    private ArtistReleaseNotificationService _releaseService;
    private PushDeviceTokenService _deviceTokenService;
    private Mock<IAppSettingsService> _appSettings;
    private ArtistPushDispatchService _service;

    [SetUp]
    public void SetUp()
    {
        _harness = new ArtistFollowTestHarness();
        _sender = new FakePushSender();

        var email = new Mock<IEmailService>();
        email.Setup(x => x.GetAppBaseUrl()).Returns("https://streamtunes.net");
        email.Setup(x => x.GetEmailLogoHtml()).Returns("<img/>");
        email.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _followService = new ArtistFollowService(
            _harness.ContextFactory.Object,
            new ArtistFollowerIdentityService(new Random(11)),
            Mock.Of<ILogger<ArtistFollowService>>());

        _messageService = new ArtistFollowerMessageService(
            _harness.ContextFactory.Object, email.Object, Mock.Of<ILogger<ArtistFollowerMessageService>>());

        _releaseService = new ArtistReleaseNotificationService(
            _harness.ContextFactory.Object, email.Object, Mock.Of<ILogger<ArtistReleaseNotificationService>>());

        _deviceTokenService = new PushDeviceTokenService(
            _harness.ContextFactory.Object, Mock.Of<ILogger<PushDeviceTokenService>>());

        _appSettings = new Mock<IAppSettingsService>();
        _appSettings.Setup(x => x.IsPushNotificationsEnabledAsync()).ReturnsAsync(true);

        _service = new ArtistPushDispatchService(
            _harness.ContextFactory.Object,
            _deviceTokenService,
            _sender,
            _appSettings.Object,
            Mock.Of<ILogger<ArtistPushDispatchService>>());
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    private async Task<int> FollowWithDeviceAsync()
    {
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true, _harness.SongId);
        await _deviceTokenService.RegisterAsync(_harness.ListenerUserId, PushPlatforms.Android, "token-abc", "device-1");

        // Opt the listener in explicitly. Both push preferences default to OFF, so "has a
        // registered device" is not by itself enough to be sent anything - the listener has to
        // have asked. Every test below that expects a delivery depends on this line, and the two
        // that expect silence turn one of them back off to say so.
        await using (var optIn = _harness.NewContext())
        {
            var user = await optIn.Users.SingleAsync(u => u.Id == _harness.ListenerUserId);
            user.ReceiveArtistReleasePush = true;
            user.ReceiveArtistMessagePush = true;
            await optIn.SaveChangesAsync();
        }

        await using var context = _harness.NewContext();
        return (await context.ArtistFollowers.SingleAsync()).Id;
    }

    // ------------------------------------------------------------ releases

    [Test]
    public async Task Dispatch_PushesANewRelease()
    {
        await FollowWithDeviceAsync();
        _harness.AddSong("Ocean Road");
        await _releaseService.CreatePendingNotificationsAsync();

        var delivered = await _service.DispatchPendingAsync();

        await using var context = _harness.NewContext();
        var notification = await context.ArtistReleaseNotifications.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.EqualTo(1));
            Assert.That(notification.PushSentDateUtc, Is.Not.Null);
            Assert.That(_sender.Sent, Has.Count.EqualTo(1));
            Assert.That(_sender.Sent[0].Message.Title, Is.EqualTo("New music from Alex Rivers"));
            Assert.That(_sender.Sent[0].Message.Body, Does.Contain("Ocean Road"));
        });
    }

    [Test]
    public async Task Dispatch_CarriesTheDataTheClientNeedsToRouteATap()
    {
        await FollowWithDeviceAsync();
        var songId = _harness.AddSong("Ocean Road");
        await _releaseService.CreatePendingNotificationsAsync();

        await _service.DispatchPendingAsync();

        var data = _sender.Sent[0].Message.Data;

        Assert.Multiple(() =>
        {
            Assert.That(data[PushDataKeys.Kind], Is.EqualTo(PushNotificationKinds.Release));
            Assert.That(data[PushDataKeys.SongId], Is.EqualTo(songId.ToString()));
            Assert.That(data[PushDataKeys.PersonaId], Is.EqualTo(_harness.PersonaId.ToString()));
            Assert.That(data.ContainsKey(PushDataKeys.EntityId), Is.True);
        });
    }

    [Test]
    public async Task Dispatch_NeverSendsTheSameNotificationTwice()
    {
        await FollowWithDeviceAsync();
        _harness.AddSong("Ocean Road");
        await _releaseService.CreatePendingNotificationsAsync();

        await _service.DispatchPendingAsync();
        var second = await _service.DispatchPendingAsync();

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.Zero);
            Assert.That(_sender.Sent, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Dispatch_SkipsAListenerWhoTurnedPushOffButStopsReconsideringThem()
    {
        await FollowWithDeviceAsync();

        await using (var context = _harness.NewContext())
        {
            var user = await context.Users.SingleAsync(u => u.Id == _harness.ListenerUserId);
            user.ReceiveArtistReleasePush = false;
            await context.SaveChangesAsync();
        }

        _harness.AddSong("Ocean Road");
        await _releaseService.CreatePendingNotificationsAsync();

        var delivered = await _service.DispatchPendingAsync();

        await using var verify = _harness.NewContext();
        var notification = await verify.ArtistReleaseNotifications.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.Zero);
            Assert.That(_sender.Sent, Is.Empty);

            // Stamped anyway, or the job re-examines this row every five minutes forever.
            Assert.That(notification.PushSentDateUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Dispatch_SettlesAListenerWithNoRegisteredDevice()
    {
        // Wants push, has no phone. There is nothing to wait for, so the row must not sit pending.
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        _harness.AddSong("Ocean Road");
        await _releaseService.CreatePendingNotificationsAsync();

        await _service.DispatchPendingAsync();

        await using var context = _harness.NewContext();

        Assert.That((await context.ArtistReleaseNotifications.SingleAsync()).PushSentDateUtc, Is.Not.Null);
    }

    [Test]
    public async Task Dispatch_DoesNotAnnounceASongThatHasSinceBeenPulled()
    {
        await FollowWithDeviceAsync();
        var songId = _harness.AddSong("Ocean Road");
        await _releaseService.CreatePendingNotificationsAsync();

        await using (var context = _harness.NewContext())
        {
            var song = await context.SongMetadata.SingleAsync(s => s.Id == songId);
            song.IsEnabled = false;
            await context.SaveChangesAsync();
        }

        var delivered = await _service.DispatchPendingAsync();

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.Zero);
            Assert.That(_sender.Sent, Is.Empty);
        });
    }

    // ------------------------------------------------------------ retry semantics

    [Test]
    public async Task Dispatch_LeavesARowPendingWhenTheTransportFails()
    {
        // The whole reason PushSentDateUtc is a timestamp rather than a bool. A Firebase outage
        // must defer the notification, not consume it.
        await FollowWithDeviceAsync();
        _harness.AddSong("Ocean Road");
        await _releaseService.CreatePendingNotificationsAsync();

        _sender.NextOutcome = PushDeliveryOutcome.TransportFailure;
        await _service.DispatchPendingAsync();

        await using (var context = _harness.NewContext())
        {
            Assert.That(
                (await context.ArtistReleaseNotifications.SingleAsync()).PushSentDateUtc,
                Is.Null,
                "A transport failure must not settle the row.");
        }

        _sender.NextOutcome = PushDeliveryOutcome.Delivered;
        var delivered = await _service.DispatchPendingAsync();

        await using var verify = _harness.NewContext();
        var retried = await verify.ArtistReleaseNotifications.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.EqualTo(1), "The retry must deliver it.");
            Assert.That(retried.PushSentDateUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Dispatch_RetiresATokenThePlatformRejectsAndSettlesTheRow()
    {
        // A rejected token can never succeed, so retrying it is pure waste - and left alone it
        // fails every run forever.
        await FollowWithDeviceAsync();
        _harness.AddSong("Ocean Road");
        await _releaseService.CreatePendingNotificationsAsync();

        _sender.NextOutcome = PushDeliveryOutcome.TokenRejected;
        await _service.DispatchPendingAsync();

        await using var context = _harness.NewContext();
        var device = await context.PushDeviceTokens.SingleAsync();
        var notification = await context.ArtistReleaseNotifications.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(device.IsActive, Is.False);
            Assert.That(device.DeactivationReason, Is.Not.Null);
            Assert.That(notification.PushSentDateUtc, Is.Not.Null, "A rejection settles the row.");
        });
    }

    [Test]
    public async Task Dispatch_DoesNothingAndSettlesNothingWhenNoTransportIsConfigured()
    {
        // Unconfigured must behave like a transport failure, not like a delivery. Otherwise
        // deploying push before its credentials silently consumes the first days of notifications.
        _sender.Configured = false;

        await FollowWithDeviceAsync();
        _harness.AddSong("Ocean Road");
        await _releaseService.CreatePendingNotificationsAsync();

        var delivered = await _service.DispatchPendingAsync();

        await using var context = _harness.NewContext();
        var notification = await context.ArtistReleaseNotifications.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.Zero);
            Assert.That(notification.PushSentDateUtc, Is.Null);
        });
    }

    [Test]
    public async Task Dispatch_SendsNothingWhileTheAdminFlagIsOff()
    {
        // The release switch. Until push is proven and the apps are in the stores there is nothing
        // on the other end, so the dispatcher must not even try.
        _appSettings.Setup(x => x.IsPushNotificationsEnabledAsync()).ReturnsAsync(false);

        await FollowWithDeviceAsync();
        _harness.AddSong("Ocean Road");
        await _releaseService.CreatePendingNotificationsAsync();

        var delivered = await _service.DispatchPendingAsync();

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.Zero);
            Assert.That(_sender.Sent, Is.Empty);
        });
    }

    [Test]
    public async Task Dispatch_QueuesRatherThanDiscardsWhileTheFlagIsOff()
    {
        // Nothing is lost while push is switched off - the rows stay unstamped, exactly as they do
        // for an unconfigured transport, so turning it on later delivers the backlog.
        _appSettings.Setup(x => x.IsPushNotificationsEnabledAsync()).ReturnsAsync(false);

        await FollowWithDeviceAsync();
        _harness.AddSong("Ocean Road");
        await _releaseService.CreatePendingNotificationsAsync();
        await _service.DispatchPendingAsync();

        await using (var context = _harness.NewContext())
        {
            Assert.That(
                (await context.ArtistReleaseNotifications.SingleAsync()).PushSentDateUtc,
                Is.Null,
                "A switched-off flag must not consume the notification.");
        }

        _appSettings.Setup(x => x.IsPushNotificationsEnabledAsync()).ReturnsAsync(true);

        Assert.That(await _service.DispatchPendingAsync(), Is.EqualTo(1));
    }

    // ------------------------------------------------------------ messages

    [Test]
    public async Task Dispatch_PushesAThankYou()
    {
        var followerId = await FollowWithDeviceAsync();
        await _messageService.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks for the support!");

        var delivered = await _service.DispatchPendingAsync();

        await using var context = _harness.NewContext();
        var message = await context.ArtistFollowerMessages.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.EqualTo(1));
            Assert.That(message.PushSentDateUtc, Is.Not.Null);
            Assert.That(_sender.Sent[0].Message.Title, Is.EqualTo("Alex Rivers sent you a message"));
            Assert.That(_sender.Sent[0].Message.Body, Is.EqualTo("Thanks for the support!"));
        });
    }

    [Test]
    public async Task Dispatch_DoesNotPushAMessageFromAMutedArtist()
    {
        var followerId = await FollowWithDeviceAsync();
        await _messageService.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");

        await _followService.SetArtistNotificationPreferencesAsync(
            _harness.PersonaId, _harness.ListenerUserId, null, artistMessagesEnabled: false);

        var delivered = await _service.DispatchPendingAsync();

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.Zero);
            Assert.That(_sender.Sent, Is.Empty);
        });
    }

    [Test]
    public async Task Dispatch_NeverPutsAnAddressIntoTheNotificationShade()
    {
        // The message text goes straight onto a lock screen, where nobody would think to check it.
        // ArtistMessageContentPolicy is what makes that safe, and this is the test that says the
        // dispatcher relies on it rather than re-sanitising.
        var followerId = await FollowWithDeviceAsync();

        var refused = await _messageService.SendThankYouAsync(
            followerId, _harness.CreatorId, "thanks! reach me at dave@gmail.com");

        await _service.DispatchPendingAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refused.Outcome, Is.EqualTo(ArtistThankYouOutcome.ContentRejected));
            Assert.That(_sender.Sent, Is.Empty, "Nothing was ever stored, so nothing can be pushed.");
        });
    }

    /// <summary>
    /// A sender that records what it was asked to deliver and answers with a configurable outcome.
    /// One instance covers both platforms, matching the single-transport design.
    /// </summary>
    private sealed class FakePushSender : IPushNotificationSender
    {
        public bool Configured { get; set; } = true;

        public bool IsConfigured => Configured;

        public PushDeliveryOutcome NextOutcome { get; set; } = PushDeliveryOutcome.Delivered;

        public List<(PushMessage Message, IReadOnlyList<string> Tokens)> Sent { get; } = [];

        public Task<IReadOnlyList<PushDeliveryResult>> SendAsync(
            PushMessage message,
            IReadOnlyList<string> tokens,
            CancellationToken cancellationToken = default)
        {
            Sent.Add((message, tokens));

            IReadOnlyList<PushDeliveryResult> results = tokens
                .Select(token => new PushDeliveryResult(token, NextOutcome))
                .ToList();

            return Task.FromResult(results);
        }
    }
}
