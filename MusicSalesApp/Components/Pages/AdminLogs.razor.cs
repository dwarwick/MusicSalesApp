using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using Microsoft.AspNetCore.Components;
using System.Text;
using System.Text.RegularExpressions;

#nullable enable

namespace MusicSalesApp.Components.Pages;

public partial class AdminLogsModel : BlazorBase, IAsyncDisposable
{
    private const string LogsFolder = "logs";

    protected bool _isLoading = true;
    protected string _errorMessage = string.Empty;
    protected List<string> _logFiles = new();
    protected string? _selectedLogFile;
    protected MarkupString _logContent = new MarkupString(string.Empty);
    private string? _tempFilePath;
    private bool _hasLoadedData = false;

    // Pre-compiled regexes for performance.
    // Each pattern matches both the full English word AND the Serilog bracketed short form
    // so real log lines such as  "2026-03-22 12:13:33 [INF] Message..."  are coloured correctly.
    private static readonly Regex _infoTriggerRegex = new Regex(@"\binfo\b|\[INF\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _infoGreenRegex = new Regex(@"\b(info|information)\b|\[INF\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _warningRegex = new Regex(@"\bwarning\b|\[WRN\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _errorRegex = new Regex(@"\b(error|exception)\b|\[ERR\]|\[FTL\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _quotedRegex = new Regex(@"""[^""]*""|'[^']*'", RegexOptions.Compiled);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                LoadLogFiles();
                if (_logFiles.Count > 0)
                {
                    _selectedLogFile = _logFiles[0];
                    await LoadLogFileContentAsync(_selectedLogFile);
                }
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load log files: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private void LoadLogFiles()
    {
        var logsDir = Path.Combine(Environment.ContentRootPath, LogsFolder);
        if (!Directory.Exists(logsDir))
        {
            _logFiles = new List<string>();
            return;
        }

        _logFiles = Directory.GetFiles(logsDir, "*.log")
            .Select(f => Path.GetFileName(f))
            .OrderByDescending(f => f)
            .ToList();
    }

    protected async Task OnFileSelectionChanged(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName == _selectedLogFile)
            return;

        _selectedLogFile = fileName;
        _isLoading = true;
        _logContent = new MarkupString(string.Empty);
        _errorMessage = string.Empty;
        await InvokeAsync(StateHasChanged);

        try
        {
            await LoadLogFileContentAsync(fileName);
        }
        catch (Exception ex)
        {
            _errorMessage = $"Failed to load log file: {ex.Message}";
        }
        finally
        {
            _isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task LoadLogFileContentAsync(string fileName)
    {
        DeleteTempFile();

        var logsDir = Path.Combine(Environment.ContentRootPath, LogsFolder);
        var sourcePath = Path.Combine(logsDir, fileName);

        if (!File.Exists(sourcePath))
        {
            _errorMessage = $"Log file not found: {fileName}";
            return;
        }

        // Copy to a uniquely-named temp file so we don't hold a lock on the active log file
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"logview_{Guid.NewGuid():N}.tmp");
        File.Copy(sourcePath, _tempFilePath, overwrite: false);

        var content = await File.ReadAllTextAsync(_tempFilePath);
        _logContent = BuildHighlightedMarkup(content);
    }

    private void DeleteTempFile()
    {
        if (_tempFilePath != null)
        {
            TempFileHelper.TryDelete(_tempFilePath, Logger);
            _tempFilePath = null;
        }
    }

    /// <summary>
    /// Processes raw log text into HTML markup with colored spans.
    /// Each character position is assigned a color based on priority rules;
    /// higher-priority rules (lower number) override lower-priority ones.
    ///
    /// Priority:
    ///   1 = orange  – text inside single or double quotes
    ///   2 = red     – "error" / "exception" / [ERR] / [FTL]
    ///   3 = yellow  – "warning" / [WRN]
    ///   4 = green   – "info" / "information" / [INF]
    ///   5 = blue    – text that follows "info" / [INF] until end of line
    /// </summary>
    public static MarkupString BuildHighlightedMarkup(string content)
    {
        var lines = content.Split('\n');
        var sb = new StringBuilder(content.Length * 2);

        foreach (var line in lines)
        {
            sb.Append(HighlightLine(line));
            sb.Append('\n');
        }

        return new MarkupString(sb.ToString());
    }

    public static string HighlightLine(string rawLine)
    {
        if (string.IsNullOrEmpty(rawLine))
            return string.Empty;

        var length = rawLine.Length;
        var priority = new int[length];
        var colorCode = new byte[length]; // 0=none, 1=orange, 2=red, 3=yellow, 4=green, 5=blue
        Array.Fill(priority, int.MaxValue);

        // Priority 5 (lowest): blue – text that follows "info" / [INF] until end of line
        var infoMatch = _infoTriggerRegex.Match(rawLine);
        if (infoMatch.Success)
        {
            var afterStart = infoMatch.Index + infoMatch.Length;
            for (int i = afterStart; i < length; i++)
            {
                if (5 < priority[i])
                {
                    priority[i] = 5;
                    colorCode[i] = 5;
                }
            }
        }

        // Priority 4: green – "info" / "information" / [INF]
        foreach (Match m in _infoGreenRegex.Matches(rawLine))
        {
            for (int i = m.Index; i < m.Index + m.Length; i++)
            {
                if (4 < priority[i])
                {
                    priority[i] = 4;
                    colorCode[i] = 4;
                }
            }
        }

        // Priority 3: yellow – "warning" / [WRN]
        foreach (Match m in _warningRegex.Matches(rawLine))
        {
            for (int i = m.Index; i < m.Index + m.Length; i++)
            {
                if (3 < priority[i])
                {
                    priority[i] = 3;
                    colorCode[i] = 3;
                }
            }
        }

        // Priority 2: red – "error" / "exception" / [ERR] / [FTL]
        foreach (Match m in _errorRegex.Matches(rawLine))
        {
            for (int i = m.Index; i < m.Index + m.Length; i++)
            {
                if (2 < priority[i])
                {
                    priority[i] = 2;
                    colorCode[i] = 2;
                }
            }
        }

        // Priority 1 (highest): orange – text inside single or double quotes
        foreach (Match m in _quotedRegex.Matches(rawLine))
        {
            for (int i = m.Index; i < m.Index + m.Length; i++)
            {
                priority[i] = 1;
                colorCode[i] = 1;
            }
        }

        // Build the HTML output in a single pass
        var sb = new StringBuilder(rawLine.Length * 2);
        int pos = 0;
        while (pos < length)
        {
            byte currentCode = colorCode[pos];
            int end = pos + 1;
            while (end < length && colorCode[end] == currentCode) end++;

            var segment = System.Net.WebUtility.HtmlEncode(rawLine.Substring(pos, end - pos));

            if (currentCode == 0)
            {
                sb.Append(segment);
            }
            else
            {
                var style = currentCode switch
                {
                    1 => "color:#e67e22",  // orange – quoted text
                    2 => "color:#e74c3c",  // red    – error/exception
                    3 => "color:#f0ad4e",  // yellow – warning
                    4 => "color:#2ecc71",  // green  – info/information keyword
                    5 => "color:#5b9bd5",  // blue   – message text following "info"
                    _ => string.Empty
                };
                sb.Append($"<span style=\"{style}\">{segment}</span>");
            }

            pos = end;
        }

        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        DeleteTempFile();
        await ValueTask.CompletedTask;
    }
}
