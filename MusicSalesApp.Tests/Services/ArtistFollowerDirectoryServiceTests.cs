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
            Assert.That(followers[0].DisplayName, Does.StartWith("Listener #"));
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
            Assert.That(propertyNames, Does.Contain("DisplayName"));
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

        Assert.That(after.DisplayName, Is.EqualTo(before.DisplayName));
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

    // ------------------------------------------------------ followers who are artists themselves

    /// <summary>
    /// Turns the seeded listener into an active creator, optionally with an enabled persona, and
    /// returns nothing - the point is the state it leaves behind.
    /// </summary>
    private async Task MakeListenerAnArtistAsync(string personaName, string creatorDisplayName)
    {
        await using var context = _harness.NewContext();

        var creator = new Creator
        {
            UserId = _harness.ListenerUserId,
            DisplayName = creatorDisplayName,
            IsActive = true,
        };

        context.Creators.Add(creator);
        await context.SaveChangesAsync();

        if (!string.IsNullOrEmpty(personaName))
        {
            context.CreatorPersonas.Add(new CreatorPersona
            {
                CreatorId = creator.Id,
                Name = personaName,
                IsEnabled = true,
            });

            await context.SaveChangesAsync();
        }
    }

    [Test]
    public async Task GetFollowers_NamesAFollowerWhoIsThemselvesAnArtist()
    {
        // Artist-to-artist discovery: the creator can see who it is and go and hear their music.
        // The persona name is already public on every song card, so nothing new about the ACCOUNT
        // is disclosed - what is new is the association with following.
        await MakeListenerAnArtistAsync(personaName: "Jane Echo", creatorDisplayName: "Jane");
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var followers = await _service.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId);

        Assert.Multiple(() =>
        {
            Assert.That(followers![0].DisplayName, Is.EqualTo("Jane Echo"));
            Assert.That(followers[0].IsIdentifiedArtist, Is.True);
        });
    }

    [Test]
    public async Task GetFollowers_FallsBackToTheCreatorDisplayNameWhenThereIsNoPersona()
    {
        // The same chain a song credit uses: persona first, then the creator display name.
        await MakeListenerAnArtistAsync(personaName: null, creatorDisplayName: "Jane");
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var followers = await _service.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId);

        Assert.Multiple(() =>
        {
            Assert.That(followers![0].DisplayName, Is.EqualTo("Jane"));
            Assert.That(followers[0].IsIdentifiedArtist, Is.True);
        });
    }

    [Test]
    public async Task GetFollowers_NeverFallsBackToTheEmailTheWayASongCreditWould()
    {
        // GetEffectiveArtistName has a third link - the email with the domain stripped - which is
        // fine for a credit the account holder chose to publish under and completely wrong here.
        // A creator with no persona and no display name stays a pseudonym.
        await MakeListenerAnArtistAsync(personaName: null, creatorDisplayName: null);
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var followers = await _service.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId);
        var serialised = JsonSerializer.Serialize(followers);

        Assert.Multiple(() =>
        {
            Assert.That(followers![0].DisplayName, Does.StartWith("Listener #"));
            Assert.That(followers[0].IsIdentifiedArtist, Is.False);
            Assert.That(serialised, Does.Not.Contain("listener"), "No fragment of the address may appear.");
        });
    }

    [Test]
    public async Task GetFollowers_KeepsAnInactiveCreatorAnonymous()
    {
        // Only identities that are publicly live right now are named. Somebody who has stopped
        // being a creator is not publishing under that name any more.
        await MakeListenerAnArtistAsync(personaName: "Jane Echo", creatorDisplayName: "Jane");

        await using (var context = _harness.NewContext())
        {
            var creator = await context.Creators.SingleAsync(c => c.UserId == _harness.ListenerUserId);
            creator.IsActive = false;
            await context.SaveChangesAsync();
        }

        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var followers = await _service.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId);

        Assert.That(followers![0].DisplayName, Does.StartWith("Listener #"));
    }

    [Test]
    public async Task GetFollowers_KeepsASuspendedArtistAnonymous()
    {
        await MakeListenerAnArtistAsync(personaName: "Jane Echo", creatorDisplayName: "Jane");

        await using (var context = _harness.NewContext())
        {
            var user = await context.Users.SingleAsync(u => u.Id == _harness.ListenerUserId);
            user.IsSuspended = true;
            await context.SaveChangesAsync();
        }

        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var followers = await _service.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId);

        Assert.That(followers![0].DisplayName, Does.StartWith("Listener #"));
    }

    [Test]
    public async Task GetFollowers_KeepsAnOrdinaryListenerAnonymous()
    {
        // The common case, and the one the whole feature is built around.
        await _followService.SetFollowStateAsync(_harness.PersonaId, _harness.ListenerUserId, true);

        var followers = await _service.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId);

        Assert.Multiple(() =>
        {
            Assert.That(followers![0].DisplayName, Does.StartWith("Listener #"));
            Assert.That(followers[0].IsIdentifiedArtist, Is.False);
        });
    }
}
