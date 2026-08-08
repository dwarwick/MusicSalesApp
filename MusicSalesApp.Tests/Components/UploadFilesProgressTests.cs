using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Components.Pages.Creator;

namespace MusicSalesApp.Tests.Components;

public class UploadFilesProgressTests
{
    // -----------------------------------------------------------------
    // Drop-box visibility across both phases.
    // -----------------------------------------------------------------

    [Test]
    public void UploadBox_IsHiddenWhileFilesAreBeingReceived()
    {
        var page = new TestableUploadFiles { Uploading = true };

        Assert.That(page.HideBox, Is.True);
    }

    [Test]
    public void UploadBox_StaysHiddenWhileSongsAreStillProcessing()
    {
        // The regression this pins: _isUploading goes false the instant the last file is staged,
        // which is the instant processing starts. The box used to reappear right underneath a batch
        // that was still transcoding, inviting a second batch on top of the first.
        var page = new TestableUploadFiles();
        page.AddQueuedRow(AudioProcessingStep.Transcoding);

        Assert.Multiple(() =>
        {
            Assert.That(page.Uploading, Is.False, "Receiving is over by this point.");
            Assert.That(page.HideBox, Is.True);
        });
    }

    [Test]
    public void UploadBox_ComesBackOnceEverySongIsTerminal()
    {
        var page = new TestableUploadFiles();
        page.AddQueuedRow(AudioProcessingStep.Completed);
        page.AddQueuedRow(AudioProcessingStep.Failed);

        Assert.Multiple(() =>
        {
            Assert.That(page.HideBox, Is.False);
            Assert.That(
                page.OverallPercent,
                Is.EqualTo(100),
                "The box returning and the batch bar reaching 100% are the same moment.");
        });
    }

    [Test]
    public void UploadBox_IsStillOfferedDuringTheTitleReviewPause()
    {
        // Those rows exist but have never been staged, and choosing different files at the review
        // step has always been a legitimate way to replace the batch.
        var page = new TestableUploadFiles();
        page.AddUnstagedRow();

        Assert.That(page.HideBox, Is.False);
    }

    [Test]
    public void UploadBox_IsHiddenForAnInFlightBatchRestoredOnARevisit()
    {
        // Coming back to the page mid-processing rebuilds the rows from the job table. They carry a
        // media GUID and a real step, so they gate the box exactly as the originals did.
        var page = new TestableUploadFiles();
        page.AddQueuedRow(AudioProcessingStep.Copying);

        Assert.That(page.HideBox, Is.True);
    }

    /// <summary>
    /// Reaches the protected visibility members. Nothing here renders or touches an injected
    /// service, so the component needs no DI graph.
    /// </summary>
    private sealed class TestableUploadFiles : UploadFilesModel
    {
        public bool Uploading
        {
            get => _isUploading;
            init => _isUploading = value;
        }

        public bool HideBox => HideUploadBox;

        public int OverallPercent => OverallProgressPercent;

        public void AddQueuedRow(AudioProcessingStep step) => _uploadItems.Add(new UploadPairItem
        {
            MediaGuid = Guid.NewGuid(),
            Step = step,
            Progress = (int)AudioProcessingProgressCalculator.ToOverallPercent(step)
        });

        /// <summary>A row buffered for the title review, before anything has been staged.</summary>
        public void AddUnstagedRow() => _uploadItems.Add(new UploadPairItem
        {
            Step = AudioProcessingStep.Staging
        });
    }

    [Test]
    public void UploadFiles_UsesBlazorStateForInitialUploadProgress()
    {
        var markup = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor");
        var codeBehind = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs");

        Assert.That(markup, Does.Contain("@if (_initialUploadItems.Any())"));
        Assert.That(markup, Does.Contain("_initialUploadBatchProgress"));
        Assert.That(markup, Does.Contain("_initialUploadStatusMessage"));

        // The drop box is gated through HideUploadBox now, which folds the processing phase in
        // alongside the two upload flags - see the UploadBox_* tests above.
        Assert.That(markup, Does.Contain("HideUploadBox"));

        Assert.That(codeBehind, Does.Contain("InitialUploadProgressUpdateInterval = TimeSpan.FromSeconds(1)"));
        Assert.That(codeBehind, Does.Contain("await InvokeAsync(StateHasChanged);"));
        Assert.That(codeBehind, Does.Not.Contain("updateInitialUploadProgress"));
        Assert.That(markup, Does.Not.Contain("startInitialUploadProgress"));
        Assert.That(markup, Does.Not.Contain("updateInitialUploadProgress"));
        Assert.That(markup, Does.Not.Contain("hideInitialUploadProgress"));
    }

