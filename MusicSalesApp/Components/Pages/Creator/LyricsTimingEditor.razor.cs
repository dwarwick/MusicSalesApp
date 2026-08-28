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

    /// <summary>
    /// Half speed is the one that matters; the rest are there so it does not look odd alone.
    /// </summary>
    /// <remarks>
    /// A quarter is worth offering for a fast, densely-worded line, where half speed is still too
    /// quick to hear where one word ends and the next begins. It is the floor rather than an
    /// arbitrary choice: below 0.25 browsers stop playing audio at all, and <c>preservesPitch</c>
    /// keeps even this sounding like the song rather than a tape winding down.
    /// </remarks>
    protected static readonly double[] PlaybackRates = [0.25, 0.5, 0.75, 1.0];

    /// <summary>
    /// How far the seek buttons move the playhead: one second, a tenth, a hundredth.
    /// </summary>
    /// <remarks>
    /// Deliberately the same values as <see cref="EdgeStepsMs"/>, rendered in the same outward-reading
    /// order, so the two controls teach each other. They exist because no seek bar resolves a
    /// hundredth of a second at any width this page could give it, and "play from exactly where that
    /// word should land" is the thing a creator does over and over while tapping.
    /// </remarks>
    protected static readonly long[] SeekStepsMs = [1_000, 100, 10];

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
    private bool _jsInitialised;
    protected string? _error;
    protected string _songTitle = string.Empty;
    protected string? _streamUrl;

    protected LyricsTimingsDocument? _document;
    /// <summary>
    /// What this page opened with - which is the saved draft when there is one, NOT the aligner's
    /// output. "Start again" restores this; going further back means discarding the draft.
    /// </summary>
    private LyricsTimingsDocument? _pristine;

    /// <summary>Whether the revert button is waiting for a second press to confirm.</summary>
    protected bool _confirmingRevert;
    protected bool _confirmingRemove;
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
    protected long _selectedEndMs;

    /// <summary>
    /// The steps the start/end controls offer, coarsest first.
    /// </summary>
    /// <remarks>
    /// Three of them because one cannot span the range this control is used over. The case it exists
    /// for is a word given a span of 60 ms, which needs hundredths; getting there from a word that is
    /// a second out at 10 ms a press is a hundred presses. Offered as separate buttons rather than a
    /// step the creator selects first, because a mode to switch between is a thing to get wrong and
    /// these are three cheap buttons.
    /// </remarks>
    protected static readonly long[] EdgeStepsMs = [1_000, 100, 10];

    /// <summary>A step as the creator reads it: 1, 0.1, 0.01.</summary>
    protected static string FormatStep(long ms) => (ms / 1000d).ToString("0.##");

    protected bool _isDirty;
    protected bool _isSaving;
    protected bool _hasUnpublishedChanges;
    protected bool _isPublished;

    /// <summary>Why a publish was refused, in the creator's language. Empty unless one was.</summary>
    protected IReadOnlyList<string> _problems = [];
    protected string? _statusMessage;
    protected string _bannerClass = "alert-secondary";
    protected string _bannerMessage = string.Empty;


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
        _isPublished = editable.Lyrics?.Status == SongLyricsStatus.Published;

        var song = await SongMetadataService.GetByIdAsync(SongId);
        if (song is not null)
        {
            _songTitle = SongTitleHelper.GetEffectiveTitle(song.SongTitle, song.Mp3BlobPath, song.BlobPath);

            // The one place a SAS to the plaintext MP3 is still minted, and deliberately so.
            //
            // Every listener-facing surface moved to encrypted HLS, but this is not one: the page is
            // [Authorize(Policy = ManageOwnSongs)] and the service re-checks the song's own CreatorId,
            // so the only person who reaches it is the creator who uploaded the audio. A creator
            // obtaining their own master is not the threat this feature exists to address.
            //
            // Two practical reasons as well. Timing is done immediately after upload, when packaging
            // may still be queued - keying this on a package would make the editor unavailable for
            // exactly the songs most likely to need it. And word-level alignment wants the master
            // rather than a re-encode.
            _streamUrl = AzureStorageService.GetReadSasUri(song.Mp3BlobPath, TimeSpan.FromHours(2))?.ToString();
        }

        BuildBanner(editable.HasUnpublishedChanges);

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
    /// The greeting. Two states: a draft in progress, or a freshly timed song to listen to.
    /// </summary>
    /// <remarks>
    /// <b>No longer graded by confidence.</b> This used to pick one of three messages by comparing
    /// the aligner's composite score against an admin threshold, and it got the verdict wrong in the
    /// common case: the score is systematically pessimistic - alignments a creator would call perfect
    /// score in the fifties - so the page routinely opened by telling somebody their good timings
    /// needed work. A creator can hear the answer for themselves in about fifteen seconds, which is
    /// what this now asks them to do. The score is still stored, for diagnosing the aligner.
    /// </remarks>
    /// <summary>
    /// Send the creator to the songs grid with the paste box open, ready for corrected words.
    /// </summary>
    /// <remarks>
    /// A navigation rather than a paste box hosted here, so there is exactly one of those in the
    /// application. The grid already owns it, already owns what happens when a run finishes, and is
    /// where the creator ends up anyway - a second copy on this page would be a second place for the
    /// finish-and-preview handoff to go wrong.
    /// </remarks>
    /// <summary>
    /// Take these lyrics off the air, keeping every timing behind them.
    /// </summary>
    /// <remarks>
    /// No confirmation, because nothing is lost and Publish puts it straight back - a creator who
    /// presses this by mistake is one press from undoing it. Contrast <see cref="RemoveLyricsAsync"/>,
    /// which asks twice.
    /// </remarks>
    protected async Task HideFromListenersAsync()
    {
        _isSaving = true;

        try
        {
            var result = await LyricsService.UnpublishAsync(SongId, _creatorId ?? 0);
            _statusMessage = result.Message;

            if (result.Success)
            {
                _isPublished = false;
            }
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <summary>
    /// Delete these lyrics outright and go back to the songs grid.
    /// </summary>
    /// <remarks>
    /// Asks twice, following "Back to original timing", and for a stronger reason: that one discards
    /// a draft, this one discards the alignment, the draft, the pasted words and the published
    /// timings together. There is no undo behind it - the way back is to paste the words again and
    /// spend another separation pass.
    /// </remarks>
    protected async Task RemoveLyricsAsync()
    {
        if (!_confirmingRemove)
        {
            _confirmingRemove = true;
            return;
        }

        _confirmingRemove = false;
        _isSaving = true;

        try
        {
            var result = await LyricsService.RemoveAsync(SongId, _creatorId ?? 0);

            if (!result.Success)
            {
                _statusMessage = result.Message;
                return;
            }

            // Back to the grid: this page has nothing left to show, and the row it came from now
            // offers the paste box again.
            NavigationManager.NavigateTo(AppPageRoutes.CreatorSongs);
        }
        finally
        {
            _isSaving = false;
        }
    }

    protected void ReplaceLyrics() =>
        NavigationManager.NavigateTo(AppPageRoutes.CreatorSongsReplaceLyrics(SongId));

    /// <summary>Whether an administrator has taken these lyrics down.</summary>
    protected bool _adminDisabled => _lyrics?.DisabledAt is not null;

    private void BuildBanner(bool hasUnpublishedChanges)
    {
        // First, because it outranks everything else this banner could say. A creator whose lyrics
        // have been taken down needs to know that before they read advice about tapping - and needs
        // to be told rather than left to discover it by pressing Publish and being refused.
        if (_adminDisabled)
        {
            _bannerClass = "alert-danger";
            _bannerMessage = string.IsNullOrWhiteSpace(_lyrics!.DisabledReason)
                ? "An administrator has disabled these lyrics. Listeners see the cover art, and they "
                  + "can't be published until an administrator re-enables them."
                : $"An administrator has disabled these lyrics: {_lyrics.DisabledReason} Listeners "
                  + "see the cover art, and they can't be published until an administrator "
                  + "re-enables them.";
            return;
        }

        if (hasUnpublishedChanges)
        {
            _bannerClass = "alert-info";
            _bannerMessage = "These are your unpublished changes. Listeners still see the last "
                             + "version you published.";
            return;
        }

        _bannerClass = "alert-success";
        _bannerMessage = "Have a listen. Nudge anything that drifted, then press Publish — nothing "
                         + "is visible to listeners until you do.";
    }

    /// <summary>
    /// Wire up the transport once the editor is actually on screen.
    ///
    /// <para>
    /// <b>Gated on having initialised, not on <c>firstRender</c>.</b> The whole editor sits behind
    /// <c>@if (_loading)</c>, and <c>_loading</c> is only cleared after the document has been
    /// fetched - which is always later than the first render. A <c>firstRender</c> gate therefore
    /// never coincided with the markup existing, so the module was never imported and every transport
    /// control was inert.
    /// </para>
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_jsInitialised || _loading || _document is null)
        {
            return;
        }

        _jsInitialised = true;

        _selfRef = DotNetObjectReference.Create(this);
        _module = await JS.InvokeAsync<IJSObjectReference>(
            "import", "./Components/Pages/Creator/LyricsTimingEditor.razor.js");

        await _module.InvokeVoidAsync("init", _audioElement, _selfRef, _progressBarContainer, _volumeBarContainer);

        // One more render, so the scroller is handed the element references this render populated.
        // It is declared above the <audio> element it follows, so on the render that first drew both
        // it received an empty reference and deliberately declined to initialise.
        StateHasChanged();
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

    /// <summary>
    /// Move the playhead by a fixed amount, clamped to the track.
    /// </summary>
    /// <remarks>
    /// Clamped rather than trusted to the element: a negative <c>currentTime</c> throws, and seeking
    /// past the end lands the creator on an ended player when what they pressed was "a bit later".
    /// The duration is only known once the browser has told us, so before that this refuses to guess.
    /// </remarks>
    protected async Task SeekByAsync(long deltaMs)
    {
        if (_module is null || _duration <= 0)
        {
            return;
        }

        var target = Math.Clamp((_currentTime * 1000d) + deltaMs, 0d, _duration * 1000d);
        await _module.InvokeVoidAsync("seekToMs", _audioElement, target);
    }

    protected static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    /// <summary>
    /// The elapsed time to a hundredth of a second.
    /// </summary>
    /// <remarks>
    /// Only ever used for the position, never the duration. Hundredths on a number being aimed at are
    /// the whole point of the seek steps; hundredths on the track's length are noise.
    /// </remarks>
    protected static string FormatTimePrecise(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 10:00}";
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

        // Clicking the selected word again puts it down. The gesture a creator tries first, and
        // without it the word stayed selected no matter what they clicked, which reads as the
        // selection being stuck rather than as there being a particular way out of it.
        if (_selected is not null
            && _selected.Value.LineIndex == selection.LineIndex
            && _selected.Value.WordIndex == selection.WordIndex)
        {
            await ClearSelectionAsync();
            return;
        }

        _selected = selection;

        var line = _document.Lines[selection.LineIndex];
        var word = line.Words.Count > selection.WordIndex ? line.Words[selection.WordIndex] : null;

        _selectedText = word?.Text ?? line.Text;
        _selectedStartMs = word?.StartMs ?? line.StartMs ?? 0;
        _selectedEndMs = word?.EndMs ?? line.EndMs ?? 0;

        if (_scroller is not null)
        {
            await _scroller.HighlightSelectionAsync(selection.LineIndex, selection.WordIndex);
        }
    }

    /// <summary>
    /// Put the selection down again.
    ///
    /// <para>
    /// Clicking a word was a one-way door: the panel stayed on whatever was last clicked, and its
    /// outline stayed on the word, with no way to say "I am done with that one". Escape does this
    /// too, when a tap pass is not running to claim it.
    /// </para>
    /// </summary>
    protected internal async Task ClearSelectionAsync()
    {
        if (_selected is null)
        {
            return;
        }

        _selected = null;
        _selectedText = string.Empty;
        _selectedStartMs = 0;
        _selectedEndMs = 0;

        if (_scroller is not null)
        {
            await _scroller.ClearSelectionAsync();
        }

        StateHasChanged();
    }

    /// <summary>
    /// Move the start or the end of the selected word, leaving the other edge where it is.
    /// </summary>
    protected void AdjustSelectedEdge(LyricsWordEdge edge, long deltaMs)
    {
        if (_document is null || _selected is null) return;

        PushUndo(_selected.Value.LineIndex);
        LyricsTimingEdits.AdjustWordEdge(
            _document, _selected.Value.LineIndex, _selected.Value.WordIndex, edge, deltaMs);

        // Re-read rather than assuming the delta landed: the edge clamps against the word's other
        // side, and Normalize may widen the line around it, so the panel must show what the document
        // now says instead of what was asked for.
        var line = _document.Lines[_selected.Value.LineIndex];
        if (line.Words.Count > _selected.Value.WordIndex)
        {
            var word = line.Words[_selected.Value.WordIndex];
            _selectedStartMs = word.StartMs ?? _selectedStartMs;
            _selectedEndMs = word.EndMs ?? _selectedEndMs;
        }

        AfterEdit();
    }

    /// <summary>A word's span to the hundredth, which is the resolution this panel edits at.</summary>
    protected static string FormatPrecise(long ms)
    {
        if (ms < 0)
        {
            ms = 0;
        }

        var minutes = ms / 60_000;
        var seconds = (ms % 60_000) / 1000d;

        return $"{minutes}:{seconds:00.00}";
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

        // Back to what this page OPENED with, which is the saved draft when there is one rather than
        // the aligner's output. That is the right scope for a button sitting next to Undo - it
        // reverses this sitting, not work the creator deliberately saved on a previous one. Getting
        // behind a saved draft is RevertToAlignedAsync, which has to delete something and therefore
        // asks first.
        _document = LyricsTimingEdits.CopyDocument(_pristine);
        _undo.Clear();
        _selected = null;
        AfterEdit(markDirty: true);
    }

    /// <summary>
    /// Throw away the saved draft and go back to the timings the aligner produced.
    ///
    /// <para>
    /// The one thing on this page that "Start again" cannot do, because a saved draft is what the
    /// editor loads in place of the aligner's output - so once a creator has saved, every local
    /// reset lands on their own edits. Discarding the draft is what makes the original reachable
    /// again, and since the original was never overwritten there is nothing to rebuild: the aligner's
    /// document is still sitting at <c>TimingsBlobPath</c>.
    /// </para>
    ///
    /// <para>
    /// Two presses, because this deletes work the creator chose to keep and there is no undo for it -
    /// the undo stack covers edits, not the draft they were saved into.
    /// </para>
    /// </summary>
    protected internal async Task RevertToAlignedAsync()
    {
        if (_creatorId is null)
        {
            return;
        }

        if (!_confirmingRevert)
        {
            _confirmingRevert = true;
            return;
        }

        _confirmingRevert = false;
        _isSaving = true;

        try
        {
            var discarded = await LyricsService.DiscardDraftAsync(SongId, _creatorId.Value);
            if (!discarded.Success)
            {
                _statusMessage = discarded.Message;
                return;
            }

            // Re-read rather than keeping anything: with the draft gone this now returns the
            // aligner's document, which is exactly what was asked for.
            var editable = await LyricsService.GetEditableTimingsAsync(SongId, _creatorId.Value);

            if (editable.Document is null)
            {
                _statusMessage = "The draft was discarded, but the original timings could not be "
                                 + "read back. Please reload the page.";
                return;
            }

            _document = editable.Document;
            _pristine = LyricsTimingEdits.CopyDocument(editable.Document);
            _lyrics = editable.Lyrics;
            _hasUnpublishedChanges = editable.Lyrics?.HasUnpublishedChanges ?? false;
            _isPublished = editable.Lyrics?.Status == SongLyricsStatus.Published;

            _undo.Clear();
            _selected = null;
            _isDirty = false;

            BuildBanner(editable.HasUnpublishedChanges);
            _statusMessage = "Back to the timings we produced for you. Your saved changes are gone.";
        }
        finally
        {
            _isSaving = false;
            StateHasChanged();
        }
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

    [JSInvokable]
    public async Task ClearSelectionFromKeyboard() => await ClearSelectionAsync();

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


    /// <summary>
    /// Release these timings to listeners.
    ///
    /// <para>
    /// The only action on this page that changes what anybody else sees. It saves first when there
    /// are unsaved edits, so a creator who tunes and presses Publish gets what they just heard rather
    /// than the last thing they happened to save - the alternative is publishing a version that never
    /// existed on their screen.
    /// </para>
    /// </summary>
    protected internal async Task Publish()
    {
        if (_document is null || _creatorId is null || _isSaving) return;

        _isSaving = true;
        _problems = [];
        _statusMessage = null;

        try
        {
            if (_isDirty)
            {
                var saved = await LyricsService.SaveDraftAsync(SongId, _creatorId.Value, _document);
                if (!saved.Success)
                {
                    _error = saved.Message;
                    return;
                }

                _isDirty = false;
            }

            var result = await LyricsService.PublishAsync(SongId, _creatorId.Value);

            if (result.Success)
            {
                _isPublished = true;
                _hasUnpublishedChanges = false;
                _statusMessage = result.Message;
                return;
            }

            // Validation is the only gate between here and listeners, so its complaints are shown
            // rather than summarised - "these timings aren't ready" with no reason leaves a creator
            // with nothing to act on.
            _problems = result.Problems;

            if (_problems.Count == 0)
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
