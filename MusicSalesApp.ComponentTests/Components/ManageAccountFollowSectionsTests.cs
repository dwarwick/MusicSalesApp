using Bunit;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// The two listener-facing sections inside Manage Account.
/// </summary>
[TestFixture]
public class ManageAccountFollowSectionsTests : BUnitTestBase
{
    private const int UserId = 5;
    private const int PersonaId = 30;

    [SetUp]
    public void SetUpSections()
    {
        SetupAuthorizedUser(UserId);
        SetupRendererInfo();
    }

    // ------------------------------------------------------------ following

    [Test]
    public void FollowedArtists_ListsTheArtistWithItsLatestReleaseAndUnreadCount()
    {
        MockArtistFollowService
            .Setup(x => x.GetFollowedArtistsAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildArtist(unreadMessageCount: 2)]);

        var cut = TestContext.Render<FollowedArtistsSection>(p => p.Add(c => c.UserId, UserId));
        cut.WaitForState(() => cut.Markup.Contains("Alex Rivers"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Alex Rivers"));
            Assert.That(cut.Markup, Does.Contain("Midnight Highway"));
            Assert.That(cut.Markup, Does.Contain("2 unread"));
            Assert.That(cut.Markup, Does.Contain("Unfollow"));
        });
    }

    [Test]
    public void FollowedArtists_ReportsWhichWayAMuteWent()
    {
        // A bare "Saved" leaves the reader unable to tell whether the switch took the new value or
        // the old, and this section is several cards below the page-level banner - so a save that
        // moved nothing on screen would read as a dead button.
        MockArtistFollowService
            .Setup(x => x.GetFollowedArtistsAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildArtist()]);

        MockArtistFollowService
            .Setup(x => x.SetArtistNotificationPreferencesAsync(
                PersonaId, UserId, false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cut = TestContext.Render<FollowedArtistsSection>(p => p.Add(c => c.UserId, UserId));
        cut.WaitForState(() => cut.Markup.Contains("Mute new releases"), TimeSpan.FromSeconds(5));

        cut.FindAll("button").First(button => button.TextContent.Contains("Mute new releases")).Click();
        cut.WaitForState(() => cut.Markup.Contains("muted"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("New releases from Alex Rivers muted."));
    }

    [Test]
    public void FollowedArtists_StillOffersUnblockForABlockedArtist()
    {
        // A blocked artist stays listed on purpose: this section is where the block is undone, so
        // hiding them would make the action unreachable.
        MockArtistFollowService
            .Setup(x => x.GetFollowedArtistsAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildArtist(isBlocked: true)]);

        var cut = TestContext.Render<FollowedArtistsSection>(p => p.Add(c => c.UserId, UserId));
        cut.WaitForState(() => cut.Markup.Contains("Alex Rivers"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Unblock"));
            Assert.That(cut.Markup, Does.Contain("Blocked"));
            Assert.That(cut.Markup, Does.Not.Contain("Mute new releases"),
                "Muting a blocked artist is meaningless - nothing reaches the listener anyway.");
        });
    }

    [Test]
    public void FollowedArtists_PointsAnEmptyListAtTheLibrary()
    {
        MockArtistFollowService
            .Setup(x => x.GetFollowedArtistsAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var cut = TestContext.Render<FollowedArtistsSection>(p => p.Add(c => c.UserId, UserId));
        cut.WaitForState(() => cut.Markup.Contains("not following anyone"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain(AppPageRoutes.MusicLibrary));
    }

    // ------------------------------------------------------------ messages

    [Test]
    public void ArtistMessages_ShowsTheMessageWithoutNamingWhoSentIt()
    {
        // The privacy promise runs both ways. A listener sees the persona and the words, never the
        // creator's account.
        MockArtistFollowerMessageService
            .Setup(x => x.GetMessagesForListenerAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildMessage()]);

        var cut = TestContext.Render<ArtistMessagesSection>(p => p.Add(c => c.UserId, UserId));
        cut.WaitForState(() => cut.Markup.Contains("Thanks for the support!"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Alex Rivers"));
            Assert.That(cut.Markup, Does.Contain("Thanks for the support!"));
            Assert.That(cut.Markup, Does.Not.Contain("@"), "No address may appear in the markup.");
        });
    }

    [Test]
    public void ArtistMessages_OffersNoReplyControl()
    {
        // Version 1 is artist-to-listener only. A two-way channel needs its own moderation and
        // abuse handling on both ends, which is a bigger feature than acknowledging support.
        MockArtistFollowerMessageService
            .Setup(x => x.GetMessagesForListenerAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildMessage()]);

        var cut = TestContext.Render<ArtistMessagesSection>(p => p.Add(c => c.UserId, UserId));
        cut.WaitForState(() => cut.Markup.Contains("Thanks for the support!"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Not.Contain("Reply"));
    }

    [Test]
    public void ArtistMessages_OffersReportAndHideOnAnUnreadMessage()
    {
        MockArtistFollowerMessageService
            .Setup(x => x.GetMessagesForListenerAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildMessage()]);

        var cut = TestContext.Render<ArtistMessagesSection>(p => p.Add(c => c.UserId, UserId));
        cut.WaitForState(() => cut.Markup.Contains("Thanks for the support!"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Mark read"));
            Assert.That(cut.Markup, Does.Contain("Report"));
            Assert.That(cut.Markup, Does.Contain("Hide"));
            Assert.That(cut.Markup, Does.Contain("Unread"));
        });
    }

    [Test]
    public void ArtistMessages_HidesAMessageWhenAsked()
    {
        MockArtistFollowerMessageService
            .Setup(x => x.GetMessagesForListenerAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildMessage()]);

        MockArtistFollowerMessageService
            .Setup(x => x.HideAsync(7, UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cut = TestContext.Render<ArtistMessagesSection>(p => p.Add(c => c.UserId, UserId));
        cut.WaitForState(() => cut.Markup.Contains("Hide"), TimeSpan.FromSeconds(5));

        cut.FindAll("button").First(button => button.TextContent.Contains("Hide")).Click();

        MockArtistFollowerMessageService.Verify(
            x => x.HideAsync(7, UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static FollowedArtistDto BuildArtist(int unreadMessageCount = 0, bool isBlocked = false) =>
        new(
            ArtistFollowerId: 1,
            CreatorPersonaId: PersonaId,
            ArtistName: "Alex Rivers",
            PersonaImageBlobPath: null,
            FollowedDateUtc: new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc),
            LatestReleaseSongMetadataId: 99,
            LatestReleaseTitle: "Midnight Highway",
            LatestReleaseDateUtc: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            ReleaseNotificationsEnabled: true,
            ArtistMessagesEnabled: true,
            IsBlocked: isBlocked,
            UnreadMessageCount: unreadMessageCount);

    private static ArtistMessageDto BuildMessage() =>
        new(
            MessageId: 7,
            CreatorPersonaId: PersonaId,
            ArtistName: "Alex Rivers",
            MessageText: "Thanks for the support!",
            RelatedSongMetadataId: null,
            RelatedSongTitle: null,
            CreatedDateUtc: new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc),
            IsRead: false,
            IsReported: false);
}
