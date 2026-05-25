using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Controllers;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class MobileContactControllerTests
{
    private Mock<IContactRequestEmailService> _mockEmailService;
    private Mock<IContactRequestRateLimitService> _mockRateLimitService;
    private MobileContactController _controller;

    [SetUp]
    public void SetUp()
    {
        _mockEmailService = new Mock<IContactRequestEmailService>();
        _mockRateLimitService = new Mock<IContactRequestRateLimitService>();
        _mockEmailService
            .Setup(service => service.SendContactRequestEmailsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ContactRequestEmailResult(true, true));
        _mockRateLimitService
            .Setup(service => service.TryReserveSubmissionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(ContactRequestReservationResult.Allowed(42));

        _controller = CreateController(userId: 7, email: "validated@example.com", includeValidatedClaim: true);
    }

    [Test]
    public void Controller_RequiresValidatedUserPolicy()
    {
        var authorizeAttribute = typeof(MobileContactController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.That(authorizeAttribute, Is.Not.Null);
        Assert.That(authorizeAttribute!.Policy, Is.EqualTo(Permissions.ValidatedUser));
    }

    [Test]
    public async Task Submit_InvalidSubject_ReturnsBadRequest()
    {
        var result = await _controller.Submit(new MobileContactRequest
        {
            Subject = "Other",
            Message = "Hello"
        });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockEmailService.Verify(service => service.SendContactRequestEmailsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task Submit_BlankMessage_ReturnsBadRequest()
    {
        var result = await _controller.Submit(new MobileContactRequest
        {
            Subject = ContactRequestSubjectTypes.BugReport,
            Message = "   "
        });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Submit_MissingIdentity_ReturnsUnauthorized()
    {
        _controller = CreateController(userId: null, email: null, includeValidatedClaim: false);

        var result = await _controller.Submit(new MobileContactRequest
        {
            Subject = ContactRequestSubjectTypes.BugReport,
            Message = "Hello"
        });

        Assert.That(result, Is.InstanceOf<UnauthorizedResult>());
    }

    [Test]
    public async Task Submit_RateLimitFailure_ReturnsTooManyRequestsAndDoesNotSendEmail()
    {
        _mockRateLimitService
            .Setup(service => service.TryReserveSubmissionAsync(7, "validated@example.com", ContactRequestSubjectTypes.BugReport, 5, "10.0.0.1"))
            .ReturnsAsync(ContactRequestReservationResult.Blocked("Please wait."));

        var result = await _controller.Submit(new MobileContactRequest
        {
            Subject = ContactRequestSubjectTypes.BugReport,
            Message = "Hello"
        });

        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status429TooManyRequests));
        _mockEmailService.Verify(service => service.SendContactRequestEmailsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task Submit_ValidRequest_SendsEmailAndMarksResult()
    {
        var result = await _controller.Submit(new MobileContactRequest
        {
            Subject = ContactRequestSubjectTypes.AppSuggestion,
            Message = "Please add queue editing."
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockEmailService.Verify(service => service.SendContactRequestEmailsAsync(
            "validated@example.com",
            ContactRequestSubjectTypes.AppSuggestion,
            "Please add queue editing."), Times.Once);
        _mockRateLimitService.Verify(service => service.MarkEmailResultAsync(42, true, true), Times.Once);
    }

    private MobileContactController CreateController(int? userId, string email, bool includeValidatedClaim)
    {
        var controller = new MobileContactController(
            _mockEmailService.Object,
            _mockRateLimitService.Object,
            Mock.Of<ILogger<MobileContactController>>());

        var claims = new List<Claim>();
        if (userId.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        if (includeValidatedClaim)
        {
            claims.Add(new Claim(CustomClaimTypes.Permission, Permissions.ValidatedUser));
        }

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
        };
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return controller;
    }
}