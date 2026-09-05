using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Controllers;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

/// <summary>
/// The status codes matter more than usual here. The mobile client queues follow intents while
/// offline and replays them in order; it drops a 400 and retries a 5xx forever, and its flush stops
/// at the first failure. A permanent refusal returned as anything retryable would strand every
/// intent queued behind it.
/// </summary>
[TestFixture]
public class MobileFollowControllerTests
{
    private const int TestUserId = 100;
    private const int TestPersonaId = 42;

    private Mock<IArtistFollowService> _followService;
    private Mock<IArtistFollowerMessageService> _messageService;
    private Mock<IArtistReleaseNotificationService> _releaseNotificationService;
    private Mock<IArtistNotificationPreferenceService> _preferenceService;
    private MobileFollowController _controller;

    [SetUp]
    public void SetUp()
    {
        _followService = new Mock<IArtistFollowService>();
        _messageService = new Mock<IArtistFollowerMessageService>();
        _releaseNotificationService = new Mock<IArtistReleaseNotificationService>();
        _preferenceService = new Mock<IArtistNotificationPreferenceService>();

        _controller = new MobileFollowController(
            _followService.Object,
            _messageService.Object,
            _releaseNotificationService.Object,
            _preferenceService.Object);

        SetAuthenticatedUser(TestUserId);
    }

