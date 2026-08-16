#nullable enable
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MusicSalesApp.Common.Contracts;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components.Base;
using MusicSalesApp.Components.Shared;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Components.Pages.Creator;

/// <summary>
/// Where a creator hears their timed lyrics against their own song and corrects them.
///
/// <para>
/// <b>This page is the only way a song reaches listeners now.</b> Alignment produces timings and
/// stops; nothing is published until somebody has heard it here and pressed the button. That is why
/// the transport is deliberately unrestricted - it is the creator's own song, and a 60-second preview
/// cap would make the page unusable for exactly the person who needs it.
/// </para>
///
/// <para>
/// The controls are the card mini-player's rather than the song page's: one song, so previous and
/// next mean nothing, but play, stop, volume and above all a <em>seekable</em> progress bar do.
/// Fixing a chorus means replaying the same eight bars repeatedly, and without a seek the creator
/// waits out the whole song for each attempt.
/// </para>
/// </summary>
public class LyricsTimingEditorModel : BlazorBase, IAsyncDisposable
{
    /// <summary>How far one arrow press moves a word. A twentieth of a second.</summary>
    protected const long NudgeMs = 50;

    /// <summary>Half speed is the one that matters; the rest are there so it does not look odd alone.</summary>
    protected static readonly double[] PlaybackRates = [0.5, 0.75, 1.0];

    /// <summary>
    /// Capped so a long session cannot grow without bound. Deeper undo is worth less than
    /// "Start again", which is one press and always available.
    /// </summary>
    private const int MaxUndoDepth = 50;

    [Parameter] public int SongId { get; set; }

    protected ElementReference _audioElement;
    protected ElementReference _progressBarContainer;
    protected ElementReference _volumeBarContainer;

    protected LyricsScroller? _scroller;

    protected bool _loading = true;
    protected string? _error;
    protected string _songTitle = string.Empty;
    protected string? _streamUrl;

    protected LyricsTimingsDocument? _document;
    private LyricsTimingsDocument? _pristine;
    private SongLyrics? _lyrics;

    protected bool _isPlaying;
    protected bool _isMuted;
    protected double _volume = 1d;
    protected double _currentTime;
    protected double _duration;
    protected double _playbackRate = 1d;

    protected LyricsWordSelection? _selected;
    protected string _selectedText = string.Empty;
    protected long _selectedStartMs;

    protected bool _isDirty;
    protected bool _isSaving;
    protected bool _hasUnpublishedChanges;
    protected string? _statusMessage;
    protected string _bannerClass = "alert-secondary";
    protected string _bannerMessage = string.Empty;
    protected bool _helpOpen = true;

    private readonly Stack<(int LineIndex, LyricsTimedLine Before)> _undo = new();
    protected bool _canUndo => _undo.Count > 0;

    private IJSObjectReference? _module;
    private DotNetObjectReference<LyricsTimingEditorModel>? _selfRef;
    private int? _creatorId;

    protected override async Task OnInitializedAsync()
    {
        var user = await UserManager.GetUserAsync((await AuthenticationStateProvider
            .GetAuthenticationStateAsync()).User);

        if (user is null)
        {
            _error = "You need to be signed in to edit lyric timing.";
            _loading = false;
            return;
        }

        var creator = await CreatorService.GetCreatorByUserIdAsync(user.Id);
        if (creator is null)
        {
            _error = "Only creators can edit lyric timing.";
            _loading = false;
            return;
        }

        _creatorId = creator.Id;

        // The ownership check happens in the service, against the song's own row. The page's policy
        // says "is a creator", which is emphatically not the claim "owns this song".
        var editable = await LyricsService.GetEditableTimingsAsync(SongId, creator.Id);

        switch (editable.Outcome)
        {
            case LyricsEditOutcome.NotAllowed:
                _error = "That song belongs to a different creator.";
                _loading = false;
                return;

            case LyricsEditOutcome.NotFound:
                _error = "That song could not be found.";
                _loading = false;
                return;

            case LyricsEditOutcome.NoTimings:
                _error = "This song has no lyric timings yet. Paste its lyrics from your song list "
                         + "and we'll time them for you.";
                _loading = false;
                return;
        }

        _document = editable.Document;
        _pristine = LyricsTimingEdits.CopyDocument(editable.Document!);
        _lyrics = editable.Lyrics;
        _hasUnpublishedChanges = editable.Lyrics?.HasUnpublishedChanges ?? false;

        var song = await SongMetadataService.GetByIdAsync(SongId);
        if (song is not null)
        {
            _songTitle = SongTitleHelper.GetEffectiveTitle(song.SongTitle, song.Mp3BlobPath, song.BlobPath);
            _streamUrl = AzureStorageService.GetReadSasUri(song.Mp3BlobPath, TimeSpan.FromHours(2))?.ToString();
        }

        await BuildBannerAsync(editable.IsDraft);

        _loading = false;
    }

