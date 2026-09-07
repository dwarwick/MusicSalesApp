using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class ArtistReleaseNotificationServiceTests
{
    private ArtistFollowTestHarness _harness;
    private Mock<IEmailService> _emailService;
    private ArtistFollowService _followService;
    private ArtistReleaseNotificationService _service;

    [SetUp]
    public void SetUp()
    {
        _harness = new ArtistFollowTestHarness();

        _emailService = new Mock<IEmailService>();
        _emailService.Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _emailService.Setup(x => x.GetAppBaseUrl()).Returns("https://streamtunes.net");
        _emailService.Setup(x => x.GetEmailLogoHtml()).Returns("<img/>");

        _followService = new ArtistFollowService(
            _harness.ContextFactory.Object,
            new ArtistFollowerIdentityService(new Random(3)),
            Mock.Of<ILogger<ArtistFollowService>>());

        _service = new ArtistReleaseNotificationService(
            _harness.ContextFactory.Object,
            _emailService.Object,
            Mock.Of<ILogger<ArtistReleaseNotificationService>>());
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [Test]
    public async Task CreatePendingNotifications_NotifiesAFollowerOfANewRelease()
    {
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        // Unstamped, so the job treats it as newly public and stamps it on this run.
        var songId = _harness.AddSong("Ocean Road");

        var created = await _service.CreatePendingNotificationsAsync();

        await using var context = _harness.NewContext();
        var notification = await context.ArtistReleaseNotifications.SingleAsync();
        var song = await context.SongMetadata.SingleAsync(s => s.Id == songId);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.EqualTo(1));
            Assert.That(notification.SongMetadataId, Is.EqualTo(songId));
            Assert.That(notification.ListenerUserId, Is.EqualTo(_harness.ListenerUserId));
            Assert.That(song.FirstPublishedAtUtc, Is.Not.Null, "The job stamps the release time.");
        });
    }

    [Test]
    public async Task CreatePendingNotifications_IsSilentAboutMusicReleasedBeforeTheFollow()
    {
        // The seeded song was published in January; the follow happens now. Notifying here would
        // mean every new follower is greeted with a backlog, and it is also what makes deploying
        // this feature onto a live catalogue safe.
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        _harness.AddSong("Older Track", DateTime.UtcNow.AddDays(-2));

        var created = await _service.CreatePendingNotificationsAsync();

        Assert.That(created, Is.Zero);
    }

    [Test]
    public async Task CreatePendingNotifications_IgnoresReleasesOlderThanTheWindow()
    {
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        _harness.AddSong("Ancient History", DateTime.UtcNow.AddDays(-30));

        Assert.That(await _service.CreatePendingNotificationsAsync(), Is.Zero);
    }

    [Test]
    public async Task CreatePendingNotifications_StampsButDoesNotNotifyForADisabledSong()
    {
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        var draftId = _harness.AddSong("Still A Draft", isEnabled: false);

        var created = await _service.CreatePendingNotificationsAsync();

        await using var context = _harness.NewContext();
        var song = await context.SongMetadata.SingleAsync(s => s.Id == draftId);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.Zero);
            Assert.That(song.FirstPublishedAtUtc, Is.Null, "A disabled song has not been released.");
        });
    }

    [Test]
    public async Task CreatePendingNotifications_DoesNotNotifyAMutedFollower()
    {
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _followService.SetArtistNotificationPreferencesAsync(
            _harness.PersonaId, _harness.ListenerUserId, releaseNotificationsEnabled: false, null);

        _harness.AddSong("Ocean Road");

        Assert.That(await _service.CreatePendingNotificationsAsync(), Is.Zero);
    }

    [Test]
    public async Task CreatePendingNotifications_DoesNotNotifyABlockedOrUnfollowedListener()
    {
        var second = _harness.AddListener("second@test.com");

        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _followService.SetFollowStateAsync(_harness.PersonaId, second, true);

        await _followService.SetBlockedAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _followService.SetFollowStateAsync(_harness.PersonaId, second, false);

        _harness.AddSong("Ocean Road");

        Assert.That(await _service.CreatePendingNotificationsAsync(), Is.Zero);
    }

    [Test]
    public async Task CreatePendingNotifications_IsSilentWhileTheCreatorIsSuspended()
    {
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        _harness.AddSong("Ocean Road");

        await using (var context = _harness.NewContext())
        {
            var user = await context.Users.SingleAsync(u => u.Id == _harness.CreatorUserId);
            user.IsSuspended = true;
            await context.SaveChangesAsync();
        }

        Assert.That(await _service.CreatePendingNotificationsAsync(), Is.Zero);
    }

    [Test]
    public async Task CreatePendingNotifications_RunningTwiceDoesNotNotifyTwice()
    {
        // The job runs hourly, so this is its normal condition rather than an edge case.
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        _harness.AddSong("Ocean Road");

        var first = await _service.CreatePendingNotificationsAsync();
        var second = await _service.CreatePendingNotificationsAsync();

        await using var context = _harness.NewContext();

        Assert.Multiple(async () =>
        {
            Assert.That(first, Is.EqualTo(1));
            Assert.That(second, Is.Zero);
            Assert.That(await context.ArtistReleaseNotifications.CountAsync(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CreatePendingNotifications_DoesNotReStampASongItAlreadyPublished()
    {
        // Editing metadata or replacing artwork must not re-release a song. Nothing here looks at
        // those events; the stamp simply never moves once set.
        _harness.AddSong("Ocean Road");
        await _service.CreatePendingNotificationsAsync();

        DateTime? firstStamp;
        await using (var context = _harness.NewContext())
        {
            firstStamp = (await context.SongMetadata.SingleAsync(s => s.SongTitle == "Ocean Road"))
                .FirstPublishedAtUtc;
        }

        await _service.CreatePendingNotificationsAsync();

        await using var verify = _harness.NewContext();
        var song = await verify.SongMetadata.SingleAsync(s => s.SongTitle == "Ocean Road");

        Assert.That(song.FirstPublishedAtUtc, Is.EqualTo(firstStamp));
    }

    [Test]
    public async Task TheDatabaseRefusesADuplicateNotification()
    {
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        var songId = _harness.AddSong("Ocean Road");
        await _service.CreatePendingNotificationsAsync();

        await using var context = _harness.NewContext();
        context.ArtistReleaseNotifications.Add(new ArtistReleaseNotification
        {
            CreatorPersonaId = _harness.PersonaId,
            SongMetadataId = songId,
            ListenerUserId = _harness.ListenerUserId,
            CreatedDateUtc = DateTime.UtcNow,
        });

        Assert.ThrowsAsync<DbUpdateException>(async () => await context.SaveChangesAsync());
    }

    [Test]
    public async Task GetForListener_MarksAWithdrawnSongAsUnavailable()
    {
        // The song can be pulled after the notification went out. The row stays as history and
        // says so, rather than linking the listener to a page that will not load.
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        var songId = _harness.AddSong("Ocean Road");
        await _service.CreatePendingNotificationsAsync();

        await using (var context = _harness.NewContext())
        {
            var song = await context.SongMetadata.SingleAsync(s => s.Id == songId);
            song.IsEnabled = false;
            await context.SaveChangesAsync();
        }

        var notifications = await _service.GetForListenerAsync(_harness.ListenerUserId);

        Assert.Multiple(() =>
        {
            Assert.That(notifications, Has.Count.EqualTo(1));
            Assert.That(notifications[0].SongIsAvailable, Is.False);
            Assert.That(notifications[0].SongTitle, Is.EqualTo("Ocean Road"));
        });
    }

    [Test]
    public async Task MarkRead_OnlyWorksForTheListenerItBelongsTo()
    {
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        _harness.AddSong("Ocean Road");
        await _service.CreatePendingNotificationsAsync();

        int notificationId;
        await using (var context = _harness.NewContext())
        {
            notificationId = (await context.ArtistReleaseNotifications.SingleAsync()).Id;
        }

        var stranger = _harness.AddListener("stranger@test.com");

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.MarkReadAsync(notificationId, stranger), Is.False);
            Assert.That(await _service.MarkReadAsync(notificationId, _harness.ListenerUserId), Is.True);
            Assert.That(await _service.GetUnreadCountAsync(_harness.ListenerUserId), Is.Zero);
        });
    }

    [Test]
    public async Task SendPendingEmails_SkipsAnOptedOutListenerButStopsReconsideringThem()
    {
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        await using (var context = _harness.NewContext())
        {
            var listener = await context.Users.SingleAsync(u => u.Id == _harness.ListenerUserId);
            listener.ReceiveArtistReleaseEmails = false;
            await context.SaveChangesAsync();
        }

        _harness.AddSong("Ocean Road");
        await _service.CreatePendingNotificationsAsync();

        var sent = await _service.SendPendingEmailsAsync();

        await using var verify = _harness.NewContext();
        var notification = await verify.ArtistReleaseNotifications.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(sent, Is.Zero);
            _emailService.Verify(
                x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
            Assert.That(notification.EmailSentDateUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task SendPendingEmails_SendsOnceAndNamesTheArtistAndSong()
    {
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _harness.OptListenerIntoEmailsAsync();
        _harness.AddSong("Ocean Road");
        await _service.CreatePendingNotificationsAsync();

        var first = await _service.SendPendingEmailsAsync();
        var second = await _service.SendPendingEmailsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(1));
            Assert.That(second, Is.Zero, "A notification must not be emailed twice.");
            _emailService.Verify(
                x => x.SendEmailAsync(
                    "listener@test.com",
                    "New music from Alex Rivers",
                    It.Is<string>(body => body.Contains("Ocean Road"))),
                Times.Once);
        });
    }

    [Test]
    public async Task SendPendingEmails_DoesNotEmailAboutASongThatHasSinceBeenPulled()
    {
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        var songId = _harness.AddSong("Ocean Road");
        await _service.CreatePendingNotificationsAsync();

        await using (var context = _harness.NewContext())
        {
            var song = await context.SongMetadata.SingleAsync(s => s.Id == songId);
            song.IsEnabled = false;
            await context.SaveChangesAsync();
        }

        var sent = await _service.SendPendingEmailsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(sent, Is.Zero);
            _emailService.Verify(
                x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        });
    }
}
