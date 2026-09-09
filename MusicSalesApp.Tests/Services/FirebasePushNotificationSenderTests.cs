#nullable enable
using System.Net;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The one decision this class makes that can do damage: turning an FCM error into an outcome.
/// </summary>
/// <remarks>
/// <para>
/// Getting it wrong is not a lost notification, it is a lost device. A TokenRejected deactivates
/// the row and settles everything queued for it, so misreading an error that means "your
/// configuration is wrong" as "this handset is gone" retires the whole push registry one run after
/// a typo, and every device then has to re-register before anything works again.
/// </para>
/// <para>
/// Tested through the internal static seam because the sender builds a real GoogleCredential in
/// its constructor, so nothing can drive it end to end without service-account credentials. Same
/// shape, for the same reason, as GooglePlayVerificationServiceTests.
/// </para>
/// </remarks>
[TestFixture]
public class FirebasePushNotificationSenderTests
{
    private const string Token = "token-abc";

    private static PushDeliveryOutcome Classify(HttpStatusCode status, string body) =>
        FirebasePushNotificationSender.Classify(Token, status, body).Outcome;

    /// <summary>A real FCM error body: the code lives in details[], not in error.status.</summary>
    private static string FcmError(string status, string? errorCode = null) =>
        errorCode is null
            ? $$"""
                { "error": { "code": 404, "status": "{{status}}", "message": "requested entity was not found" } }
                """
            : $$"""
                {
                  "error": {
                    "code": 404,
                    "status": "{{status}}",
                    "details": [ { "@type": "type.googleapis.com/google.firebase.fcm.v1.FcmError",
                                   "errorCode": "{{errorCode}}" } ]
                  }
                }
                """;

    [Test]
    public void ADeadTokenIsRetired()
    {
        // The only thing that may retire a token, and it has to be named in the body.
        Assert.That(
            Classify(HttpStatusCode.NotFound, FcmError("NOT_FOUND", "UNREGISTERED")),
            Is.EqualTo(PushDeliveryOutcome.TokenRejected));
    }

    [Test]
    public void ABare404IsDeferred_BecauseItUsuallyMeansTheProjectIdIsWrong()
    {
        // FCM answers 404 for BOTH a dead token and a project path that does not exist, and only
        // the body separates them. This used to OR the status code into the UNREGISTERED branch,
        // so a wrong Push:Firebase:ProjectId - which IsConfigured cannot catch, since it only
        // checks the setting is non-empty - deactivated every device in the batch.
        Assert.That(
            Classify(HttpStatusCode.NotFound, FcmError("NOT_FOUND")),
            Is.EqualTo(PushDeliveryOutcome.TransportFailure));
    }

    [Test]
    public void An404WithNoBodyAtAllIsDeferred()
    {
        // Belt and braces: an empty body carries no evidence, and the safe reading of no evidence
        // is "try again later" rather than "throw the device away".
        Assert.That(
            Classify(HttpStatusCode.NotFound, string.Empty),
            Is.EqualTo(PushDeliveryOutcome.TransportFailure));
    }

    [Test]
    public void AnInvalidTokenArgumentIsRetired()
    {
        var body = """
            {
              "error": {
                "code": 400, "status": "INVALID_ARGUMENT",
                "details": [
                  { "@type": "type.googleapis.com/google.rpc.BadRequest",
                    "fieldViolations": [ { "field": "message.token", "description": "Invalid registration token" } ] },
                  { "@type": "type.googleapis.com/google.firebase.fcm.v1.FcmError",
                    "errorCode": "INVALID_ARGUMENT" }
                ]
              }
            }
            """;

        Assert.That(Classify(HttpStatusCode.BadRequest, body), Is.EqualTo(PushDeliveryOutcome.TokenRejected));
    }

    [Test]
    public void AnInvalidTokenArgumentIsStillRetiredWhenTheBodyIsCompact()
    {
        // The field was matched as a substring including the space after the colon, which really
        // asserted that Google pretty-prints its errors. A compact body fell through to
        // PermanentFailure - neither retried nor retired, so a token that can never succeed was
        // asked again on every run forever.
        var body = "{\"error\":{\"code\":400,\"status\":\"INVALID_ARGUMENT\",\"details\":"
                   + "[{\"@type\":\"type.googleapis.com/google.rpc.BadRequest\",\"fieldViolations\":"
                   + "[{\"field\":\"message.token\"}]},"
                   + "{\"@type\":\"type.googleapis.com/google.firebase.fcm.v1.FcmError\","
                   + "\"errorCode\":\"INVALID_ARGUMENT\"}]}}";

        Assert.That(Classify(HttpStatusCode.BadRequest, body), Is.EqualTo(PushDeliveryOutcome.TokenRejected));
    }

    [Test]
    public void AMalformedPayloadIsOurBug_SoTheTokenIsKept()
    {
        // The whole reason the body is read at all: treating every 400 as a dead token would
        // unregister every device the first time a payload bug shipped.
        var body = """
            {
              "error": {
                "code": 400, "status": "INVALID_ARGUMENT",
                "details": [
                  { "@type": "type.googleapis.com/google.rpc.BadRequest",
                    "fieldViolations": [ { "field": "message.android.notification.color" } ] },
                  { "@type": "type.googleapis.com/google.firebase.fcm.v1.FcmError",
                    "errorCode": "INVALID_ARGUMENT" }
                ]
              }
            }
            """;

        Assert.That(Classify(HttpStatusCode.BadRequest, body), Is.EqualTo(PushDeliveryOutcome.PermanentFailure));
    }

    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.Forbidden)]
    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    public void ThingsThatCouldWorkLaterAreDeferred(HttpStatusCode status)
    {
        // Deferred means the row stays unstamped, which is what makes the backlog survive a
        // Firebase outage or an expired credential.
        Assert.That(Classify(status, "{}"), Is.EqualTo(PushDeliveryOutcome.TransportFailure));
    }

    [Test]
    public void AnUnrecognisedRefusalIsPermanent_NotADeadToken()
    {
        // Settled so the queue drains, but the token is kept: we have no evidence about the device.
        var result = FirebasePushNotificationSender.Classify(Token, HttpStatusCode.Conflict, "{}");

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(PushDeliveryOutcome.PermanentFailure));
            Assert.That(result.Token, Is.EqualTo(Token));
        });
    }
}
