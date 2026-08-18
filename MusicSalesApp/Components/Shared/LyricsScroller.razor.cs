#nullable enable
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Contracts;

namespace MusicSalesApp.Components.Shared;

/// <summary>How much room the scroller has, which is the only thing that varies between its homes.</summary>
public enum LyricsScrollerMode
{
    /// <summary>A song card in the library grid. Smallest type, fewest lines visible.</summary>
    Card,

    /// <summary>The song or playlist player page, in place of the cover art.</summary>
    Player,

    /// <summary>The creator's timing editor. Same rendering, plus word selection.</summary>
    Editor
}

/// <summary>
/// Scrolling lyrics with the sung word highlighted, in whichever of its three homes.
///
/// <para>
/// <b>The highlighting does not run in C#, and that is the central decision here.</b> The obvious
/// implementation drives it from the existing <c>timeupdate</c> callback, which is what every player
/// on this site already uses to move its progress bar. It cannot work for this: <c>timeupdate</c>
/// fires about four times a second, and on Blazor <em>Server</em> every one of those is a SignalR
/// round trip that ends in <c>StateHasChanged</c> over the whole page. Karaoke highlighting at 4 Hz
/// is visibly late and jerky, and paying a render diff per tick - per playing card, on a page that
/// can show a dozen - is a cost nobody would accept once they saw it.
/// </para>
///
/// <para>
/// So the component renders the words once, with <c>data-l</c>/<c>data-w</c> attributes, and hands
/// the timings to a JS module that runs a <c>requestAnimationFrame</c> loop against the audio
/// element's own clock. Blazor is not in the hot path at all. What C# keeps is everything that
/// happens at human speed: which document is loaded, which word is selected, and teardown.
/// </para>
/// </summary>
public class LyricsScrollerModel : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<LyricsScrollerModel> Logger { get; set; } = default!;

    /// <summary>The audio element these lyrics follow. Supplied by whichever player owns it.</summary>
    [Parameter, EditorRequired] public ElementReference AudioElement { get; set; }

    /// <summary>How much room this instance has.</summary>
    [Parameter] public LyricsScrollerMode Mode { get; set; } = LyricsScrollerMode.Player;

    /// <summary>
    /// The timings to display, when the caller already has them - the editor, which reads drafts
    /// through the service because unpublished timings are not fetchable over HTTP.
    /// </summary>
    [Parameter] public LyricsTimingsDocument? Document { get; set; }

    /// <summary>
    /// Where to fetch published timings instead, relative to the site root.
    ///
    /// <para>
    /// Must carry the <c>?v=</c> cache-buster. The blob path never changes between versions and the
    /// response is served with a year-long immutable cache header, so without it a creator's
    /// re-publish is invisible to every browser that has already seen the song - permanently.
    /// </para>
    /// </summary>
    [Parameter] public string? TimingsUrl { get; set; }

    /// <summary>Raised when a word is clicked, in editor mode only.</summary>
    [Parameter] public EventCallback<LyricsWordSelection> OnWordSelected { get; set; }

    /// <summary>What to show before any timings arrive.</summary>
    [Parameter] public string EmptyMessage { get; set; } = "No lyrics yet.";

    /// <summary>
    /// Identifies this instance to the JS module.
    ///
    /// <para>
    /// Needed because the library grid shares one module across every card on the page, so the module
    /// keeps its loops in a map rather than in module-level variables.
    /// </para>
    /// </summary>
    protected string InstanceId { get; } = $"ls-{Guid.NewGuid():N}";

    protected ElementReference _root;
    protected LyricsTimingsDocument? _document;

    private IJSObjectReference? _module;
    private DotNetObjectReference<LyricsScrollerModel>? _selfRef;
    private bool _initialised;
    private string? _pushedSignature;

    protected override void OnParametersSet()
    {
        // Rendered from whichever source the caller gave. A fetched document arrives later, through
        // JS, and never round-trips into C# - there is nothing here that needs to see it.
        if (Document is not null)
        {
            _document = Document;
        }
    }

    /// <summary>
    /// Initialise once the audio element actually exists, which is not necessarily the first render.
    ///
    /// <para>
    /// <b>Deliberately not gated on <c>firstRender</c>.</b> <see cref="ElementReference"/> is a struct
    /// captured when the parameter is set, and a parent that declares its <c>&lt;audio&gt;</c> after
    /// this component - as the timing editor does - has not assigned its <c>@ref</c> yet at that
    /// point, because refs are populated only once the render batch reaches the DOM. Initialising
    /// anyway hands JS a reference to nothing, and the resulting JSException takes the circuit down
    /// with it. Waiting for a populated <see cref="ElementReference.Id"/> costs one extra render and
    /// removes the ordering trap entirely.
    /// </para>
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_initialised)
        {
            if (string.IsNullOrEmpty(AudioElement.Id))
            {
                return;
            }

            _selfRef ??= DotNetObjectReference.Create(this);

            try
            {
                _module ??= await JS.InvokeAsync<IJSObjectReference>(
                    "import", "./Components/Shared/LyricsScroller.razor.js");

                await _module.InvokeVoidAsync(
                    "init",
                    InstanceId,
                    _root,
                    AudioElement,
                    _selfRef,
                    Mode == LyricsScrollerMode.Editor);

                _initialised = true;
            }
            catch (JSDisconnectedException)
            {
                // The circuit went away mid-initialisation. Nothing to clean up that dispose will not
                // handle, and nothing worth logging as an error.
                return;
            }
        }

        if (_initialised)
        {
            await PushTimingsAsync();
        }
    }

    /// <summary>
    /// Hand the module whatever it should be following now.
    ///
    /// <para>
    /// Guarded by a signature so a re-render caused by something unrelated - a parent's state change,
    /// a toggle - does not re-send a document the module already has and restart its loop mid-song.
    /// </para>
    /// </summary>
    private async Task PushTimingsAsync()
    {
        var signature = Document is not null
            ? $"doc:{Document.SongId}:{Document.DurationMs}:{Document.Lines.Count}:{DocumentStamp(Document)}"
            : $"url:{TimingsUrl}";

        if (signature == _pushedSignature || _module is null)
        {
            return;
        }

        _pushedSignature = signature;

        try
        {
            if (Document is not null)
            {
                await _module.InvokeVoidAsync("setTimings", InstanceId, Flatten(Document));
            }
            else if (!string.IsNullOrWhiteSpace(TimingsUrl))
            {
                await _module.InvokeVoidAsync("loadTimings", InstanceId, TimingsUrl);
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone. Nothing to do.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not hand lyric timings to the scroller.");
        }
    }

    /// <summary>
    /// A cheap fingerprint of the document's timings, so an edit is noticed but a re-render is not.
    /// </summary>
    private static long DocumentStamp(LyricsTimingsDocument document)
    {
        long stamp = 17;

        foreach (var line in document.Lines)
        {
            stamp = unchecked(stamp * 31 + (line.StartMs ?? -1));
            stamp = unchecked(stamp * 31 + (line.EndMs ?? -1));
        }

        return stamp;
    }

    /// <summary>
    /// The line's span as <c>2:45.5 - 2:45.6</c>, shown down the editor's left edge.
    ///
    /// <para>
    /// Tenths rather than hundredths on purpose: a creator is comparing these against what they can
    /// hear, and hundredths are past the resolution of that judgement. Tenths are also enough to make
    /// the failure this was added for obvious - a line squeezed into a few hundredths reads as
    /// <c>2:45.5 - 2:45.6</c>, where the highlight simply appears never to land on it.
    /// </para>
    /// </summary>
    protected static string FormatLineRange(LyricsTimedLine line) =>
        line.IsTimed
            ? $"{FormatStamp(line.StartMs!.Value)} - {FormatStamp(line.EndMs!.Value)}"
            : string.Empty;

    private static string FormatStamp(long ms)
    {
        if (ms < 0)
        {
            ms = 0;
        }

        var minutes = ms / 60_000;
        var seconds = (ms % 60_000) / 1000d;

        return $"{minutes}:{seconds:00.0}";
    }

    /// <summary>
    /// Flatten the document into parallel arrays of every <em>timed</em> word.
    ///
    /// <para>
    /// The module does a lookup on every animation frame, so what it wants is one ascending array of
    /// start times it can binary-search or walk, not a tree it has to descend. Sending the nested
    /// document and flattening it in JS would work equally well and put the shape in two places.
    /// </para>
    /// </summary>
    internal static FlattenedTimings Flatten(LyricsTimingsDocument document)
    {
        var starts = new List<long>();
        var ends = new List<long>();
        var lineOf = new List<int>();
        var wordOf = new List<int>();

        for (var lineIndex = 0; lineIndex < document.Lines.Count; lineIndex++)
        {
            var line = document.Lines[lineIndex];
            if (!line.IsTimed)
            {
                continue;
            }

            if (line.Words.Count == 0)
            {
                // A placed line whose words were not. It highlights as a single block, which is what
                // the markup renders for it too.
                starts.Add(line.StartMs!.Value);
                ends.Add(line.EndMs!.Value);
                lineOf.Add(lineIndex);
                wordOf.Add(0);
                continue;
            }

            for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
            {
                var word = line.Words[wordIndex];
                if (!word.IsTimed)
                {
                    continue;
                }

                starts.Add(word.StartMs!.Value);
                ends.Add(word.EndMs!.Value);
                lineOf.Add(lineIndex);
                wordOf.Add(wordIndex);
            }
        }

        return new FlattenedTimings(
            starts.ToArray(),
            ends.ToArray(),
            lineOf.ToArray(),
            wordOf.ToArray(),
            document.DurationMs);
    }

    /// <summary>
    /// Mark a word as selected.
    /// </summary>
    /// <remarks>
    /// Applied as a class by the JS module rather than by re-rendering. Selection changes on every
    /// click, and re-rendering a few thousand spans to move one outline would be a diff of the whole
    /// panel each time.
    /// </remarks>
    public async Task HighlightSelectionAsync(int lineIndex, int wordIndex)
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("setSelectedWord", InstanceId, lineIndex, wordIndex);
        }
        catch (JSDisconnectedException)
        {
            // Circuit gone.
        }
    }

    /// <summary>
    /// Drop the selection outline.
    ///
    /// <para>
    /// Reuses the module's own setter with indices no word can have: it clears the previous outline
    /// before looking up the new one, and finds nothing, which is exactly the wanted effect without
    /// a second entry point that would have to keep in step with the first.
    /// </para>
    /// </summary>
    public Task ClearSelectionAsync() => HighlightSelectionAsync(-1, -1);

    /// <summary>Called from JS when a creator clicks a word in editor mode.</summary>
    [JSInvokable]
    public async Task WordClicked(int lineIndex, int wordIndex)
    {
        if (OnWordSelected.HasDelegate)
        {
            await OnWordSelected.InvokeAsync(new LyricsWordSelection(lineIndex, wordIndex));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                // Cancels the animation frame loop and drops the module's listeners and element
                // cache. Without it a library page that has played a dozen cards leaves a dozen loops
                // running against detached audio elements.
                await _module.InvokeVoidAsync("dispose", InstanceId);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The circuit is already gone, which took the module with it.
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Lyrics scroller disposal was not clean.");
            }
        }

        _selfRef?.Dispose();
    }
}

/// <summary>Which word a creator clicked.</summary>
public readonly record struct LyricsWordSelection(int LineIndex, int WordIndex);

/// <summary>Every timed word as parallel arrays, ready for a per-frame lookup.</summary>
public sealed record FlattenedTimings(
    long[] Starts,
    long[] Ends,
    int[] LineOf,
    int[] WordOf,
    long DurationMs);