    /// <summary>
    /// The greeting, coloured by confidence against the admin threshold.
    /// </summary>
    /// <remarks>
    /// The threshold is read now rather than stored with the song, so an admin moving it re-colours
    /// every song's banner instead of leaving old ones carrying a verdict that no longer applies. The
    /// creator is never shown the number itself - it is an admin knob and would mean nothing to them.
    /// </remarks>
    private async Task BuildBannerAsync(bool isDraft)
    {
        if (isDraft)
        {
            _bannerClass = "alert-info";
            _bannerMessage = "These are your unpublished changes. Listeners still see the last "
                             + "version you published.";
            return;
        }

        var threshold = await AppSettingsService.GetLyricsConfidenceThresholdAsync();
        var confidence = _lyrics?.Confidence ?? 0d;

        if (confidence >= threshold)
        {
            _bannerClass = "alert-success";
            _bannerMessage = "This one came out well. Have a listen — it may need no work at all.";
        }
        else
        {
            _bannerClass = "alert-warning";
            _bannerMessage = "We weren't confident about this one. Expect to do some tapping before "
                             + "you publish.";
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _loading || _document is null)
        {
            return;
        }

        _selfRef = DotNetObjectReference.Create(this);
        _module = await JS.InvokeAsync<IJSObjectReference>(
            "import", "./Components/Pages/Creator/LyricsTimingEditor.razor.js");

        await _module.InvokeVoidAsync("init", _audioElement, _selfRef, _progressBarContainer, _volumeBarContainer);
    }

    // -----------------------------------------------------------------
    // Transport
    // -----------------------------------------------------------------

    protected async Task TogglePlay()
    {
        if (_module is null) return;
        await _module.InvokeVoidAsync(_isPlaying ? "pause" : "play", _audioElement);
    }

    protected async Task StopPlayback()
    {
        if (_module is null) return;
        await _module.InvokeVoidAsync("stop", _audioElement);
    }

