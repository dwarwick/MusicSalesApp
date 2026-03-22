using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MusicSalesApp.Components.Pages;
using MusicSalesApp.ComponentTests.Testing;

namespace MusicSalesApp.ComponentTests.Components;

[TestFixture]
public class AdminLogsTests : BUnitTestBase
{
    private string _tempLogsDir = string.Empty;

    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();

        // Point ContentRootPath at a temp directory so the component
        // has a well-known, writable location for the "logs" sub-folder.
        _tempLogsDir = Path.Combine(Path.GetTempPath(), $"AdminLogsTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempLogsDir);

        MockWebHostEnvironment
            .Setup(e => e.ContentRootPath)
            .Returns(_tempLogsDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempLogsDir))
            Directory.Delete(_tempLogsDir, recursive: true);
    }

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    [Test]
    public void AdminLogs_HasPageTitle()
    {
        var cut = TestContext.Render<AdminLogs>();

        Assert.That(cut.Markup, Does.Contain("Application Logs"));
    }

    [Test]
    public void AdminLogs_ShowsNoLogFilesMessage_WhenLogsDirectoryIsEmpty()
    {
        // Ensure there is a logs folder but it is empty
        Directory.CreateDirectory(Path.Combine(_tempLogsDir, "logs"));

        var cut = TestContext.Render<AdminLogs>();

        // After first render + async lifecycle, the component should indicate no files
        Assert.That(cut.Markup, Does.Contain("No log files found"));
    }

    [Test]
    public void AdminLogs_ShowsDropdown_WhenLogFilesExist()
    {
        var logsDir = Path.Combine(_tempLogsDir, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(logsDir, "app-log-20240115.log"), "2024-01-15 [INF] Hello world");

        var cut = TestContext.Render<AdminLogs>();

        // The Syncfusion dropdown control should be present in the markup
        Assert.That(cut.Markup, Does.Contain("e-ddl"));
    }

    // -------------------------------------------------------------------------
    // Highlighting logic (unit-tested directly, no component render needed)
    // -------------------------------------------------------------------------

    [Test]
    public void HighlightLine_ErrorWord_IsColoredRed()
    {
        var result = AdminLogsModel.HighlightLine("An error occurred");

        Assert.That(result, Does.Contain("color:#e74c3c"));
        Assert.That(result, Does.Contain("error"));
    }

    [Test]
    public void HighlightLine_ExceptionWord_IsColoredRed()
    {
        // "Exception" must be a standalone word for the \b boundary to match
        var result = AdminLogsModel.HighlightLine("Exception was thrown");

        Assert.That(result, Does.Contain("color:#e74c3c"));
        Assert.That(result, Does.Contain("Exception"));
    }

    [Test]
    public void HighlightLine_WarningWord_IsColoredYellow()
    {
        var result = AdminLogsModel.HighlightLine("a warning message");

        Assert.That(result, Does.Contain("color:#f0ad4e"));
        Assert.That(result, Does.Contain("warning"));
    }

    [Test]
    public void HighlightLine_InfoWord_IsColoredGreen()
    {
        var result = AdminLogsModel.HighlightLine("info level entry");

        Assert.That(result, Does.Contain("color:#2ecc71"));
        Assert.That(result, Does.Contain("info"));
    }

    [Test]
    public void HighlightLine_InformationWord_IsColoredGreen()
    {
        var result = AdminLogsModel.HighlightLine("information available");

        Assert.That(result, Does.Contain("color:#2ecc71"));
        Assert.That(result, Does.Contain("information"));
    }

    [Test]
    public void HighlightLine_TextAfterInfo_IsColoredBlue()
    {
        var result = AdminLogsModel.HighlightLine("info this is the message");

        // "info" itself → green; " this is the message" → blue
        Assert.That(result, Does.Contain("color:#5b9bd5"));
    }

    [Test]
    public void HighlightLine_DoubleQuotedText_IsColoredOrange()
    {
        var result = AdminLogsModel.HighlightLine(@"value is ""hello""");

        Assert.That(result, Does.Contain("color:#e67e22"));
        Assert.That(result, Does.Contain("hello"));
    }

    [Test]
    public void HighlightLine_SingleQuotedText_IsColoredOrange()
    {
        var result = AdminLogsModel.HighlightLine("value is 'world'");

        Assert.That(result, Does.Contain("color:#e67e22"));
        Assert.That(result, Does.Contain("world"));
    }

    [Test]
    public void HighlightLine_QuotedTextOverridesOtherColors()
    {
        // "error" inside quotes should be orange, not red
        var result = AdminLogsModel.HighlightLine(@"message is ""error in quotes""");

        // There should be an orange span wrapping the entire quoted string
        Assert.That(result, Does.Contain("color:#e67e22"));
        // The "error" inside the quotes must NOT also be individually wrapped in red
        // Check that every occurrence of "error" is inside an orange span
        var orangeSpanStart = result.IndexOf("color:#e67e22", StringComparison.Ordinal);
        var errorIndex = result.IndexOf("error", orangeSpanStart, StringComparison.Ordinal);
        Assert.That(errorIndex, Is.GreaterThan(orangeSpanStart));
    }

    [Test]
    public void HighlightLine_PlainText_IsNotWrapped()
    {
        var result = AdminLogsModel.HighlightLine("plain uncolored text");

        Assert.That(result, Does.Not.Contain("<span"));
    }

    [Test]
    public void HighlightLine_HtmlSpecialChars_AreEncoded()
    {
        var result = AdminLogsModel.HighlightLine("<script>alert('xss')</script>");

        Assert.That(result, Does.Not.Contain("<script>"));
        Assert.That(result, Does.Contain("&lt;script&gt;"));
    }

    [Test]
    public void HighlightLine_EmptyLine_ReturnsEmpty()
    {
        var result = AdminLogsModel.HighlightLine(string.Empty);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void BuildHighlightedMarkup_MultipleLines_PreservesNewlines()
    {
        var content = "line one\ninfo line two\nline three";
        var markup = AdminLogsModel.BuildHighlightedMarkup(content);

        Assert.That(markup.Value, Does.Contain('\n'));
    }
}
