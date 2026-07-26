using Bunit;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.ComponentTests.Testing;
using MusicSalesApp.Components.Pages.Admin;
using MusicSalesApp.Models;
using Syncfusion.Blazor.ProgressBar;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class StorageBackupTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        SetupRendererInfo();
    }

    [Test]
    public void StorageBackup_RendersHeadingAndActions()
    {
        var cut = TestContext.Render<StorageBackup>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Storage Backup"));
            Assert.That(cut.Markup, Does.Contain("Run Backup Now"));
            Assert.That(cut.Markup, Does.Contain("Force Full Re-copy"));
            Assert.That(cut.Markup, Does.Contain("Restore from Backup"));
        });
    }

    [Test]
    public void StorageBackup_WithAnActiveRun_RendersOneProgressBarPerContainer()
    {
        MockStorageBackupService.Setup(service => service.GetActiveRunAsync())
            .ReturnsAsync(ActiveRun());

        var cut = TestContext.Render<StorageBackup>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindComponents<SfProgressBar>(), Has.Count.EqualTo(2)));
    }

    [Test]
    public void StorageBackup_ProgressBarReflectsProcessedShareOfTotal()
    {
        MockStorageBackupService.Setup(service => service.GetActiveRunAsync())
            .ReturnsAsync(ActiveRun());

        var cut = TestContext.Render<StorageBackup>();

        cut.WaitForAssertion(() =>
        {
            var bars = cut.FindComponents<SfProgressBar>();
            // musiccontainer: 25 of 100 processed.
            Assert.That(bars[0].Instance.Value, Is.EqualTo(25).Within(0.001));
        });
    }

    [Test]
    public void StorageBackup_WhileAContainerIsListing_ItsBarIsIndeterminate()
    {
        var run = ActiveRun();
        run.Containers.First().Status = StorageBackupContainerStatus.Listing;
        MockStorageBackupService.Setup(service => service.GetActiveRunAsync()).ReturnsAsync(run);

        var cut = TestContext.Render<StorageBackup>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindComponents<SfProgressBar>()[0].Instance.IsIndeterminate, Is.True));
    }

    [Test]
    public void StorageBackup_RunBackupButton_QueuesABackup()
    {
        var cut = TestContext.Render<StorageBackup>();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Run Backup Now")));
        ClickButtonContaining(cut, "Run Backup Now");

        cut.WaitForAssertion(() => MockStorageBackupService.Verify(
            service => service.StartBackupAsync(It.IsAny<int?>(), It.IsAny<string>(), false), Times.Once));
    }

    [Test]
    public void StorageBackup_ForceFullRecopyButton_QueuesAForcedBackup()
    {
        var cut = TestContext.Render<StorageBackup>();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Force Full Re-copy")));
        ClickButtonContaining(cut, "Force Full Re-copy");

        cut.WaitForAssertion(() => MockStorageBackupService.Verify(
            service => service.StartBackupAsync(It.IsAny<int?>(), It.IsAny<string>(), true), Times.Once));
    }

    [Test]
    public void StorageBackup_WhileARunIsActive_ActionButtonsAreDisabled()
    {
        MockStorageBackupService.Setup(service => service.GetActiveRunAsync())
            .ReturnsAsync(ActiveRun());

        var cut = TestContext.Render<StorageBackup>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Actions are unavailable while a run is active."));
            Assert.That(cut.Instance, Is.Not.Null);
        });
    }

    [Test]
    public void StorageBackup_RestoreDialog_DoesNotOfferTheDataProtectionKeyRing()
    {
        var cut = TestContext.Render<StorageBackup>();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Restore from Backup")));
        ClickButtonContaining(cut, "Restore from Backup");

        cut.WaitForAssertion(() => Assert.That(cut.Instance.ContainerSelections, Is.Not.Empty));

        Assert.Multiple(() =>
        {
            Assert.That(
                cut.Instance.ContainerSelections.Select(selection => selection.Name),
                Does.Not.Contain("dataprotection-keys"));
            Assert.That(cut.Instance.ContainerSelections.Any(selection => selection.Selected), Is.False,
                "Nothing is pre-selected, so restore must start disabled.");
            Assert.That(cut.Instance.CanConfirmRestore, Is.False);
        });
    }

    [Test]
    public void StorageBackup_RestoreStaysDisabledUntilTheConfirmationPhraseIsTyped()
    {
        var cut = TestContext.Render<StorageBackup>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Restore from Backup")));
        ClickButtonContaining(cut, "Restore from Backup");

        var model = cut.Instance;
        cut.WaitForAssertion(() => Assert.That(model.ContainerSelections, Is.Not.Empty));

        model.ContainerSelections.First().Selected = true;

        Assert.Multiple(() =>
        {
            model.RestoreConfirmation = string.Empty;
            Assert.That(model.CanConfirmRestore, Is.False);

            model.RestoreConfirmation = "restore";
            Assert.That(model.CanConfirmRestore, Is.False, "The phrase is case-sensitive.");

            model.RestoreConfirmation = "RESTORE";
            Assert.That(model.CanConfirmRestore, Is.True);
        });
    }

    [Test]
    public void StorageBackup_RunHistoryGridRendersCompletedRuns()
    {
        MockStorageBackupService.Setup(service => service.GetRunsAsync())
            .ReturnsAsync(new List<StorageBackupRun>
            {
                new()
                {
                    Id = 42,
                    Direction = StorageBackupDirection.Backup,
                    Status = StorageBackupRunStatus.Completed,
                    TriggerSource = StorageBackupTriggerSources.Recurring,
                    CreatedAt = new DateTime(2026, 7, 26, 6, 45, 0, DateTimeKind.Utc),
                    CopiedCount = 12,
                    SkippedCount = 3400
                }
            });

        var cut = TestContext.Render<StorageBackup>();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Recurring")));
    }

    private static void ClickButtonContaining(IRenderedComponent<StorageBackup> cut, string text)
    {
        var button = cut.FindAll("button")
            .First(element => element.TextContent.Contains(text, StringComparison.Ordinal));
        button.Click();
    }

    private static StorageBackupRun ActiveRun()
        => new()
        {
            Id = 9,
            Direction = StorageBackupDirection.Backup,
            Status = StorageBackupRunStatus.Running,
            Containers = new List<StorageBackupContainerProgress>
            {
                Container(1, "musiccontainer", total: 100, processed: 25),
                Container(2, "persona-images", total: 10, processed: 10)
            }
        };

    private static StorageBackupContainerProgress Container(int id, string name, int total, int processed)
        => new()
        {
            Id = id,
            SourceContainerName = name,
            DestinationContainerName = StorageBackupNaming.ToBackupContainerName(name),
            Status = StorageBackupContainerStatus.Copying,
            TotalBlobCount = total,
            ProcessedCount = processed,
            CopiedCount = processed
        };
}
