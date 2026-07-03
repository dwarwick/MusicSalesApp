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
    public void UploadFiles_PhaseTwoDisplaysUploadedMp3FileName()
    {
        var markup = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor");
        var codeBehind = ReadProjectFile("MusicSalesApp", "Components", "Pages", "Creator", "UploadFiles.razor.cs");

        Assert.That(markup, Does.Contain("@item.UploadedAudioFileName"));
        Assert.That(markup, Does.Contain("Converted from @item.AudioFileName"));
        Assert.That(codeBehind, Does.Contain("UploadedAudioFileName = normalizedName + \".mp3\""));
        Assert.That(codeBehind, Does.Contain("AudioFileName = audioFileMeta.Name"));
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