    [Test]
    public void UploadFiles_RoutesEveryReceivingPhaseStageThroughTheCalculator()
    {
        // The receiving phase spans three stages that each write the same bar, and the calculator
        // exists so they join up. Its own tests only prove the bands are contiguous - they cannot
        // see whether the page actually uses them, and for a while it did not: the receive stage
        // assigned a raw 0-100 and the next stage assigned a band whose start is 0, so the bar ran
        // to full and snapped back to nothing in front of the creator.
        var codeBehind = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs");

        Assert.Multiple(() =>
        {
            Assert.That(
                codeBehind,
                Does.Contain("CoverArtMatchProgressCalculator.ToReceivingPercent(batchPercent)"),
                "The receive stage must map onto its band rather than assigning a raw percentage.");

            Assert.That(
                codeBehind,
                Does.Not.Contain("_initialUploadBatchProgress = batchPercent"),
                "A raw assignment bypasses the calculator and reintroduces the snap-back.");

            Assert.That(
                codeBehind,
                Does.Not.Contain("ToOverallPercent(CoverArtMatchStep.Staging)"),
                "That band starts at 0, so assigning it after receiving resets the bar. "
                + "Use ToStagingImagesPercent, which continues from where receiving ended.");
        });
    }

    [Test]
    public void UploadFiles_DirectUploadIsOptionalAndFallsBackToTheServerPath()
    {
        // Two independent gates, both of which must fail closed. The flag defaults off, and a browser
        // that cannot load the module leaves _uploadModule null - either way the page must still take
        // the server-side path it has always used, so an environment without CORS, without the flag,
        // or with a blocked script is slower rather than broken.
        var codeBehind = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs");
        var markup = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor");

        Assert.Multiple(() =>
        {
            Assert.That(codeBehind, Does.Contain("IsDirectToStorageUploadEnabledAsync()"));
            Assert.That(
                codeBehind,
                Does.Contain("DirectUploadAvailable => _uploadModule is not null"),
                "A failed module import must leave the page on the server-side path.");
            Assert.That(
                codeBehind,
                Does.Contain("CoverArtMatchService.StageAndEnqueueAsync"),
                "The server-side staging path must remain reachable as the fallback.");

            // JS finds the live FileList through this class; without it the upload module has no
            // files to read and every direct upload reports NoFileList.
            Assert.That(markup, Does.Contain("direct-upload-input"));

            // Cancelling a token cannot stop a transfer the server is not part of.
            Assert.That(
                codeBehind,
                Does.Contain("AbortDirectUploadsAsync"),
                "Disposal must tell the browser to stop, not just cancel a server-side token.");
        });
    }

    [Test]
    public void UploadFiles_ReviewStepOffersAnEditableTitlePrefilledFromTheFileName()
    {
        var markup = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor");
        var codeBehind = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs");

        Assert.That(markup, Does.Contain("_awaitingTitleConfirmation"));
        Assert.That(markup, Does.Contain("@bind-Value=\"item.SongTitle\""));
        Assert.That(markup, Does.Contain("StartUploadAsync"));
        Assert.That(markup, Does.Contain("CancelPendingBatchAsync"));

        Assert.That(codeBehind, Does.Contain("SongTitle = SongTitleHelper.FromFileName(pair.AudioFileName)"));
        Assert.That(codeBehind, Does.Contain("AudioFileName = audioFileMeta.Name"));

        // The storage path no longer comes from the creator's filename.
        Assert.That(codeBehind, Does.Not.Contain("UploadedAudioFileName"));
    }

    [Test]
    public void UploadFiles_BufferedBatchIsCleanedUpOnEveryExitPath()
    {
        // Splitting HandleFileSelected around the review step removed the single `finally` that
        // used to guarantee temp-file cleanup, so every exit has to reach the replacement.
        var codeBehind = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs");

        Assert.That(codeBehind, Does.Contain("private void CleanupPendingTempFiles()"));
        Assert.That(
            System.Text.RegularExpressions.Regex.Matches(codeBehind, @"CleanupPendingTempFiles\(\);").Count,
            Is.GreaterThanOrEqualTo(4),
            "Expected cleanup from upload completion, cancel, navigation away, and disposal.");
    }

    [Test]
    public void UploadFiles_NoLongerRejectsABatchForItsFileNames()
    {
        var markup = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor");
        var codeBehind = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs");

        Assert.That(codeBehind, Does.Not.Contain("MediaFileNameRules"));
        Assert.That(codeBehind, Does.Not.Contain("Fix these invalid filenames"));
        Assert.That(markup, Does.Not.Contain("exactly one dot"));

        // Unsupported extensions are skipped with a notice instead of failing the whole batch.
        Assert.That(codeBehind, Does.Contain("_skippedFiles"));

        // The cheap gates that can still run on the request thread are untouched. The full decode
        // that used to sit alongside them moved to the audio-processing Azure Function, so this
        // deliberately no longer looks for ValidateAudioDecodeAsync - see
        // FfmpegAudioProcessorTests for where that behaviour lives now.
        Assert.That(codeBehind, Does.Contain("ContentMatchesExtension"));
        Assert.That(codeBehind, Does.Contain("MediaTransferValidator.RequireComplete"));
    }

    [Test]
    public void UploadFiles_BeforeUnloadInteropHandlesCanceledCircuit()
    {
        var codeBehind = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs");

        Assert.That(codeBehind, Does.Contain("catch (TaskCanceledException)"));
        Assert.That(codeBehind, Does.Contain("uploadFilesHelper.enableBeforeUnload"));
        Assert.That(codeBehind, Does.Contain("uploadFilesHelper.disableBeforeUnload"));
    }

    private static string ReadProjectFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine(GetRepositoryRoot(), Path.Combine(pathParts)));

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MusicSalesApp", "MusicSalesApp.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
