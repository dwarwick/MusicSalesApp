using Bunit;
using Moq;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Models;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class FollowArtistButtonTests : BUnitTestBase
{
    private const int UserId = 5;
    private const int PersonaId = 30;

    [Test]
    public void FollowArtistButton_RendersNothingWithoutAPersona()
    {
        // A song whose artist name came from free text or a creator display name has no artist
        // entity, so there is genuinely nothing to follow. An inert button would be worse than
        // none: it invites a click that can never work.
        SetupAuthorizedUser(UserId);
        SetupRendererInfo();

        var cut = TestContext.Render<FollowArtistButton>(parameters => parameters
            .Add(p => p.CreatorPersonaId, 0)
            .Add(p => p.PersonaName, "Alex Rivers"));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void FollowArtistButton_ShowsFollowWhenNotYetFollowing()
    {
        SetupFollowState(isFollowing: false);

        var cut = TestContext.Render<FollowArtistButton>(parameters => parameters
            .Add(p => p.CreatorPersonaId, PersonaId)
            .Add(p => p.PersonaName, "Alex Rivers"));

        cut.WaitForState(() => cut.Markup.Contains("Follow"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Follow"));
            Assert.That(cut.Markup, Does.Not.Contain("Following ✓"));
        });
    }

    [Test]
    public void FollowArtistButton_ShowsFollowingWhenAlreadyFollowing()
    {
        SetupFollowState(isFollowing: true);

        var cut = TestContext.Render<FollowArtistButton>(parameters => parameters
            .Add(p => p.CreatorPersonaId, PersonaId)
            .Add(p => p.PersonaName, "Alex Rivers"));

        cut.WaitForState(() => cut.Markup.Contains("Following"), TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Following ✓"));
    }

    [Test]
    public void FollowArtistButton_ShowsTheFollowerCountOnlyWhenAsked()
    {
        SetupFollowState(isFollowing: false, followerCount: 1247);

        var withCount = TestContext.Render<FollowArtistButton>(parameters => parameters
            .Add(p => p.CreatorPersonaId, PersonaId)
            .Add(p => p.PersonaName, "Alex Rivers")
            .Add(p => p.ShowFollowerCount, true));

        withCount.WaitForState(() => withCount.Markup.Contains("K followers"), TimeSpan.FromSeconds(5));

        Assert.That(withCount.Markup, Does.Contain("1.2K followers"));
    }

    [Test]
    public void FollowArtistButton_KeepsTheButtonHonestWhenTheServerRefuses()
    {
        // The state comes from the OUTCOME, never from what was clicked. An artist who has since
        // been suspended must leave the button reading Follow, not flip it to Following and lie.
        SetupFollowState(isFollowing: false);

        MockArtistFollowService
            .Setup(x => x.SetFollowStateAsync(
                PersonaId, UserId, true, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ArtistFollowOutcome.ArtistUnavailable);

        var cut = TestContext.Render<FollowArtistButton>(parameters => parameters
            .Add(p => p.CreatorPersonaId, PersonaId)
            .Add(p => p.PersonaName, "Alex Rivers"));

        cut.WaitForState(() => cut.Markup.Contains("Follow"), TimeSpan.FromSeconds(5));
        cut.Find("button").Click();

        Assert.That(cut.Markup, Does.Not.Contain("Following ✓"));
    }

    [Test]
    public void FollowArtistButton_RecordsTheSongThatPromptedTheFollow()
    {
        SetupFollowState(isFollowing: false);

        MockArtistFollowService
            .Setup(x => x.SetFollowStateAsync(
                PersonaId, UserId, true, 99, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ArtistFollowOutcome.Followed);

        var cut = TestContext.Render<FollowArtistButton>(parameters => parameters
            .Add(p => p.CreatorPersonaId, PersonaId)
            .Add(p => p.PersonaName, "Alex Rivers")
            .Add(p => p.SourceSongMetadataId, 99));

        cut.WaitForState(() => cut.Markup.Contains("Follow"), TimeSpan.FromSeconds(5));
        cut.Find("button").Click();

        MockArtistFollowService.Verify(
            x => x.SetFollowStateAsync(PersonaId, UserId, true, 99, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void FollowArtistButton_AsksNothingWhenTheHostAlreadyKnows()
    {
        // The invariant behind KnownIsFollowing. The music library renders one of these per card,
        // so a self-resolving instance would be one database round trip per card on every load -
        // the same trap KnownHasStreamed exists to avoid on LikeDislikeButtons.
        SetupFollowState(isFollowing: false);

        var cut = TestContext.Render<FollowArtistButton>(parameters => parameters
            .Add(p => p.CreatorPersonaId, PersonaId)
            .Add(p => p.PersonaName, "Alex Rivers")
            .Add(p => p.KnownIsFollowing, true));

        cut.WaitForState(() => cut.Markup.Contains("Following"), TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Following ✓"), "The supplied state is authoritative.");
            MockArtistFollowService.Verify(
                x => x.IsFollowingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
                Times.Never);
        });
    }

    private void SetupFollowState(bool isFollowing, int followerCount = 0)
    {
        SetupAuthorizedUser(UserId);
        SetupRendererInfo();

        MockArtistFollowService
            .Setup(x => x.IsFollowingAsync(PersonaId, UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(isFollowing);

        MockArtistFollowService
            .Setup(x => x.GetFollowerCountAsync(PersonaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(followerCount);
    }
}
