using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Functions.Functions;
using MusicSalesApp.Functions.Services;

namespace MusicSalesApp.Tests.Functions;

/// <summary>
/// The handler that turns an exhausted queue message into a reported failure.
///
/// <para>
/// Small surface, but it is what lets <c>SongUploadJobReconciler</c> stop being the primary failure
/// detector - so the properties worth pinning are that it reports the <em>right</em> job, marks it
/// with the authoritative code rather than the reconciler's guess, and does not quietly give up if
/// the site is unreachable.
/// </para>
/// </summary>
[TestFixture]
public class HandleTranscodePoisonFunctionTests
{
    private Mock<IMediaProcessingCallbackClient> _callbacks = null!;
    private HandleTranscodePoisonFunction _function = null!;

    [SetUp]
    public void SetUp()
    {
        _callbacks = new Mock<IMediaProcessingCallbackClient>();
        _function = new HandleTranscodePoisonFunction(
            _callbacks.Object,
            Mock.Of<ILogger<HandleTranscodePoisonFunction>>());
    }

    private static AudioTranscodeRequest Request(Guid jobId)
        => new()
        {
            JobId = jobId,
            SourceBlobPath = $"{jobId:N}/source.wav",
            SourceFileName = "Night Drive.wav",
            SourceExtension = ".wav",
            PlaybackBlobPath = $"{jobId:N}/playback.mp3"
        };

    [Test]
    public async Task APoisonedMessage_ReportsThatExactJobAsFailed()
    {
        var jobId = Guid.NewGuid();
        AudioTranscodeResult posted = null;
        _callbacks
            .Setup(client => client.PostTranscodeResultAsync(
                It.IsAny<AudioTranscodeResult>(), It.IsAny<CancellationToken>()))
            .Callback<AudioTranscodeResult, CancellationToken>((result, _) => posted = result)
            .Returns(Task.CompletedTask);

        await _function.HandleTranscodePoison(Request(jobId), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(posted, Is.Not.Null);
            Assert.That(posted.JobId, Is.EqualTo(jobId));
            Assert.That(posted.Outcome, Is.EqualTo(AudioProcessingOutcome.Unplayable));
            Assert.That(posted.Diagnostic, Is.Not.Null.And.Not.Empty,
                "The creator sees this text, so it must not be blank.");
        });
    }

    [Test]
    public async Task ItUsesTheAuthoritativeCode_NotTheReconcilersGuess()
    {
        // Abandoned means "the reconciler lost sight of this"; PoisonedAfterRetries means "Azure
        // states it will never run again". Collapsing them would make a failed job's cause
        // unreadable after the fact.
        AudioTranscodeResult posted = null;
        _callbacks
            .Setup(client => client.PostTranscodeResultAsync(
                It.IsAny<AudioTranscodeResult>(), It.IsAny<CancellationToken>()))
            .Callback<AudioTranscodeResult, CancellationToken>((result, _) => posted = result)
            .Returns(Task.CompletedTask);

        await _function.HandleTranscodePoison(Request(Guid.NewGuid()), CancellationToken.None);

        Assert.That(posted.FailureCode, Is.EqualTo(MediaProcessingFailureCodes.PoisonedAfterRetries));
    }

    [Test]
    public async Task ItReportsExactlyOnce()
    {
        await _function.HandleTranscodePoison(Request(Guid.NewGuid()), CancellationToken.None);

        _callbacks.Verify(
            client => client.PostTranscodeResultAsync(
                It.IsAny<AudioTranscodeResult>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void AFailedCallback_IsNotSwallowed()
    {
        // Terminal-callback discipline: the throw returns the message for another attempt. Giving up
        // silently would leave the job in exactly the stuck state this function exists to end - and
        // the reconciler would not reach it for two hours.
        _callbacks
            .Setup(client => client.PostTranscodeResultAsync(
                It.IsAny<AudioTranscodeResult>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("site is down"));

        Assert.ThrowsAsync<HttpRequestException>(
            () => _function.HandleTranscodePoison(Request(Guid.NewGuid()), CancellationToken.None));
    }

    [Test]
    public void ANullMessage_Throws()
        => Assert.ThrowsAsync<ArgumentNullException>(
            () => _function.HandleTranscodePoison(null, CancellationToken.None));
}
