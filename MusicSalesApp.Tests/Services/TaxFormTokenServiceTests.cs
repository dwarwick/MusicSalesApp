#nullable enable
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// Covers the logic that used to live in <c>CreatorController.GetTaxFormToken</c>. It moved into a
/// service because <c>/submittaxform</c> renders InteractiveServer: its circuit has no HttpContext,
/// so the page's self-call over HTTP never carried the auth cookie and always came back 401.
/// </summary>
[TestFixture]
public class TaxFormTokenServiceTests
{
    private const int UserId = 42;
    private const string UserEmail = "creator@example.com";

    private Mock<ICreatorService> _creatorService = null!;
    private Mock<ITaxBanditsService> _taxBandits = null!;

    [SetUp]
    public void SetUp()
    {
        _creatorService = new Mock<ICreatorService>();
        _taxBandits = new Mock<ITaxBanditsService>();
    }

    [Test]
    public async Task ReturnsTheToken_WhenTheCreatorHasAPendingTaxForm()
    {
        GivenCreator(new Creator { TaxFormStatus = TaxFormStatus.Pending, TaxBanditsPayeeRef = "payee@example.com" });
        GivenTransientToken(new TransientTokenResponse { Success = true, TransientToken = "tok_123" });

        var result = await CreateService().GetTaxFormTokenAsync(UserId, UserEmail);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(TaxFormTokenOutcome.Success));
            Assert.That(result.Response!.Success, Is.True);
            Assert.That(result.Response.TransientToken, Is.EqualTo("tok_123"));
            Assert.That(result.Response.PayeeRef, Is.EqualTo("payee@example.com"));
            Assert.That(result.Response.BusinessId, Is.EqualTo("biz-1"));
            Assert.That(result.Response.ScriptUrl, Is.EqualTo("https://taxbandits.example/dropin.js"));
        });
    }

    [Test]
    public async Task FallsBackToTheUsersEmail_WhenTheCreatorHasNoStoredPayeeRef()
    {
        GivenCreator(new Creator { TaxFormStatus = TaxFormStatus.Pending, TaxBanditsPayeeRef = null });
        GivenTransientToken(new TransientTokenResponse { Success = true, TransientToken = "tok_123" });

        var result = await CreateService().GetTaxFormTokenAsync(UserId, UserEmail);

        Assert.That(result.Response!.PayeeRef, Is.EqualTo(UserEmail));
    }

    [Test]
    public async Task IsInvalidRequest_WhenThereIsNoCreatorRecord()
    {
        GivenCreator(null);

        var result = await CreateService().GetTaxFormTokenAsync(UserId, UserEmail);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(TaxFormTokenOutcome.InvalidRequest));
            Assert.That(result.ErrorMessage, Does.Contain("onboarding"));
        });
    }

    [Test]
    public async Task IsInvalidRequest_WhenTheTaxFormIsNotPending()
    {
        // A token is only meaningful for a form the creator has actually been asked to fill in.
        GivenCreator(new Creator { TaxFormStatus = TaxFormStatus.Completed, TaxBanditsPayeeRef = "payee@example.com" });

        var result = await CreateService().GetTaxFormTokenAsync(UserId, UserEmail);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(TaxFormTokenOutcome.InvalidRequest));
            Assert.That(result.ErrorMessage, Does.Contain("No pending tax form"));
        });
    }

    [Test]
    public async Task IsInvalidRequest_WhenThereIsNoPayeeRefAndNoEmail()
    {
        GivenCreator(new Creator { TaxFormStatus = TaxFormStatus.Pending, TaxBanditsPayeeRef = null });

        var result = await CreateService().GetTaxFormTokenAsync(UserId, userEmail: null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(TaxFormTokenOutcome.InvalidRequest));
            Assert.That(result.ErrorMessage, Does.Contain("No email address"));
        });
    }

    [Test]
    public async Task IsServerError_WhenNoOriginsAreConfigured()
    {
        GivenCreator(new Creator { TaxFormStatus = TaxFormStatus.Pending, TaxBanditsPayeeRef = "payee@example.com" });

        var result = await CreateService(origins: null).GetTaxFormTokenAsync(UserId, UserEmail);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(TaxFormTokenOutcome.ServerError));
            Assert.That(result.ErrorMessage, Does.Contain("no allowed origins"));
        });
    }

    [Test]
    public async Task ReadsOrigins_FromACommaSeparatedString()
    {
        // The comma-separated form is what the host's environment variables actually supply, so it
        // is not a theoretical fallback - the JSON array shape never appears in production.
        GivenCreator(new Creator { TaxFormStatus = TaxFormStatus.Pending, TaxBanditsPayeeRef = "payee@example.com" });
        GivenTransientToken(new TransientTokenResponse { Success = true, TransientToken = "tok_123" });

        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Fido2:Origins"] = "https://streamtunes.net,https://www.streamtunes.net",
            ["TaxBandits:BusinessId"] = "biz-1",
            ["TaxBandits:ScriptUrl"] = "https://taxbandits.example/dropin.js"
        });

        var result = await CreateService(config).GetTaxFormTokenAsync(UserId, UserEmail);

        Assert.That(result.Outcome, Is.EqualTo(TaxFormTokenOutcome.Success));
        _taxBandits.Verify(
            t => t.GetTransientTokenAsync(
                It.Is<List<string>>(o => o.Count == 2 && o[0] == "https://streamtunes.net"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task IsServerError_WhenTaxBanditsRefuses()
    {
        GivenCreator(new Creator { TaxFormStatus = TaxFormStatus.Pending, TaxBanditsPayeeRef = "payee@example.com" });
        GivenTransientToken(new TransientTokenResponse { Success = false, ErrorMessage = "quota exceeded" });

        var result = await CreateService().GetTaxFormTokenAsync(UserId, UserEmail);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(TaxFormTokenOutcome.ServerError));
            Assert.That(result.ErrorMessage, Does.Contain("quota exceeded"));
        });
    }

    [Test]
    public async Task IsServerError_WhenTaxBanditsThrows()
    {
        // The page shows ErrorMessage directly, so an unhandled exception here would have surfaced
        // as a blank form rather than an explanation.
        GivenCreator(new Creator { TaxFormStatus = TaxFormStatus.Pending, TaxBanditsPayeeRef = "payee@example.com" });
        _taxBandits
            .Setup(t => t.GetTransientTokenAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("TaxBandits unreachable"));

        var result = await CreateService().GetTaxFormTokenAsync(UserId, UserEmail);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(TaxFormTokenOutcome.ServerError));
            Assert.That(result.ErrorMessage, Does.Contain("error occurred"));
        });
    }

    private void GivenCreator(Creator? creator) =>
        _creatorService.Setup(c => c.GetCreatorByUserIdAsync(UserId)).ReturnsAsync(creator);

    private void GivenTransientToken(TransientTokenResponse response) =>
        _taxBandits
            .Setup(t => t.GetTransientTokenAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

    private TaxFormTokenService CreateService(string? origins = "https://streamtunes.net") =>
        CreateService(BuildConfiguration(new Dictionary<string, string?>
        {
            ["Fido2:Origins"] = origins,
            ["TaxBandits:BusinessId"] = "biz-1",
            ["TaxBandits:ScriptUrl"] = "https://taxbandits.example/dropin.js"
        }));

    private TaxFormTokenService CreateService(IConfiguration configuration) =>
        new(_creatorService.Object,
            _taxBandits.Object,
            configuration,
            NullLogger<TaxFormTokenService>.Instance);

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