    protected async Task ToggleMute()
    {
        _isMuted = !_isMuted;
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setMuted", _audioElement, _isMuted);
        }
    }

    protected async Task SetPlaybackRate(double rate)
    {
        _playbackRate = rate;
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setRate", _audioElement, rate);
        }
    }

    protected async Task OnProgressBarClick(MouseEventArgs e)
    {
        if (_module is null || _duration <= 0) return;
        await _module.InvokeVoidAsync("seekToPosition", _audioElement, _progressBarContainer, e.OffsetX);
    }

    protected async Task OnVolumeBarClick(MouseEventArgs e)
    {
        if (_module is null) return;
        await _module.InvokeVoidAsync("setVolumeFromPosition", _audioElement, _volumeBarContainer, e.OffsetX);
    }

    protected double GetDisplayVolume() => _isMuted ? 0 : _volume;

    protected double GetProgressPercent() => _duration <= 0 ? 0 : (_currentTime / _duration) * 100;

    protected static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    [JSInvokable] public void UpdateTime(double t) { _currentTime = t; InvokeAsync(StateHasChanged); }
    [JSInvokable] public void UpdateDuration(double d) { _duration = d; InvokeAsync(StateHasChanged); }
    [JSInvokable] public void UpdatePlaying(bool playing) { _isPlaying = playing; InvokeAsync(StateHasChanged); }
    [JSInvokable] public void UpdateVolume(double v, bool muted) { _volume = v; _isMuted = muted; InvokeAsync(StateHasChanged); }

    // -----------------------------------------------------------------
    // Editing
    // -----------------------------------------------------------------

    protected async Task OnWordSelected(LyricsWordSelection selection)
    {
        if (_document is null) return;

        _selected = selection;

        var line = _document.Lines[selection.LineIndex];
        var word = line.Words.Count > selection.WordIndex ? line.Words[selection.WordIndex] : null;

        _selectedText = word?.Text ?? line.Text;
        _selectedStartMs = word?.StartMs ?? line.StartMs ?? 0;

        if (_scroller is not null)
        {
            await _scroller.HighlightSelectionAsync(selection.LineIndex, selection.WordIndex);
        }
    }

    protected void NudgeSelected(long deltaMs)
    {
        if (_document is null || _selected is null) return;

        PushUndo(_selected.Value.LineIndex);
        LyricsTimingEdits.NudgeWord(_document, _selected.Value.LineIndex, _selected.Value.WordIndex, deltaMs);
        AfterEdit();
    }

    protected void NudgeSelectedLineEarlier() => NudgeSelectedLine(-NudgeMs * 4);

    protected void NudgeSelectedLineLater() => NudgeSelectedLine(NudgeMs * 4);

    private void NudgeSelectedLine(long deltaMs)
    {
        if (_document is null || _selected is null) return;

        PushUndo(_selected.Value.LineIndex);
        LyricsTimingEdits.NudgeLine(_document, _selected.Value.LineIndex, deltaMs);
        AfterEdit();
    }

    /// <summary>
    /// Snapshot one line before changing it.
    /// </summary>
    /// <remarks>
    /// Per line rather than per document: a whole-document copy per keystroke would be tens of
    /// kilobytes held fifty times over, for an operation that only ever touches one line.
    /// </remarks>
    private void PushUndo(int lineIndex)
    {
        if (_document is null) return;

        _undo.Push((lineIndex, LyricsTimingEdits.CopyLine(_document.Lines[lineIndex])));

        while (_undo.Count > MaxUndoDepth)
        {
            // Stack has no "drop oldest", so rebuild without it. Rare enough not to matter.
            var kept = _undo.ToArray().Take(MaxUndoDepth).Reverse().ToList();
            _undo.Clear();
            foreach (var entry in kept) _undo.Push(entry);
        }
    }

    protected void Undo()
    {
        if (_document is null || _undo.Count == 0) return;

        var (lineIndex, before) = _undo.Pop();
        _document.Lines[lineIndex] = before;
        AfterEdit(markDirty: true);
    }

    protected void ResetAll()
    {
        if (_pristine is null) return;

        // Back to what the aligner produced, not to the last save. "Start again" has to mean the
        // thing a creator who has made a mess actually wants.
        _document = LyricsTimingEdits.CopyDocument(_pristine);
        _undo.Clear();
        _selected = null;
        AfterEdit(markDirty: true);
    }

    private void AfterEdit(bool markDirty = true)
    {
        if (_document is null) return;

        // Repaired after every edit, never validated. A creator part way through a pass has a
        // document that briefly contradicts itself, and refusing it would lose their work for being
        // untidy. Validation belongs at Publish, which is the last gate before listeners.
        LyricsTimingsValidator.Normalize(_document);

        if (markDirty)
        {
            _isDirty = true;
            _statusMessage = null;
        }

        StateHasChanged();
    }

    protected async Task PlayFromSelected()
    {
        if (_module is null || _selected is null || _document is null) return;

        var line = _document.Lines[_selected.Value.LineIndex];
        var startMs = line.StartMs ?? 0;

        await _module.InvokeVoidAsync("seekToMs", _audioElement, startMs);
        await _module.InvokeVoidAsync("play", _audioElement);
    }

    // -----------------------------------------------------------------
    // Saving
    // -----------------------------------------------------------------

    protected async Task SaveDraft()
    {
        if (_document is null || _creatorId is null || _isSaving) return;

        _isSaving = true;

        try
        {
            var result = await LyricsService.SaveDraftAsync(SongId, _creatorId.Value, _document);

            if (result.Success)
            {
                _isDirty = false;
                _hasUnpublishedChanges = true;
                _statusMessage = result.Message;
            }
            else
            {
                _error = result.Message;
            }
        }
        finally
        {
            _isSaving = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose", _audioElement);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already gone.
            }
        }

        _selfRef?.Dispose();
    }
}
