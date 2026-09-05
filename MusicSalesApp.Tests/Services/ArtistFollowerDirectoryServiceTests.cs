using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The creator-facing follower list. These are the tests that hold the core privacy promise, so
/// several of them assert on what is absent rather than on what is returned.
/// </summary>
[TestFixture]
public class ArtistFollowerDirectoryServiceTests
{
    private ArtistFollowTestHarness _harness;
    private ArtistFollowService _followService;
    private ArtistFollowerMessageService _messageService;
    private ArtistFollowerDirectoryService _service;

    [SetUp]
    public void SetUp()
    {
        _harness = new ArtistFollowTestHarness();

        var identity = new ArtistFollowerIdentityService(new Random(7));

        _followService = new ArtistFollowService(
            _harness.ContextFactory.Object, identity, Mock.Of<ILogger<ArtistFollowService>>());

        var email = new Mock<IEmailService>();
        email.Setup(x => x.GetAppBaseUrl()).Returns("https://streamtunes.net");
        email.Setup(x => x.GetEmailLogoHtml()).Returns("<img/>");

        _messageService = new ArtistFollowerMessageService(
            _harness.ContextFactory.Object, email.Object, Mock.Of<ILogger<ArtistFollowerMessageService>>());

        _service = new ArtistFollowerDirectoryService(_harness.ContextFactory.Object, identity);
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [Test]
    public async Task GetFollowers_ShowsAPseudonymAndNothingElseAboutThePerson()
    {
        await _followService.SetFollowStateAsync(
            _harness.PersonaId, _harness.ListenerUserId, true, _harness.SongId);

        var followers = await _service.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId);

        Assert.That(followers, Is.Not.Null);
        Assert.That(followers, Has.Count.EqualTo(1));

        // Serialising the whole graph is the point: it catches a leak anywhere in the object,
        // including one added later by someone extending the DTO.
        var serialised = JsonSerializer.Serialize(followers);

        Assert.Multiple(() =>
        {
            Assert.That(followers[0].AnonymousDisplayName, Does.StartWith("Listener #"));
            Assert.That(followers[0].SourceSongTitle, Is.EqualTo("Midnight Highway"));

            Assert.That(serialised, Does.Not.Contain("listener@test.com"));
            Assert.That(serialised, Does.Not.Contain("@test.com"));
            Assert.That(
                serialised,
                Does.Not.Contain($"\"{_harness.ListenerUserId}\""),
                "The listener's user id must not appear anywhere in the result.");
        });
    }

    [Test]
    public void TheCreatorFacingDtoHasNowhereToPutAnIdentity()
    {
        // Structural, not behavioural: a query that tried to leak an email would not compile,
        // which is a stronger guarantee than remembering not to select the column.
        var propertyNames = typeof(ArtistFollowerSummaryDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(propertyNames, Has.None.Contains("Email"));
            Assert.That(propertyNames, Has.None.Contains("UserName"));
            Assert.That(propertyNames, Has.None.Contains("ListenerUserId"));
            Assert.That(propertyNames, Does.Contain("AnonymousDisplayName"));
        });
    }

    [Test]
    public async Task GetFollowers_ReturnsNullForAPersonaTheCreatorDoesNotOwn()
    {
        // Null rather than an empty list: "not yours" and "nobody follows you" are different
        // answers, and a page that confused them would silently show a stranger's empty grid.
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var followers = await _service.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId + 999);

        Assert.That(followers, Is.Null);
    }

    [Test]
    public async Task GetFollowers_ExcludesUnfollowedAndBlockedRelationships()
    {
        var second = _harness.AddListener("second@test.com");
        var third = _harness.AddListener("third@test.com");

        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _followService.SetFollowStateAsync(_harness.PersonaId, second, true);
        await _followService.SetFollowStateAsync(_harness.PersonaId, third, true);

        await _followService.SetFollowStateAsync(_harness.PersonaId, second, false);
        await _followService.SetBlockedAsync(_harness.PersonaId, third, true);

        var followers = await _service.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId);

        Assert.That(followers, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetFollowers_ReportsWhetherEachFollowerHasBeenThanked()
    {
        var second = _harness.AddListener("second@test.com");

        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _followService.SetFollowStateAsync(_harness.PersonaId, second, true);

        int thankedFollowerId;
        await using (var context = _harness.NewContext())
        {
            thankedFollowerId = (await context.ArtistFollowers
                .SingleAsync(follow => follow.ListenerUserId == _harness.ListenerUserId)).Id;
        }

        await _messageService.SendThankYouAsync(thankedFollowerId, _harness.CreatorId, "Thanks!");

        var followers = await _service.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId);

        var thanked = followers!.Single(follower => follower.ArtistFollowerId == thankedFollowerId);
        var notThanked = followers!.Single(follower => follower.ArtistFollowerId != thankedFollowerId);

        Assert.Multiple(() =>
        {
            Assert.That(thanked.HasBeenThanked, Is.True);
            Assert.That(thanked.LastMessageText, Is.EqualTo("Thanks!"));
            Assert.That(thanked.LastMessageDateUtc, Is.Not.Null);

            Assert.That(notThanked.HasBeenThanked, Is.False);
            Assert.That(notThanked.LastMessageText, Is.Null);
            Assert.That(notThanked.LastMessageDateUtc, Is.Null);
        });
    }

    [Test]
    public async Task GetFollowers_KeepsThePseudonymStableAcrossAnUnfollowAndRefollow()
    {
        // The creator is meant to be able to tell that several interactions came from one person,
        // and that has to survive the listener leaving and coming back.
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        var before = (await _service.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId))![0];

        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, false);
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var after = (await _service.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId))![0];

        Assert.That(after.AnonymousDisplayName, Is.EqualTo(before.AnonymousDisplayName));
    }

    [Test]
    public async Task TwoArtistsSeeTheSameListenerUnderDifferentPseudonyms()
    {
        // Without this, two creators could compare follower lists and work out that one person
        // follows them both - which is the cross-artist correlation the design exists to prevent.
        int secondPersonaId;
        await using (var context = _harness.NewContext())
        {
            var otherUser = new ApplicationUser { UserName = "other@test.com", Email = "other@test.com" };
            context.Users.Add(otherUser);
            await context.SaveChangesAsync();

            var otherCreator = new Creator { UserId = otherUser.Id, DisplayName = "Other", IsActive = true };
            context.Creators.Add(otherCreator);
            await context.SaveChangesAsync();

            var otherPersona = new CreatorPersona
            {
                CreatorId = otherCreator.Id,
                Name = "Jane Echo",
                IsEnabled = true,
            };
            context.CreatorPersonas.Add(otherPersona);
            await context.SaveChangesAsync();
            secondPersonaId = otherPersona.Id;
        }

        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);
        await _followService.SetFollowStateAsync(secondPersonaId, _harness.ListenerUserId, true);

        await using var verify = _harness.NewContext();
        var numbers = await verify.ArtistFollowers
            .Where(follow => follow.ListenerUserId == _harness.ListenerUserId)
            .Select(follow => follow.AnonymousListenerNumber)
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(numbers, Has.Count.EqualTo(2));
            Assert.That(numbers[0], Is.Not.EqualTo(numbers[1]));
        });
    }

    [Test]
    public async Task OwnsPersona_DistinguishesTheOwnerFromEveryoneElse()
    {
        Assert.Multiple(async () =>
        {
            Assert.That(await _service.OwnsPersonaAsync(_harness.PersonaId, _harness.CreatorId), Is.True);
            Assert.That(await _service.OwnsPersonaAsync(_harness.PersonaId, _harness.CreatorId + 999), Is.False);
        });
    }
}
