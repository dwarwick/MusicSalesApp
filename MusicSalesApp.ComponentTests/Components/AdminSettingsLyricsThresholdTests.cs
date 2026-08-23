using Bunit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages.Admin;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Hubs;

#nullable enable

namespace MusicSalesApp.ComponentTests.Components;

/// <summary>
/// The lyric timing confidence threshold, as an administrator loads and saves it.
///
/// <para>
/// <b>Nothing creator-facing consults this setting any more.</b> It used to choose the wording of
/// the greeting on the Preview Results page, until it became clear the aligner scores sung vocals so
/// far below speech that the low-confidence warning fired on timings creators were perfectly happy
/// with.  The setting is kept - stored, loaded and saved - so the greeting can be reinstated if the
/// scoring is ever made trustworthy, and these tests are kept with it.
/// </para>
///
/// <para>
/// The service stores 0-1 and the UI works in whole percent.  That conversion is the most likely
/// thing to be wrong and it fails plausibly in both directions, so it is asserted on the model rather
/// than through the markup, following <c>AdminSettingsPayPalOfferTests</c>.
/// </para>
/// </summary>
[TestFixture]
public class AdminSettingsLyricsThresholdTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        TestContext.Services.AddSingleton(new Mock<IHubContext<MaintenanceHub>>().Object);
        SetupRendererInfo();

        MockAppSettingsService.Setup(x => x.SetLyricsConfidenceThresholdAsync(It.IsAny<double>()))
            .Returns(Task.CompletedTask);

        // SaveSettings validates EVERY field before writing ANY of them, so any setting the shared
        // base leaves at its Moq default fails a rule belonging to some other feature and aborts the
        // save before the threshold is reached. Neither of these is stubbed in BUnitTestBase: the
        // audio size defaults to 0 and fails "at least 1 MB", and the app version defaults to null
        // and fails "cannot be empty".
        MockAppSettingsService.Setup(x => x.GetMaxAudioUploadSizeMBAsync())
            .ReturnsAsync(100);
        MockAppSettingsService.Setup(x => x.GetAppVersionAsync())
            .ReturnsAsync("1.0.0");
    }

    private IRenderedComponent<LyricsThresholdTestComponent> RenderLoaded(double stored)
    {
        MockAppSettingsService.Setup(x => x.GetLyricsConfidenceThresholdAsync())
            .ReturnsAsync(stored);

        var cut = TestContext.Render<LyricsThresholdTestComponent>();
        cut.WaitForAssertion(() => Assert.That(cut.Instance.IsLoaded, Is.True));
        return cut;
    }

    [Test]
    public void TheStoredFractionIsLoadedAsAPercentage()
    {
        // 0.7 must reach the admin as 70. Presenting the raw fraction under a field labelled "(%)"
        // invites an admin to type 0.52 and set half a percent.
        var cut = RenderLoaded(0.7d);

        Assert.That(cut.Instance.ThresholdPercent, Is.EqualTo(70m));
    }

    [Test]
    public void AThresholdOfZeroIsLoadedRatherThanTreatedAsUnset()
    {
        // Zero is legitimate - it publishes everything clearing the structural gates, which is what
        // somebody calibrating downwards would try. Falling back to the default because the value is
        // falsy would silently refuse the setting.
        var cut = RenderLoaded(0d);

        Assert.That(cut.Instance.ThresholdPercent, Is.EqualTo(0m));
    }

    [Test]
    public async Task SavingConvertsThePercentageBackToAFraction()
    {
        // The half of the round trip that reaches the database. 50 must be stored as 0.5, because
        // GetLyricsConfidenceThresholdAsync rejects anything outside 0-1 and silently returns 0.7 -
        // so a threshold stored as 50 would not fail loudly, it would appear not to have saved.
        var cut = RenderLoaded(0.7d);

        await cut.InvokeAsync(() => cut.Instance.SetThresholdAndSaveAsync(50m));

        Assert.That(cut.Instance.ValidationErrors, Is.Empty, "Save was blocked by validation.");
        MockAppSettingsService.Verify(
            x => x.SetLyricsConfidenceThresholdAsync(0.5d),
            Times.Once);
    }

    [Test]
    public async Task SavingAnUnchangedThresholdWritesNothing()
    {
        // Written only on a change, matching the direct-to-storage switch. The write carries a
        // warning-level log saying the bar moved, and emitting that every time somebody pressed Save
        // on this page would bury the one occasion it mattered.
        var cut = RenderLoaded(0.7d);

        await cut.InvokeAsync(() => cut.Instance.SetThresholdAndSaveAsync(70m));

        MockAppSettingsService.Verify(
            x => x.SetLyricsConfidenceThresholdAsync(It.IsAny<double>()),
            Times.Never);
    }

    [Test]
    public async Task CancellingRestoresTheLoadedThreshold()
    {
        var cut = RenderLoaded(0.7d);

        await cut.InvokeAsync(() =>
        {
            cut.Instance.SetThreshold(35m);
            cut.Instance.Cancel();
        });

        Assert.That(cut.Instance.ThresholdPercent, Is.EqualTo(70m));
    }

    private sealed class LyricsThresholdTestComponent : AdminSettingsModel
    {
        public bool IsLoaded => !_isLoading;

        public decimal ThresholdPercent => _lyricsConfidenceThresholdPercent;

        public IReadOnlyList<string> ValidationErrors => _validationErrors;

        public void SetThreshold(decimal percent) => _lyricsConfidenceThresholdPercent = percent;

        public void Cancel() => CancelChanges();

        public async Task SetThresholdAndSaveAsync(decimal percent)
        {
            _lyricsConfidenceThresholdPercent = percent;
            await SaveSettings();
        }
    }
}
