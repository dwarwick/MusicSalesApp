namespace MusicSalesApp.Tests.Components;

public class UploadFilesProgressTests
{
    [Test]
    public void UploadFiles_UsesBlazorStateForInitialUploadProgress()
    {
        var markup = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor");
        var codeBehind = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs");

        Assert.That(markup, Does.Contain("@if (_initialUploadItems.Any())"));
        Assert.That(markup, Does.Contain("_initialUploadBatchProgress"));
        Assert.That(markup, Does.Contain("_initialUploadStatusMessage"));
        Assert.That(markup, Does.Contain("_isUploading || _isProcessingFiles"));

        Assert.That(codeBehind, Does.Contain("InitialUploadProgressUpdateInterval = TimeSpan.FromSeconds(1)"));
        Assert.That(codeBehind, Does.Contain("await InvokeAsync(StateHasChanged);"));
        Assert.That(codeBehind, Does.Not.Contain("updateInitialUploadProgress"));
        Assert.That(markup, Does.Not.Contain("startInitialUploadProgress"));
        Assert.That(markup, Does.Not.Contain("updateInitialUploadProgress"));
        Assert.That(markup, Does.Not.Contain("hideInitialUploadProgress"));
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

        // The playability preflight is untouched.
        Assert.That(codeBehind, Does.Contain("ValidateAudioDecodeAsync"));
        Assert.That(codeBehind, Does.Contain("AudioContentMatchesExtension"));
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