    private void SetAuthenticatedUser(int? userId)
    {
        var identity = userId.HasValue
            ? new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString())], "TestAuth")
            : new ClaimsIdentity();

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
    }

    [Test]
    public async Task SetFollowState_ReturnsFollowingTrueWhenTheFollowIsCreated()
    {
        _followService
            .Setup(s => s.SetFollowStateAsync(TestPersonaId, TestUserId, true, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ArtistFollowOutcome.Followed);

        var result = await _controller.SetFollowState(
            TestPersonaId, new SetFollowStateRequest { Following = true, SourceSongId = 7 });

        var ok = result as OkObjectResult;
        var payload = ok?.Value as FollowStateResponse;

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.Not.Null);
            Assert.That(payload!.Following, Is.True);
            Assert.That(payload.CreatorPersonaId, Is.EqualTo(TestPersonaId));
        });
    }

    [Test]
    public async Task SetFollowState_TreatsAlreadyFollowingAsSuccess()
    {
        // The replayed-intent case. It must read as "your request is satisfied", not as an error,
        // or the client drops an intent that actually took effect on an earlier attempt.
        _followService
            .Setup(s => s.SetFollowStateAsync(TestPersonaId, TestUserId, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ArtistFollowOutcome.AlreadyFollowing);

        var result = await _controller.SetFollowState(TestPersonaId, new SetFollowStateRequest { Following = true });

        var payload = (result as OkObjectResult)?.Value as FollowStateResponse;

        Assert.That(payload?.Following, Is.True);
    }

    [Test]
    public async Task SetFollowState_TreatsNotFollowingAsSuccessWhenUnfollowing()
    {
        _followService
            .Setup(s => s.SetFollowStateAsync(TestPersonaId, TestUserId, false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ArtistFollowOutcome.NotFollowing);

        var result = await _controller.SetFollowState(TestPersonaId, new SetFollowStateRequest { Following = false });

        var payload = (result as OkObjectResult)?.Value as FollowStateResponse;

        Assert.That(payload?.Following, Is.False);
    }

    [TestCase(ArtistFollowOutcome.ArtistUnavailable)]
    [TestCase(ArtistFollowOutcome.Blocked)]
    public async Task SetFollowState_AnswersPermanentRefusalsWith400(ArtistFollowOutcome outcome)
    {
        // Not 404, and above all not a 5xx: the client retries 5xx forever and stops flushing at
        // the first failure, so a permanent condition dressed as transient blocks the whole queue.
        _followService
            .Setup(s => s.SetFollowStateAsync(TestPersonaId, TestUserId, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var result = await _controller.SetFollowState(TestPersonaId, new SetFollowStateRequest { Following = true });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task SetFollowState_RejectsAMissingBody()
    {
        var result = await _controller.SetFollowState(TestPersonaId, null);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task SetFollowState_RequiresAnAuthenticatedUser()
    {
        SetAuthenticatedUser(null);

        var result = await _controller.SetFollowState(TestPersonaId, new SetFollowStateRequest { Following = true });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
            _followService.Verify(
                s => s.SetFollowStateAsync(
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        });
    }

    [Test]
    public async Task GetFollowStates_ReturnsEmptyForAnEmptyRequestWithoutQueryingAnything()
    {
        var result = await _controller.GetFollowStates(new FollowStatesRequest { PersonaIds = [] });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            _followService.Verify(
                s => s.GetFollowedPersonaIdsAsync(
                    It.IsAny<IEnumerable<int>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
                Times.Never);
        });
    }

    [Test]
    public async Task GetFollowStates_AsksOnlyAboutTheCallersOwnFollows()
    {
        _followService
            .Setup(s => s.GetFollowedPersonaIdsAsync(
                It.IsAny<IEnumerable<int>>(), TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<int> { TestPersonaId });

        var result = await _controller.GetFollowStates(new FollowStatesRequest { PersonaIds = [TestPersonaId, 43] });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _followService.Verify(
            s => s.GetFollowedPersonaIdsAsync(It.IsAny<IEnumerable<int>>(), TestUserId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ReportMessage_RequiresAReason()
    {
        var result = await _controller.ReportMessage(5, new ReportArtistMessageRequest { Reason = "  " });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ReportMessage_AnswersARefusalWith400RatherThan404()
    {
        // The service returns false for both an unknown reason and someone else's message. Either
        // way it is permanent, and a 404 would read to the client as a routing problem.
        _messageService
            .Setup(s => s.ReportAsync(5, TestUserId, ReportReasonTypes.TermsOfUseViolation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.ReportMessage(
            5, new ReportArtistMessageRequest { Reason = ReportReasonTypes.TermsOfUseViolation });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task MarkMessageRead_PassesTheCallersIdSoAnotherUsersMessageCannotBeTouched()
    {
        _messageService
            .Setup(s => s.MarkReadAsync(5, TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.MarkMessageRead(5);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<NotFoundResult>());
            _messageService.Verify(s => s.MarkReadAsync(5, TestUserId, It.IsAny<CancellationToken>()), Times.Once);
        });
    }

    [Test]
    public async Task SetArtistPreferences_RejectsAMuteForAnArtistTheCallerDoesNotFollow()
    {
        _followService
            .Setup(s => s.SetArtistNotificationPreferencesAsync(
                TestPersonaId, TestUserId, false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.SetArtistPreferences(
            TestPersonaId, new ArtistPreferencesRequest { ReleaseNotificationsEnabled = false });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task SetNotificationPreferences_SavesForTheCaller()
    {
        _preferenceService
            .Setup(s => s.SetAsync(TestUserId, It.IsAny<ArtistNotificationPreferences>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.SetNotificationPreferences(
            new ArtistNotificationPreferences { ReceiveArtistReleaseEmails = false, ReceiveArtistMessageEmails = true });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkResult>());
            _preferenceService.Verify(
                s => s.SetAsync(
                    TestUserId,
                    It.Is<ArtistNotificationPreferences>(p => !p.ReceiveArtistReleaseEmails && p.ReceiveArtistMessageEmails),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        });
    }

    [Test]
    public void EveryRouteIsGatedByBothTheApiKeyAndAToken()
    {
        // Class-level attributes, so a new action added later inherits them. Asserted because the
        // mobile API key is what keeps the catalogue off a plain browser, and losing either
        // attribute is invisible until someone goes looking.
        var attributes = typeof(MobileFollowController).GetCustomAttributes(inherit: true);

        Assert.Multiple(() =>
        {
            Assert.That(
                attributes.Any(a => a.GetType().Name == "RequireMobileApiKeyAttribute"),
                Is.True,
                "The controller must carry [RequireMobileApiKey].");
            Assert.That(
                attributes.Any(a => a.GetType().Name == "AuthorizeAttribute"),
                Is.True,
                "The controller must carry [Authorize].");
        });
    }
}
