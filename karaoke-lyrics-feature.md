# Karaoke lyrics — feature handoff

State of the karaoke lyric-timing feature as at branch `work/karaoke-lyrics-backend`.

**What works:** the whole loop. A creator pastes lyrics, a Python Durable Function times them, the
creator is emailed, they open a timing editor, hear the words light up against their song, correct
anything that drifts, and press Publish. Listeners then get a lyrics/art toggle on the song and
playlist pages.

**What has never been checked:** any of the display side against real audio in a browser. See
[Verified vs not](#verified-vs-not) — this is the single most important section of this document.

---

## Quick orientation

| Where | What |
|---|---|
| `MusicSalesApp.LyricsFunctions/` | The Python Durable Function. **Has its own `CLAUDE.md`** — read it before touching memory, models or concurrency. |
| `MusicSalesApp.Common/Contracts/Lyrics*.cs` | Timing DTOs, serializer, validator, edit operations, LRC writer. Pure, no infrastructure. |
| `MusicSalesApp/Services/SongLyrics*.cs`, `LyricsAlignment*.cs` | Submit, draft, publish, completion, email, reconcile |
| `MusicSalesApp/Components/Shared/LyricsScroller.*` | The scrolling lyrics component, all three modes |
| `MusicSalesApp/Components/Pages/Creator/LyricsTimingEditor.*` | The creator's tuning page |
| `MusicSalesApp/Components/Shared/LyricsEditorDialog.*` | The paste dialog (pre-existing, unchanged) |

Run the tests:

```bash
dotnet test MusicSalesApp.slnx                              # 2029 unit + 426 component
cd MusicSalesApp.LyricsFunctions && .venv/bin/python -m pytest tests/ -q   # 99
```

---

## The flow, end to end

```
1. PASTE      Creator → Lyrics button on /creator/songs → LyricsEditorDialog
              SongLyricsService.SubmitAsync writes {guid}-lyrics.txt to MEDIA,
              inserts SongLyrics(Pending) + LyricsAlignmentJob, enqueues Hangfire

2. ALIGN      LyricsAlignmentInvoker → HTTP POST → Python Durable orchestration
              (decode → chunked Demucs separation → forced alignment → map → write)
              ~8-9 minutes for a 4-minute song

3. LAND       report_lyrics_result → POST api/media-processing/lyrics-complete
              LyricsAlignmentCompletionService copies staging → media,
              writes SongLyrics as NeedsReview, enqueues the creator email

4. NOTIFY     SignalR progress throughout (only reaches an open tab)
              + LyricsAlignmentEmailService via Hangfire (reaches everyone)

5. TUNE       /creator/songs/{id}/lyrics — LyricsTimingEditor
              listen, tap-along, nudge, save draft

6. PUBLISH    SongLyricsService.PublishAsync — validate, write timings + regenerate .lrc,
              Status = Published, Version++

7. LISTEN     Song and playlist pages show an art/lyrics toggle for Published songs only
```

---

## Decisions that shaped this, and why

These are the ones worth understanding before changing anything. Each was a deliberate choice with a
rejected alternative.

### Alignment cannot publish. Only a creator can.

`Classify` never returns `Published`, at any confidence including 1.0. Every successful alignment
lands as `NeedsReview` and waits.

Machine alignment of sung vocals lands 150–300 ms out on a good day and a listener notices
immediately. No number computed from the aligner's own scores can stand in for someone hearing it
against their own song. The admin confidence threshold survives as **advice** — it chooses which of
two messages the creator reads and gates nothing a listener sees, which also means it can be tuned
freely without changing anybody's experience.

*Migration consequence:* `AddLyricsDraftAndUnpublishAutoPublished` demotes existing `Published` rows
to `NeedsReview`. Nothing is lost — the timings blob is untouched, so a creator who listens and
agrees republishes the identical file with one click. `Down()` deliberately does not reverse it.

### Edits are a draft until published

Live timings keep serving untouched while a creator experiments. The draft is a separate blob,
`{guid}-lyrics.draft.json`, and **three independent things** keep it away from listeners: it is on no
song row; `IsPubliclyReadableAsync` only matches the live paths; and `IsLyricsArtifactPath` rejects
`.draft.json` explicitly. That third guard is redundant today and exists so that loosening the
matcher later trips a red test instead of quietly making every half-finished tapping session
publicly routable.

A new alignment **discards** the draft: a draft is a set of edits to specific timings, and
re-alignment has replaced every one of them. Reapplying corrections to a document they were never
made against is worse than losing them.

### Highlighting runs in JavaScript, not C#

The obvious implementation drives it from the `timeupdate` callback every player here already uses.
It cannot work: `timeupdate` fires ~4 Hz, and on Blazor **Server** each tick is a SignalR round trip
ending in `StateHasChanged` over the whole page. Karaoke at 4 Hz, a network hop late, on a page with
a dozen playing cards, is both visibly wrong and expensive.

Blazor renders the words once with `data-l`/`data-w` attributes; a `requestAnimationFrame` loop in
`LyricsScroller.razor.js` does the rest against the audio element's clock. Three consequences:

- **The panel translates a track; it never scrolls.** `scrollIntoView({block:'center'})` scrolls every
  scrollable *ancestor* — it would yank the library grid whenever a song changed line.
- **Word clicks are one delegated listener**, not `@onclick` per word. A four-minute song is a few
  thousand words.
- **Position is re-derived from `currentTime` every frame** rather than accumulated, so buffering and
  seeks self-correct instead of drifting permanently.

### Tap-along reads the clock in the browser

An `@onkeydown` on a Server circuit is a network round trip before any C# runs — at 120–250 ms that
is most of a syllable, and it *varies with the connection*, so a creator would be calibrating against
their own latency. `audio.currentTime` is read in the same handler that saw the keypress; the trip to
.NET afterwards costs nothing.

The on-screen Tap button carries **no `@onclick`** for the same reason and is found by a data
attribute instead. There is a test asserting it stays that way — adding a handler back would look
like an obvious tidy-up and would silently make the button worse than the keyboard.

### The editor uses the card player's controls, not the song page's

One song, so previous/next mean nothing — but play, **stop**, volume and above all a **seekable**
progress bar do. Tuning a chorus means replaying the same eight bars repeatedly.

No preview restriction, hardcoded rather than relying on the `_isCreatorOfSong` check resolving: it
is the creator's own song, and a 60-second cap would put the final chorus of a four-minute track out
of reach for exactly the person the page exists for.

*This replaced an earlier plan* to extract a shared `PlayerTransport` from the song and playlist
pages and retrofit both. That plan is abandoned — see [Abandoned work](#abandoned-work).

### Email is enqueued through Hangfire, never sent inline

Both terminal paths run on the Function's callback request, which has a two-minute contract and a
documented hazard if it overruns. `SendEmailAsync` is synchronous SMTP with a 30-second timeout. It
also buys retries for the only notification that reaches a creator who closed the tab — and closing
the tab is expected, since timing takes minutes.

The most important sentence in that email is that the timings **are not live yet**. The old behaviour
published automatically, so a creator told only "your lyrics are timed" will assume listeners can
already see them and never press Publish.

### Refusals are indistinguishable

`/creator/songs/{id}/lyrics` takes an id from the URL. The ownership check is server-side in
`GetEditableTimingsAsync`, against the song's own `CreatorId`. Both "not yours" and "not a song"
redirect home by the same path — saying "that song belongs to a different creator" confirms it exists
and is owned, which is enough to walk the id space. A test asserts the two are identical.

---

## Verified vs not

**This is the part to act on first.**

### Verified by tests

- Every service path: submit, draft, publish, discard, ownership refusal, validation
- The status machine, including that no confidence publishes
- That unpublished timings 404 on **both** public routes (`Stream` and `GetStreamUrl`)
- Timing document round-trip against a fixture, including nulls surviving
- The C# LRC writer against the Python writer's verbatim output
- Chunk boundary arithmetic — that kept spans tile the timeline exactly
- Tap-along maths: line moves, previous line ends, markers skipped, undo restores
- The scroller's markup and the flattened payload handed to JS
- Toggle gating on every status, on both player pages

### Verified by hand, on Test

- One real song end to end (`five-year-plan-final.mp3`, 4:07, 411 tokens, 98 lines)
- Confidence progression across fixes: **0.000 → 0.516 → 0.519**
- Words landing inside instrumental windows: **115 → 0**
- Joined stem length exactly matching the source (247360 ms in, 247360 ms out)
- Section boundaries landing sensibly; enhanced LRC carrying per-word timings

### NOT verified — needs a browser and a real song

- **The highlighting loop.** Does the right word light up at the right moment?
- **Vertical centring and its easing.** Does the current line sit in the middle and move smoothly?
- **Seek-follow.** Does the highlight snap correctly when scrubbing, including while paused?
- **Tap-along accuracy in practice.** Does a tap land where the creator hears it?
- **The playlist page's markup wiring.** Its component cannot render in bUnit (loads in
  `OnAfterRenderAsync`), so only its gating *logic* is covered. Three tests are skipped for this.
- **Anything on a phone.** Layout, the Tap button, whether the scroller is readable at card size.

Nothing on Test has been published yet, so **no listener has ever seen this feature working.** The
52% song is sitting in `NeedsReview` and is reachable from the editor now.

---

## Suggested first session on Windows

1. `dotnet build` + both test suites, confirm green on Windows too.
2. Run the app, sign in as a creator, open `/creator/songs`.
3. Open **Timing** on the five-year-plan song. Play it. **Watch the words.** This is the moment the
   whole feature either works or does not.
4. Try tap-along on a chorus. Try half speed.
5. Press **Publish**.
6. Open the song page for that track — the lyrics/art toggle should now appear.
7. Same on a playlist containing it (the one surface with no automated markup coverage).

---

## Outstanding

| Item | Notes |
|---|---|
| **Card-mode scroller** | Built and tested as a mode, never placed on a card. Needs `MusicLibrary.razor`, which `Home.razor` also renders. The art is ~210px — scrolling lyrics there may not be readable. Was the plan's first cut candidate. |
| **Mobile app** | Untouched by design. `LyricsTimingsUrl` on the mobile DTOs and a `MobileSongMapper` change would be the start. |
| **macOS build issue** | `bin/Debug/net10.0/bin/Debug/…` nesting whenever static assets change. `Directory.Build.props` documents two prior attempts. A third — moving `RemoveNestedOutputCopy` from `BeforeTargets` to `AfterTargets` — helped but did not fix it and was reverted rather than left as a half-fix. **The ordering finding is still valid and worth keeping:** MSBuild resolves `Content` at evaluation, before any target runs, so a `BeforeTargets` cleanup deletes files the build has already globbed and *causes* "could not copy … not found". Windows never sees any of this. |
| **Storage key rotation** | Two storage account keys and `MediaProcessingApiKey` were printed in full by a failed settings push earlier in development. Still outstanding. |
| **Spike app** | A throwaway Phase 0 Function app may still exist in Azure. |
| **Per-line confidence** | Not available — `formats.py` emits confidence at document level only. Marking the least-confident lines would need a Python change. Untimed lines *are* known and could be marked instead. |

---

## Abandoned work, and why

**Extracting a shared `PlayerTransport`.** The original plan had three stages extracting the
duplicated `.player-bar` markup from the song page, playlist page and card mini-player into one
component, then retrofitting two live pages. This was dropped when the editor's requirements changed
to card-style controls: with no consumer needing the shared bar, the refactor became pure risk —
touching playback every listener uses, including a 1,960-line playlist code-behind whose bar could
not be rendered in bUnit at all.

Two artifacts survive and are worth keeping:

- `PlayerTransportRegressionTests.cs` — 16 tests pinning the existing bar's markup, including two
  asserting the 60-second preview cap renders for restricted users and **does not** for unrestricted
  ones. Useful whenever that markup is touched.
- A finding worth acting on: **`AGENTS.md` lists the player-bar classes as "identical in both files".
  Twelve of twenty-nine are not.** The playlist copy adds `flex-shrink: 0`, `min-width: 0`,
  `box-sizing: border-box` and `overflow: hidden` — flex-hardening the song page never needed because
  the playlist page has a track table competing for width. Every difference is the playlist *adding* a
  property, so consolidation is still safe, but anyone merging them on the strength of that AGENTS.md
  line would silently drop the hardening the playlist page depends on.

---

## Things that will bite

- **`?v={Version}` on the timings URL is load-bearing.** The blob path never changes between versions
  and the response is served immutable for a year. Drop it and a creator's re-publish is invisible to
  every browser that has already seen the song, permanently.
- **`Version` is the cache-buster, not the path.** Versioning the paths would accumulate dead blobs in
  an account that deliberately has no lifecycle rule.
- **`NeedsReview` is not a problem state.** It is where every successful alignment lands. The grid says
  "Not published" for that reason — "Needs review" tells a creator with a perfectly good 88% song that
  something is wrong with it.
- **The Function's callback for an unknown job returns 200 deliberately.** A non-2xx would make the
  Function retry a callback that can never be accepted, forever.
- **`Completed` from the Durable status API means "the orchestration finished", not "it worked".**
  Check `output.outcome`.
