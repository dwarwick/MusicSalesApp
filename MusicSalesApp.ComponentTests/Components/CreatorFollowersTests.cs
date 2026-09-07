using Bunit;
using Moq;
using MusicSalesApp.Components.Pages.Creator;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// The creator's follower page. The privacy assertions here are on the RENDERED MARKUP, which is
/// the last place a leak could appear even if every service behaved - a template that helpfully
/// printed something extra would show up here and nowhere else.
/// </summary>
[TestFixture]
public class CreatorFollowersTests : BUnitTestBase
{
    private const int UserId = 5;
    private const int CreatorId = 12;
    private const int PersonaId = 30;

    [Test]
    public void CreatorFollowers_ShowsPseudonymsAndNoContactDetails()
    {
        SetupCreator(
            new ArtistFollowerSummaryDto(
                ArtistFollowerId: 1,
                DisplayName: "Listener #4817",
                IsIdentifiedArtist: false,
                FollowedDateUtc: new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc),
                SourceSongMetadataId: 99,
                SourceSongTitle: "Midnight Highway",
                HasBeenThanked: false,
                LastMessageDateUtc: null,
                LastMessageText: null));

        var cut = TestContext.Render<CreatorFollowers>();
        cut.WaitForState(() => cut.Markup.Contains("Listener #4817"), TimeSpan.FromSeconds(5));

        var markup = cut.Markup;

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("Listener #4817"));
            Assert.That(markup, Does.Contain("Midnight Highway"));

            // The three things a creator must never be able to read off this page.
            Assert.That(markup, Does.Not.Contain("@"), "No email address may appear in the markup.");
            Assert.That(markup, Does.Not.Contain("listener@"));
            Assert.That(markup.ToLowerInvariant(), Does.Not.Contain("username"));
        });
    }

    [Test]
    public void CreatorFollowers_OffersAThankYouOnlyToSomeoneNotYetThanked()
    {
        SetupCreator(
            new ArtistFollowerSummaryDto(1, "Listener #4817", false, DateTime.UtcNow, null, null, false, null, null),
            new ArtistFollowerSummaryDto(2, "Listener #3012", false, DateTime.UtcNow, null, null, true, DateTime.UtcNow, "Thanks!"));

        var cut = TestContext.Render<CreatorFollowers>();
        cut.WaitForState(() => cut.Markup.Contains("Listener #4817"), TimeSpan.FromSeconds(5));

        // One per relationship, ever - so the already-thanked row has no button to press.
        Assert.That(cut.Markup.Split("Send Thank You").Length - 1, Is.EqualTo(1));
    }

    [Test]
    public void CreatorFollowers_TellsAPersonaLessCreatorWhyThereIsNothingHere()
    {
        // Following is artist-level, so a creator with no persona has nothing followable. Saying so
        // is better than an empty grid that reads as "nobody likes you".
        SetupAuthorizedUser(UserId, "creator@test.com");
        MockCreatorService.Setup(x => x.GetCreatorIdForUserAsync(UserId)).ReturnsAsync(CreatorId);
        MockCreatorPersonaService.Setup(x => x.GetPersonasByCreatorIdAsync(CreatorId))
            .ReturnsAsync(new List<CreatorPersona>());
        SetupRendererInfo();

        var cut = TestContext.Render<CreatorFollowers>();
        cut.WaitForState(() => cut.Markup.Contains("no personas"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Create a persona"));
    }

    [Test]
    public void CreatorFollowers_ShowsTheHeadlineAnalytics()
    {
        SetupCreator();

        MockArtistFollowerAnalyticsService
            .Setup(x => x.GetAnalyticsAsync(PersonaId, CreatorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArtistFollowerAnalyticsDto(
                TotalFollowers: 1247,
                NewFollowersThisMonth: 84,
                TopSongsGeneratingFollows: [new FollowSourceSongDto(99, "Midnight Highway", 31)]));

        var cut = TestContext.Render<CreatorFollowers>();
        cut.WaitForState(() => cut.Markup.Contains("1,247"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("1,247"));
            Assert.That(cut.Markup, Does.Contain("+84"));
            Assert.That(cut.Markup, Does.Contain("Midnight Highway"));
        });
    }

    [Test]
    public void CreatorFollowers_DoesNotBorrowTheChecklistStyleForTheSongRanking()
    {
        // .terms-list draws a checkmark ::before at left: 0 and relies on the li's padding-left to
        // make room for it. This list overrides that padding with its own shorthand, so the
        // checkmark landed on top of the song title. It is the wrong list anyway: a ranking of
        // songs is not a list of things you get.
        SetupCreator();

        MockArtistFollowerAnalyticsService
            .Setup(x => x.GetAnalyticsAsync(PersonaId, CreatorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArtistFollowerAnalyticsDto(
                TotalFollowers: 3,
                NewFollowersThisMonth: 1,
                TopSongsGeneratingFollows: [new FollowSourceSongDto(99, "Midnight Highway", 31)]));

        var cut = TestContext.Render<CreatorFollowers>();
        cut.WaitForState(() => cut.Markup.Contains("Midnight Highway"), TimeSpan.FromSeconds(5));

        var list = cut.Find(".follower-source-list");

        Assert.That(
            list.ClassList,
            Does.Not.Contain("terms-list"),
            "the checkmark bullet comes with that class and overlaps the song title");
    }

    private void SetupCreator(params ArtistFollowerSummaryDto[] followers)
    {
        SetupAuthorizedUser(UserId, "creator@test.com");
        MockCreatorService.Setup(x => x.GetCreatorIdForUserAsync(UserId)).ReturnsAsync(CreatorId);
        MockCreatorPersonaService.Setup(x => x.GetPersonasByCreatorIdAsync(CreatorId))
            .ReturnsAsync([new CreatorPersona { Id = PersonaId, CreatorId = CreatorId, Name = "Alex Rivers", IsEnabled = true }]);

        MockArtistFollowerDirectoryService
            .Setup(x => x.GetFollowersAsync(PersonaId, CreatorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(followers.ToList());

        MockArtistFollowerAnalyticsService
            .Setup(x => x.GetAnalyticsAsync(PersonaId, CreatorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArtistFollowerAnalyticsDto(followers.Length, 0, []));

        SetupRendererInfo();
    }
}
