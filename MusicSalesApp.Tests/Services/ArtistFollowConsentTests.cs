using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// Consent to being named to artists you follow, and what withdrawing it does.
/// </summary>
/// <remarks>
/// The listener in the harness is turned into a creator here, so they are both a follower and an
/// artist - which is the whole situation this feature is about.
/// </remarks>
[TestFixture]
public class ArtistFollowConsentTests
{
    private ArtistFollowTestHarness _harness;
    private ArtistFollowService _followService;
    private ArtistFollowerDirectoryService _directory;
    private int _followerCreatorId;
    private int _personaOneId;
    private int _personaTwoId;

    [SetUp]
    public async Task SetUp()
    {
        _harness = new ArtistFollowTestHarness();

        var identity = new ArtistFollowerIdentityService(new Random(99));

        _followService = new ArtistFollowService(
            _harness.ContextFactory.Object, identity, Mock.Of<ILogger<ArtistFollowService>>());

        _directory = new ArtistFollowerDirectoryService(_harness.ContextFactory.Object, identity);

        await using var context = _harness.NewContext();

        var creator = new Creator
        {
            UserId = _harness.ListenerUserId,
            DisplayName = "Jane",
            IsActive = true,
        };

        context.Creators.Add(creator);
        await context.SaveChangesAsync();
        _followerCreatorId = creator.Id;

        var one = new CreatorPersona { CreatorId = creator.Id, Name = "Jane Echo", IsEnabled = true };
        var two = new CreatorPersona { CreatorId = creator.Id, Name = "Night Shift", IsEnabled = true };
        context.CreatorPersonas.AddRange(one, two);
        await context.SaveChangesAsync();

        _personaOneId = one.Id;
        _personaTwoId = two.Id;
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    private async Task SetConsentAsync(bool reveal)
    {
        await using var context = _harness.NewContext();
        var creator = await context.Creators.SingleAsync(c => c.Id == _followerCreatorId);
        creator.RevealPersonaToFollowedArtists = reveal;
        await context.SaveChangesAsync();
    }

    private async Task<string> FollowerNameAsync()
    {
        var followers = await _directory.GetFollowersAsync(_harness.PersonaId, _harness.CreatorId);
        return followers![0].DisplayName;
    }

    // ------------------------------------------------------------ the default

    [Test]
    public async Task WithoutConsent_ACreatorFollowsAnonymously()
    {
        // The default, and the whole point of it being opt-in: being a public artist is not consent
        // to have who you follow disclosed.
        await _followService.SetFollowStateAsync(
            _harness.PersonaId, _harness.ListenerUserId, true, null, _personaOneId);

        Assert.That(await FollowerNameAsync(), Does.StartWith("Listener #"));
    }

    [Test]
    public async Task WithoutConsent_TheChosenPersonaIsNotEvenStored()
    {
        // Refused at the service, not just hidden at display. A client that passed a persona id
        // without consent must not have it quietly recorded against the follow.
        await _followService.SetFollowStateAsync(
            _harness.PersonaId, _harness.ListenerUserId, true, null, _personaOneId);

        await using var context = _harness.NewContext();

        Assert.That((await context.ArtistFollowers.SingleAsync()).FollowAsPersonaId, Is.Null);
    }

    [Test]
    public async Task GetFollowAsOptions_OffersNothingWithoutConsent()
    {
        var options = await _followService.GetFollowAsOptionsAsync(_harness.ListenerUserId);

        Assert.Multiple(() =>
        {
            Assert.That(options.RevealsPersona, Is.False);
            Assert.That(options.NeedsChoice, Is.False, "No consent means no dialog.");
            Assert.That(options.DefaultPersonaId, Is.Null);
        });
    }

    // ------------------------------------------------------------ with consent

    [Test]
    public async Task WithConsent_TheChosenPersonaIsShown()
    {
        await SetConsentAsync(true);

        await _followService.SetFollowStateAsync(
            _harness.PersonaId, _harness.ListenerUserId, true, null, _personaTwoId);

        Assert.That(await FollowerNameAsync(), Is.EqualTo("Night Shift"));
    }

    [Test]
    public async Task WithConsent_FollowingAnonymouslyIsStillPossible()
    {
        // Consenting in general is not consenting to every artist - the dialog always offers
        // anonymous, and choosing it has to actually work.
        await SetConsentAsync(true);

        await _followService.SetFollowStateAsync(
            _harness.PersonaId, _harness.ListenerUserId, true, null, followAsPersonaId: null);

        Assert.That(await FollowerNameAsync(), Does.StartWith("Listener #"));
    }

    [Test]
    public async Task GetFollowAsOptions_AsksWhenThereIsMoreThanOnePersona()
    {
        await SetConsentAsync(true);

        var options = await _followService.GetFollowAsOptionsAsync(_harness.ListenerUserId);

        Assert.Multiple(() =>
        {
            Assert.That(options.RevealsPersona, Is.True);
            Assert.That(options.Personas, Has.Count.EqualTo(2));
            Assert.That(options.NeedsChoice, Is.True);
            Assert.That(options.DefaultPersonaId, Is.Null, "Two personas means there is no default.");
        });
    }

    [Test]
    public async Task GetFollowAsOptions_DoesNotAskWhenThereIsOnlyOnePersona()
    {
        await SetConsentAsync(true);

        await using (var context = _harness.NewContext())
        {
            var second = await context.CreatorPersonas.SingleAsync(p => p.Id == _personaTwoId);
            second.IsEnabled = false;
            await context.SaveChangesAsync();
        }

        var options = await _followService.GetFollowAsOptionsAsync(_harness.ListenerUserId);

        Assert.Multiple(() =>
        {
            Assert.That(options.NeedsChoice, Is.False, "One identity is not a choice.");
            Assert.That(options.DefaultPersonaId, Is.EqualTo(_personaOneId));
        });
    }

    [Test]
    public async Task AFollowerCannotClaimSomeoneElsesPersona()
    {
        // Impersonation, not just a leak: without this a client could attribute its follow to any
        // artist on the platform.
        await SetConsentAsync(true);

        await _followService.SetFollowStateAsync(
            _harness.PersonaId, _harness.ListenerUserId, true, null, followAsPersonaId: _harness.PersonaId);

        await using var context = _harness.NewContext();

        Assert.Multiple(async () =>
        {
            Assert.That((await context.ArtistFollowers.SingleAsync()).FollowAsPersonaId, Is.Null);
            Assert.That(await FollowerNameAsync(), Does.StartWith("Listener #"));
        });
    }

    // ------------------------------------------------------------ withdrawal

    [Test]
    public async Task WithdrawingConsent_HidesTheNameFromArtistsAlreadyFollowed()
    {
        // The question this whole design was built to answer. Because the name is resolved at read
        // time and never stored on the follow row, withdrawal reaches back automatically.
        await SetConsentAsync(true);
        await _followService.SetFollowStateAsync(
            _harness.PersonaId, _harness.ListenerUserId, true, null, _personaOneId);

        Assert.That(await FollowerNameAsync(), Is.EqualTo("Jane Echo"));

        var creatorService = CreateCreatorService();
        await creatorService.SetRevealPersonaToFollowedArtistsAsync(_followerCreatorId, false);

        Assert.That(await FollowerNameAsync(), Does.StartWith("Listener #"));
    }

    [Test]
    public async Task WithdrawingConsent_RotatesThePseudonym()
    {
        // Hiding the name is not enough on its own. The row keeps its Following-since date and its
        // source song, so an artist who saw "Jane Echo" there yesterday would read today's number
        // as the same person. A new number breaks that continuity.
        await SetConsentAsync(true);
        await _followService.SetFollowStateAsync(
            _harness.PersonaId, _harness.ListenerUserId, true, null, _personaOneId);

        int before;
        await using (var context = _harness.NewContext())
        {
            before = (await context.ArtistFollowers.SingleAsync()).AnonymousListenerNumber;
        }

        await CreateCreatorService().SetRevealPersonaToFollowedArtistsAsync(_followerCreatorId, false);

        await using var verify = _harness.NewContext();
        var follow = await verify.ArtistFollowers.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(follow.AnonymousListenerNumber, Is.Not.EqualTo(before));
            Assert.That(follow.FollowAsPersonaId, Is.Null, "The stored choice goes with the consent.");
        });
    }

    [Test]
    public async Task TurningConsentBackOn_DoesNotSilentlyRenameOldFollows()
    {
        // Asymmetric on purpose: hiding is automatic, revealing takes a deliberate act. A follow
        // made anonymously carries no identity choice, so there is nothing to restore.
        await SetConsentAsync(true);
        await _followService.SetFollowStateAsync(
            _harness.PersonaId, _harness.ListenerUserId, true, null, _personaOneId);

        var creatorService = CreateCreatorService();
        await creatorService.SetRevealPersonaToFollowedArtistsAsync(_followerCreatorId, false);
        await creatorService.SetRevealPersonaToFollowedArtistsAsync(_followerCreatorId, true);

        Assert.That(
            await FollowerNameAsync(),
            Does.StartWith("Listener #"),
            "Consent returning must not re-reveal a follow the listener never re-attributed.");
    }

    [Test]
    public async Task DisablingThePersonaTheyFollowedAsFallsBackToAnonymous()
    {
        await SetConsentAsync(true);
        await _followService.SetFollowStateAsync(
            _harness.PersonaId, _harness.ListenerUserId, true, null, _personaOneId);

        await using (var context = _harness.NewContext())
        {
            var persona = await context.CreatorPersonas.SingleAsync(p => p.Id == _personaOneId);
            persona.IsEnabled = false;
            await context.SaveChangesAsync();
        }

        Assert.That(await FollowerNameAsync(), Does.StartWith("Listener #"));
    }

    /// <summary>
    /// CreatorService has a wide constructor; only the context factory matters for consent, so the
    /// rest are bare mocks. UserManager needs a store to construct at all - the same shape
    /// CreatorServiceTests uses.
    /// </summary>
    private CreatorService CreateCreatorService()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        return new CreatorService(
            _harness.ContextFactory.Object,
            Mock.Of<IAzureStorageService>(),
            userManager.Object,
            Mock.Of<IConfiguration>(),
            Mock.Of<ILogger<CreatorService>>(),
            Mock.Of<IAppSettingsService>(),
            Mock.Of<IAdminNotificationService>(),
            Mock.Of<ICreatorPersonaService>(),
            Mock.Of<ICreatorEmailService>());
    }
}
