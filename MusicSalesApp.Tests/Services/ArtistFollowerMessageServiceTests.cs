using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class ArtistFollowerMessageServiceTests
{
    private ArtistFollowTestHarness _harness;
    private Mock<IEmailService> _emailService;
    private ArtistFollowService _followService;
    private ArtistFollowerMessageService _service;

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
            new ArtistFollowerIdentityService(new Random(1)),
            Mock.Of<ILogger<ArtistFollowService>>());

        _service = new ArtistFollowerMessageService(
            _harness.ContextFactory.Object,
            _emailService.Object,
            Mock.Of<ILogger<ArtistFollowerMessageService>>());
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    private async Task<int> FollowAndGetFollowerIdAsync()
    {
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true, _harness.SongId);

        // Email preferences default to OFF, so every test below that expects an email has to have
        // the listener ask for one first. The opted-out test turns it back off to say so.
        await _harness.OptListenerIntoEmailsAsync();

        await using var context = _harness.NewContext();
        return (await context.ArtistFollowers.SingleAsync()).Id;
    }

    [Test]
    public async Task SendThankYou_DeliversTheMessage()
    {
        var followerId = await FollowAndGetFollowerIdAsync();

        var result = await _service.SendThankYouAsync(followerId, _harness.CreatorId, "  Thanks for the support!  ");

        await using var context = _harness.NewContext();
        var message = await context.ArtistFollowerMessages.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(message.MessageText, Is.EqualTo("Thanks for the support!"), "Stored normalised.");
            Assert.That(message.MessageKind, Is.EqualTo(ArtistMessageKinds.ThankYou));
            Assert.That(message.SenderUserId, Is.EqualTo(_harness.CreatorUserId));
            Assert.That(message.ReadDateUtc, Is.Null);
        });
    }

    [Test]
    public async Task SendThankYou_IsAllowedOncePerFollowerEver()
    {
        var followerId = await FollowAndGetFollowerIdAsync();

        await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");
        var second = await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks again!");

        await using var context = _harness.NewContext();

        Assert.Multiple(async () =>
        {
            Assert.That(second.Outcome, Is.EqualTo(ArtistThankYouOutcome.AlreadyThanked));
            Assert.That(await context.ArtistFollowerMessages.CountAsync(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task TheDatabaseRefusesASecondThankYouForTheSameFollower()
    {
        // Going around the service check, to prove the filtered unique index is the real guarantee
        // - the one a double-click on two tabs actually depends on.
        var followerId = await FollowAndGetFollowerIdAsync();
        await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");

        await using var context = _harness.NewContext();
        context.ArtistFollowerMessages.Add(new ArtistFollowerMessage
        {
            ArtistFollowerId = followerId,
            SenderUserId = _harness.CreatorUserId,
            MessageKind = ArtistMessageKinds.ThankYou,
            MessageText = "Sneaking a second one in",
            CreatedDateUtc = DateTime.UtcNow,
        });

        Assert.ThrowsAsync<DbUpdateException>(async () => await context.SaveChangesAsync());
    }

    [Test]
    public async Task SendThankYou_RefusesContactDetails()
    {
        var followerId = await FollowAndGetFollowerIdAsync();

        var result = await _service.SendThankYouAsync(followerId, _harness.CreatorId, "thanks! dave@gmail.com");

        await using var context = _harness.NewContext();

        Assert.Multiple(async () =>
        {
            Assert.That(result.Outcome, Is.EqualTo(ArtistThankYouOutcome.ContentRejected));
            Assert.That(result.RejectionReason, Is.Not.Empty);
            Assert.That(await context.ArtistFollowerMessages.AnyAsync(), Is.False);
        });
    }

    [Test]
    public async Task SendThankYou_RefusesAnotherCreatorsFollower()
    {
        var followerId = await FollowAndGetFollowerIdAsync();

        var result = await _service.SendThankYouAsync(followerId, creatorId: _harness.CreatorId + 999, "Thanks!");

        Assert.That(result.Outcome, Is.EqualTo(ArtistThankYouOutcome.NotPersonaOwner));
    }

    [Test]
    public async Task SendThankYou_RefusesAListenerWhoHasUnfollowed()
    {
        // Unfollowing ends the creator's ability to START anything new. Existing messages stay in
        // the listener's history; this is only about initiating.
        var followerId = await FollowAndGetFollowerIdAsync();
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, false);

        var result = await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");

        Assert.That(result.Outcome, Is.EqualTo(ArtistThankYouOutcome.NotFollowing));
    }

    [Test]
    public async Task SendThankYou_RefusesAListenerWhoHasBlockedTheArtist()
    {
        var followerId = await FollowAndGetFollowerIdAsync();
        await _followService.SetBlockedAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var result = await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");

        Assert.That(result.Outcome, Is.EqualTo(ArtistThankYouOutcome.Blocked));
    }

    [Test]
    public async Task SendThankYou_RefusesAListenerWhoHasMutedArtistMessages()
    {
        var followerId = await FollowAndGetFollowerIdAsync();
        await _followService.SetArtistNotificationPreferencesAsync(
            _harness.PersonaId, _harness.ListenerUserId, null, artistMessagesEnabled: false);

        var result = await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");

        Assert.That(result.Outcome, Is.EqualTo(ArtistThankYouOutcome.Blocked));
    }

    [Test]
    public async Task SendThankYou_RefusesWhenTheCreatorIsSuspended()
    {
        var followerId = await FollowAndGetFollowerIdAsync();

        await using (var context = _harness.NewContext())
        {
            var user = await context.Users.SingleAsync(u => u.Id == _harness.CreatorUserId);
            user.IsSuspended = true;
            await context.SaveChangesAsync();
        }

        var result = await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");

        Assert.That(result.Outcome, Is.EqualTo(ArtistThankYouOutcome.ArtistUnavailable));
    }

    [Test]
    public async Task SendThankYou_OnlyLinksASongThePersonaOwns()
    {
        var followerId = await FollowAndGetFollowerIdAsync();

        int foreignSongId;
        await using (var seed = _harness.NewContext())
        {
            var song = new SongMetadata { SongTitle = "Not Mine", BlobPath = "x.mp3", Mp3BlobPath = "x.mp3" };
            seed.SongMetadata.Add(song);
            await seed.SaveChangesAsync();
            foreignSongId = song.Id;
        }

        await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!", foreignSongId);

        await using var context = _harness.NewContext();
        var message = await context.ArtistFollowerMessages.SingleAsync();

        Assert.That(message.RelatedSongMetadataId, Is.Null);
    }

    [Test]
    public async Task GetMessagesForListener_NeverExposesWhoSentIt()
    {
        // The privacy promise runs both ways. A listener learns which persona wrote to them and
        // nothing about the account behind the alias, so the DTO has nowhere to put it.
        var followerId = await FollowAndGetFollowerIdAsync();
        await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!", _harness.SongId);

        var messages = await _service.GetMessagesForListenerAsync(_harness.ListenerUserId);

        var propertyNames = typeof(ArtistMessageDto).GetProperties().Select(p => p.Name).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.Count.EqualTo(1));
            Assert.That(messages[0].ArtistName, Is.EqualTo("Alex Rivers"));
            Assert.That(messages[0].RelatedSongTitle, Is.EqualTo("Midnight Highway"));
            Assert.That(propertyNames, Has.None.Contains("Sender"));
            Assert.That(propertyNames, Has.None.Contains("Email"));
            Assert.That(propertyNames, Has.None.Contains("UserId"));
        });
    }

    [Test]
    public async Task MarkRead_IsRefusedForSomeoneElsesMessage()
    {
        var followerId = await FollowAndGetFollowerIdAsync();
        await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");

        int messageId;
        await using (var context = _harness.NewContext())
        {
            messageId = (await context.ArtistFollowerMessages.SingleAsync()).Id;
        }

        var stranger = _harness.AddListener("stranger@test.com");

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.MarkReadAsync(messageId, stranger), Is.False);
            Assert.That(await _service.MarkReadAsync(messageId, _harness.ListenerUserId), Is.True);
        });
    }

    [Test]
    public async Task Hide_RemovesItFromTheListenersListWithoutDeletingIt()
    {
        // Not a delete: a reported message has to survive for an admin to look at.
        var followerId = await FollowAndGetFollowerIdAsync();
        await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");

        int messageId;
        await using (var context = _harness.NewContext())
        {
            messageId = (await context.ArtistFollowerMessages.SingleAsync()).Id;
        }

        await _service.HideAsync(messageId, _harness.ListenerUserId);

        await using var verify = _harness.NewContext();

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.GetMessagesForListenerAsync(_harness.ListenerUserId), Is.Empty);
            Assert.That(await verify.ArtistFollowerMessages.CountAsync(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Report_FlagsTheMessageAndRejectsAnUnknownReason()
    {
        var followerId = await FollowAndGetFollowerIdAsync();
        await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");

        int messageId;
        await using (var context = _harness.NewContext())
        {
            messageId = (await context.ArtistFollowerMessages.SingleAsync()).Id;
        }

        var badReason = await _service.ReportAsync(messageId, _harness.ListenerUserId, "Because I said so");
        var goodReason = await _service.ReportAsync(
            messageId, _harness.ListenerUserId, ReportReasonTypes.TermsOfUseViolation);

        await using var verify = _harness.NewContext();
        var message = await verify.ArtistFollowerMessages.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(badReason, Is.False, "Reasons must come from ReportReasonTypes.");
            Assert.That(goodReason, Is.True);
            Assert.That(message.IsReported, Is.True);
            Assert.That(message.ReportReason, Is.EqualTo(ReportReasonTypes.TermsOfUseViolation));
            Assert.That(message.ModerationResolvedAtUtc, Is.Null);
        });
    }

    [Test]
    public async Task SendPendingEmails_SkipsAListenerWhoOptedOutButStopsReconsideringThem()
    {
        var followerId = await FollowAndGetFollowerIdAsync();

        await using (var context = _harness.NewContext())
        {
            var listener = await context.Users.SingleAsync(u => u.Id == _harness.ListenerUserId);
            listener.ReceiveArtistMessageEmails = false;
            await context.SaveChangesAsync();
        }

        await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");

        var sent = await _service.SendPendingEmailsAsync();

        await using var verify = _harness.NewContext();
        var message = await verify.ArtistFollowerMessages.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(sent, Is.Zero);
            _emailService.Verify(
                x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);

            // Stamped anyway, or the job would re-examine this row every 15 minutes forever.
            Assert.That(message.EmailSentDateUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task SendPendingEmails_SendsToAListenerWhoWantsThem()
    {
        var followerId = await FollowAndGetFollowerIdAsync();
        await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks for the support!");

        var sent = await _service.SendPendingEmailsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(sent, Is.EqualTo(1));
            _emailService.Verify(
                x => x.SendEmailAsync(
                    "listener@test.com",
                    It.Is<string>(subject => subject.Contains("Alex Rivers")),
                    It.Is<string>(body => body.Contains("Thanks for the support!"))),
                Times.Once);
        });
    }

    [Test]
    public async Task SendPendingEmails_DoesNotSendTheSameMessageTwice()
    {
        var followerId = await FollowAndGetFollowerIdAsync();
        await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");

        await _service.SendPendingEmailsAsync();
        var second = await _service.SendPendingEmailsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.Zero);
            _emailService.Verify(
                x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Once);
        });
    }

    [Test]
    public async Task SendPendingEmails_NeverPutsTheListenersAddressInTheBody()
    {
        var followerId = await FollowAndGetFollowerIdAsync();
        await _service.SendThankYouAsync(followerId, _harness.CreatorId, "Thanks!");

        string capturedBody = null;
        _emailService
            .Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string>((_, _, body) => capturedBody = body)
            .ReturnsAsync(true);

        await _service.SendPendingEmailsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(capturedBody, Is.Not.Null);
            Assert.That(capturedBody, Does.Not.Contain("creator@test.com"));
            Assert.That(capturedBody, Does.Contain("cannot see your email address"));
        });
    }
}
