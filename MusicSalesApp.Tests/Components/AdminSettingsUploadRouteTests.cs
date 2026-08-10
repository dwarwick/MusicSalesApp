namespace MusicSalesApp.Tests.Components;

/// <summary>
/// The admin control for browser-direct creator uploads.
///
/// <para>
/// This is the rollback switch for the feature. Until it existed, turning browser uploads off meant
/// hand-writing an UPDATE against the live database — which is a poor thing to be discovering under
/// pressure, at the exact moment uploads have started failing.
/// </para>
/// </summary>
[TestFixture]
public class AdminSettingsUploadRouteTests
{
    private static string Markup => ReadProjectFile(
        "MusicSalesApp", "Components", "Pages", "Admin", "AdminSettings.razor");

    private static string CodeBehind => ReadProjectFile(
        "MusicSalesApp", "Components", "Pages", "Admin", "AdminSettings.razor.cs");

    [Test]
    public void TheSettingIsReadIntoThePage()
    {
        // Without this the control renders unchecked whatever the database says, and an admin
        // "turning it off" would write False over False while the feature stayed on.
        Assert.That(CodeBehind, Does.Contain("IsDirectToStorageUploadEnabledAsync()"));
        Assert.That(CodeBehind, Does.Contain("_originalDirectToStorageUploadEnabled = _directToStorageUploadEnabled"));
    }

    [Test]
    public void TheSettingIsWrittenBack()
    {
        // The setter existed for a while with no caller at all - the interface member, its doc
        // comment and the service method were dead code.
        Assert.That(CodeBehind, Does.Contain("SetDirectToStorageUploadEnabledAsync(_directToStorageUploadEnabled)"));
    }

    [Test]
    public void ChangingItCountsAsAnUnsavedChange()
    {
        // _hasChanges gates the Save button. Left out, an admin ticks the box, sees nothing enable,
        // and concludes the control is broken.
        var flattened = System.Text.RegularExpressions.Regex.Replace(CodeBehind, @"\s+", " ");

        Assert.That(
            flattened,
            Does.Contain("_directToStorageUploadEnabled != _originalDirectToStorageUploadEnabled"),
            "The change must reach _hasChanges, or Save stays disabled.");
    }

    [Test]
    public void CancellingRevertsIt()
    {
        var cancelBody = CodeBehind[CodeBehind.IndexOf("protected void CancelChanges()", StringComparison.Ordinal)..];

        Assert.That(
            cancelBody[..cancelBody.IndexOf('}')],
            Does.Contain("_directToStorageUploadEnabled = _originalDirectToStorageUploadEnabled"),
            "Cancel must revert every field it is offered for, or it silently keeps one.");
    }

    [Test]
    public void FlippingItIsLoggedAtWarning()
    {
        // A rollout switch, not a tuning value. "Uploads started failing at some point yesterday" is
        // only answerable if the flip left a mark, and Information is filtered out in places.
        Assert.That(CodeBehind, Does.Contain("LogWarning"));
        Assert.That(CodeBehind, Does.Contain("Direct-to-storage creator uploads turned {State}"));
    }

    [Test]
    public void TheControlIsRenderedWithSyncfusion()
    {
        // AGENTS.md: Syncfusion components rather than raw HTML, so the control picks up the
        // light/dark palette overrides the rest of the page depends on.
        var flattened = System.Text.RegularExpressions.Regex.Replace(Markup, @"\s+", " ");

        Assert.That(flattened, Does.Contain("<SfCheckBox TChecked=\"bool\""));
        Assert.That(flattened, Does.Contain("@bind-Checked=\"_directToStorageUploadEnabled\""));
    }

    [Test]
    public void TheHelpTextSaysWhenTheChangeTakesEffect()
    {
        // It is read once per page load, so a creator already sitting on the upload page keeps the
        // route they started with. An admin rolling back needs to know that the switch alone does
        // not rescue an upload already in flight.
        var flattened = System.Text.RegularExpressions.Regex.Replace(Markup, @"\s+", " ");

        Assert.That(flattened, Does.Contain("next time a creator loads the upload page"));
        Assert.That(flattened, Does.Contain("rollback"));
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
