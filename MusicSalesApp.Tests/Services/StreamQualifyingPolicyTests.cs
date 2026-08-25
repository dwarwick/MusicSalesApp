using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The promotional reduction to the stream-qualifying threshold.
///
/// It lowers only the effective counting threshold - each creator's contracted
/// StreamQualifyingSeconds is untouched, because that value is locked at onboarding and referenced by
/// the creator agreement.
/// </summary>
[TestFixture]
public class StreamQualifyingPolicyTests
{
    [Test]
    public void Resolve_Disabled_ReturnsTheContractedValueUnchanged()
    {
        Assert.That(StreamQualifyingPolicy.Resolve(65, reductionEnabled: false), Is.EqualTo(65));
    }

    [Test]
    public void Resolve_Enabled_TakesTheReductionOff()
    {
        Assert.That(
            StreamQualifyingPolicy.Resolve(65, reductionEnabled: true),
            Is.EqualTo(65 - StreamQualifyingPolicy.PromotionalReductionSeconds));
    }

    [Test]
    public void Resolve_Enabled_BringsAContractedSixtyFiveInsideTheFreeListenerPreviewWindow()
    {
        // The whole point: a listener without a subscription hears only 60 seconds of a restricted
        // song, so a creator contracted at 65 can never be credited with a free listener's stream.
        var effective = StreamQualifyingPolicy.Resolve(65, reductionEnabled: true);

        Assert.That(effective, Is.LessThan(60));
    }

    [Test]
    public void Resolve_Enabled_NeverGoesBelowTheFloor()
    {
        // Without the floor a creator contracted at or under the reduction would land on zero and be
        // credited the instant playback started.
        Assert.That(
            StreamQualifyingPolicy.Resolve(StreamQualifyingPolicy.PromotionalReductionSeconds, reductionEnabled: true),
            Is.EqualTo(StreamQualifyingPolicy.MinimumQualifyingSeconds));

        Assert.That(
            StreamQualifyingPolicy.Resolve(1, reductionEnabled: true),
            Is.EqualTo(StreamQualifyingPolicy.MinimumQualifyingSeconds));
    }

    [Test]
    public void Resolve_EnabledNeverRaisesTheThreshold()
    {
        // A reduction that increased anyone's threshold would be a worse deal than the agreement, which
        // is the one direction this may not move.
        for (var contracted = 1; contracted <= 300; contracted++)
        {
            Assert.That(
                StreamQualifyingPolicy.Resolve(contracted, reductionEnabled: true),
                Is.LessThanOrEqualTo(Math.Max(contracted, StreamQualifyingPolicy.MinimumQualifyingSeconds)),
                $"Contracted {contracted} should never be raised by the reduction.");
        }
    }

    // --- StreamQualifyingSettings ---

    [Test]
    public void Settings_WithACreatorValue_PrefersItOverTheAdminDefault()
    {
        var settings = new StreamQualifyingSettings(DefaultSeconds: 30, ReductionEnabled: false);

        Assert.That(settings.Resolve(creatorSeconds: 65), Is.EqualTo(65));
    }

    [Test]
    public void Settings_WithNoCreator_FallsBackToTheAdminDefault()
    {
        var settings = new StreamQualifyingSettings(DefaultSeconds: 45, ReductionEnabled: false);

        Assert.That(settings.Resolve(creatorSeconds: null), Is.EqualTo(45));
    }

    [Test]
    public void Settings_ReductionAppliesToTheCreatorValueNotJustTheDefault()
    {
        // The regression this guards: reducing only the admin default would leave every existing
        // creator on their contracted threshold and the flag would appear to do nothing.
        var settings = new StreamQualifyingSettings(DefaultSeconds: 30, ReductionEnabled: true);

        Assert.That(
            settings.Resolve(creatorSeconds: 65),
            Is.EqualTo(65 - StreamQualifyingPolicy.PromotionalReductionSeconds));
    }

    [Test]
    public void Settings_ReductionAppliesToTheFallbackToo()
    {
        var settings = new StreamQualifyingSettings(DefaultSeconds: 65, ReductionEnabled: true);

        Assert.That(
            settings.Resolve(creatorSeconds: null),
            Is.EqualTo(65 - StreamQualifyingPolicy.PromotionalReductionSeconds));
    }
}
