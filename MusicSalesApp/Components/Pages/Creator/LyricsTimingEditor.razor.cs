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

    protected bool _isRecording;

    /// <summary>
    /// The line the next tap will re-time, or -1 when there is nothing left to tap.
    /// </summary>
    protected int _recordLineIndex = -1;

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

    /// <summary>
    /// The document as edited so far.
    /// </summary>
    /// <remarks>
    /// Exposed to the test assembly only. A tap pass is asserted on the timings it produces - that a
    /// line moved to the tapped moment, that the previous line was ended, that undo restored it - and
    /// none of that is visible in rendered markup, which shows words rather than milliseconds.
    /// </remarks>
    internal LyricsTimingsDocument? EditedDocument => _document;

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
            SendHome();
            return;
        }

        var creator = await CreatorService.GetCreatorByUserIdAsync(user.Id);
        if (creator is null)
        {
            SendHome();
            return;
        }

        _creatorId = creator.Id;

        // THE SONG ID COMES FROM THE URL, so this is the whole authorisation story for this page.
        // The route's policy establishes "is a creator", which is not the claim "owns song 412" -
        // and the id is a small integer somebody can trivially change in the address bar.
        //
        // The check itself is made inside the service, against the song's own CreatorId, following
        // SubmitAsync: the destination is derived from the song's record rather than from anything
        // the caller supplied, so an authenticated creator cannot reach another's work by asking
        // nicely.
        var editable = await LyricsService.GetEditableTimingsAsync(SongId, creator.Id);

        switch (editable.Outcome)
        {
            case LyricsEditOutcome.NotAllowed:
            case LyricsEditOutcome.NotFound:
                // Both go home, and they go home the SAME way on purpose. Telling somebody "that
                // song belongs to a different creator" confirms it exists and is owned - which is
                // enough to walk the id space and learn how many songs the site has and which ones
                // are real. Refusing identically for "not yours" and "not a song" leaves nothing to
                // learn from trying.
                Logger.LogWarning(
                    "Creator {CreatorId} was refused the timing editor for song {SongId} ({Outcome}).",
                    creator.Id,
                    SongId,
                    editable.Outcome);

                SendHome();
                return;

            case LyricsEditOutcome.NoTimings:
                // Not a refusal - they own this song, it simply has nothing timed yet. Saying so is
                // useful, and reveals nothing they did not already know about their own song.
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
    /// Leave, without saying why.
    /// </summary>
    /// <remarks>
    /// <c>forceLoad: false</c> so it stays a client-side navigation - a full reload would work but
    /// costs a round trip and a flash of white for something that is not an error the user needs to
    /// dwell on. The reason is logged rather than displayed.
    /// </remarks>
    private void SendHome() => NavigationManager.NavigateTo(AppPageRoutes.Home, forceLoad: false);

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

    protected internal void Undo()
    {
        if (_document is null || _undo.Count == 0) return;

        var (lineIndex, before) = _undo.Pop();
        _document.Lines[lineIndex] = before;
        AfterEdit(markDirty: true);
    }

    protected internal void ResetAll()
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
    // Tap-along
    // -----------------------------------------------------------------

    /// <summary>
    /// Arm the tap pass, starting from wherever the song currently is.
    ///
    /// <para>
    /// Deliberately not from the top. Alignment usually drifts in one section rather than uniformly,
    /// and the instructions promise "you don't have to do the whole song — just the part that's
    /// wrong", so a creator can seek to the chorus, press this, and tap from there.
    /// </para>
    /// </summary>
    protected internal async Task StartRecording()
    {
        if (_document is null) return;

        _recordLineIndex = FindNextTimedLineFrom((long)(_currentTime * 1000));

        if (_recordLineIndex < 0)
        {
            _statusMessage = "There are no more lines after this point to tap.";
            return;
        }

        _isRecording = true;
        _statusMessage = null;

        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setRecording", true);

            // Playing is the point - a tap pass against a stopped song would be tapping to silence.
            await _module.InvokeVoidAsync("play", _audioElement);
        }
    }

    protected internal async Task StopRecording()
    {
        _isRecording = false;
        _recordLineIndex = -1;

        if (_module is not null)
        {
            await _module.InvokeVoidAsync("setRecording", false);
        }

        StateHasChanged();
    }

    [JSInvokable]
    public async Task StopRecordingFromKeyboard() => await StopRecording();

    /// <summary>
    /// One tap: this line starts now, and the pass moves to the next.
    /// </summary>
    /// <param name="atMs">
    /// Read from the audio element inside the keydown handler that saw the press, so it is the moment
    /// the creator heard - not the moment the message reached the server.
    /// </param>
    [JSInvokable]
    public async Task RecordLineTap(double atMs)
    {
        if (!_isRecording || _document is null || _recordLineIndex < 0)
        {
            return;
        }

        PushUndo(_recordLineIndex);
        LyricsTimingEdits.RetimeLine(_document, _recordLineIndex, (long)atMs);

        // RetimeLine also ends the PREVIOUS line here, which is what makes one pass down the song
        // produce a coherent result instead of every earlier line still running underneath.
        _recordLineIndex = FindNextTimedLineAfter(_recordLineIndex);

        if (_recordLineIndex < 0)
        {
            await StopRecording();
            _statusMessage = "That's the last line. Have a listen, then publish when you're happy.";
        }

        AfterEdit();
    }

    /// <summary>The first timed line starting at or after <paramref name="fromMs"/>.</summary>
    private int FindNextTimedLineFrom(long fromMs)
    {
        if (_document is null) return -1;

        for (var i = 0; i < _document.Lines.Count; i++)
        {
            var line = _document.Lines[i];
            if (line.IsTimed && line.StartMs >= fromMs)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The next timed line after <paramref name="lineIndex"/>, skipping section markers.</summary>
    private int FindNextTimedLineAfter(int lineIndex)
    {
        if (_document is null) return -1;

        for (var i = lineIndex + 1; i < _document.Lines.Count; i++)
        {
            // Untimed lines are markers and blanks. Nobody sings them, so nobody taps them - stopping
            // on one would leave the creator waiting for a cue that never comes.
            if (_document.Lines[i].IsTimed)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The line the creator should be tapping next, for the on-screen prompt.</summary>
    protected string NextLineToTap =>
        _document is not null && _recordLineIndex >= 0 && _recordLineIndex < _document.Lines.Count
            ? _document.Lines[_recordLineIndex].Text
            : string.Empty;

    /// <summary>Arrow-key nudge of the selected word. Direction only; the step is fixed.</summary>
    [JSInvokable]
    public Task NudgeSelectedFromKeyboard(int direction)
    {
        NudgeSelected(direction < 0 ? -NudgeMs : NudgeMs);
        return Task.CompletedTask;
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
