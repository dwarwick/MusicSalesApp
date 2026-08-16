# Karaoke Lyrics Alignment — Implementation Plan

**Target repo:** `dwarwick/MusicSalesApp`
**New project:** `MusicSalesApp.LyricsFunctions` (Python, separate function app)
**Dev machine:** macOS (Apple Silicon or Intel) + VS Code

---

## 1. Goal

Artists upload plain-text lyrics for a song they've already uploaded. The system produces
word-level timestamps so listeners can see lyrics highlighted in time with playback,
karaoke-style.

**Input:** an existing transcoded mp3 + a plain-text lyrics blob
**Output:** a timing artifact (JSON, primary) plus an Enhanced LRC file, both derived artifacts
**Mechanism:** Azure Durable Functions orchestration in Python, doing vocal separation then
forced alignment then token-to-line mapping.

### Non-goals for v1

- No GPU. CPU-only on Flex Consumption.
- No in-browser timing editor (phase 4, deferred).
- No automatic lyrics transcription. The artist supplies the words; we only supply the timing.
- No changes to the existing C# function app beyond emitting one new queue message.

---

## 2. Existing system — constraints to respect

The C# app (`MusicSalesApp.Functions`) is **Windows Consumption, .NET 10 isolated, Functions v4**,
with `functionTimeout: 00:10:00`. It ships `ffmpeg.exe` in the deployment package. Leave it alone
apart from the one new queue emit described in §5.

### Hard architectural invariant (from `FunctionOptions.cs`)

> The web app remains the sole writer of every primary blob and the sole writer of the database;
> nothing here may create, overwrite or delete a blob a `SongMetadata` row already points at.

**The Python app must honour this.** It writes *derived* artifacts only, and reports terminal
results back to the web app via HTTP callback. It must never write to the database directly and
must never touch a primary media blob.

### Callback pattern to mirror

`Services/MediaProcessingCallbackClient.cs` posts to `CallbackBaseUrl` with a shared secret in the
`X-Media-Processing-Key` header, using:

- **Terminal result callbacks** — throw on failure (the caller needs to know)
- **Progress callbacks** — never throw (best-effort, drives SignalR UI updates)

Reproduce both behaviours in Python. Progress updates matter here: alignment takes minutes, and the
existing UI already surfaces progress via SignalR.

### Settings names already in use

`StagingStorageConnectionString`, `MediaStorageConnectionString`, `StagingContainerName`,
`MediaContainerName`, `CallbackBaseUrl`, `MediaProcessingApiKey`, and queue names under
`MediaProcessing:*`. Match these conventions in the new app so `Sync-FunctionSettings.ps1` stays
coherent.

---

## 3. Hosting decision (already made — do not re-litigate)

**Flex Consumption, Linux, Python 3.11, instance memory 4096 MB.**

Rationale, for context:

- Python on Azure Functions is Linux-only, so this cannot share the existing Windows app.
- A function app is pinned to one language runtime — this must be a **separate function app**.
- Flex has no enforced max execution timeout; Consumption's 10-minute ceiling would break Demucs.
- 4096 MB is the top Flex tier and allocates ~2 CPU cores. 2048 MB gets 1 core. Because billing is
  per GB-second, 4096 MB costs about the same per song as 2048 MB while running roughly twice as
  fast. Always pick 4096.
- Expected cost is a few cents per song beyond the free grant.

**Consequence:** the Windows `ffmpeg.exe` is useless here. A Linux `ffmpeg` binary is required —
see §6 for how to supply it.

---

## 4. Pipeline

```
[C# app: existing transcode/probe]  →  mp3 in media container
                                            │
[artist submits lyrics via web app] ────────┤
                                            ▼
                            queue: lyrics-align-jobs
                                            │
                                            ▼
                            ┌───────────────────────────────┐
                            │  Python Durable orchestration │
                            └───────────────────────────────┘
                                            │
   1. prep_audio         ffmpeg: 16 kHz mono WAV, loudnorm, silencedetect
   2. separate_vocals    Demucs htdemucs → vocals stem
   3. force_align        torchaudio MMS_FA (or stable-ts) vs artist's text
   4. map_to_lines       token alignment → per-word + per-line timings
   5. write_outputs      derived blobs + terminal callback to web app
```

### Trigger choice: start on lyrics, not on audio

The orchestration begins when **lyrics are submitted**, not when audio is uploaded. Audio and
lyrics arrive at different times and many songs will never get lyrics at all — starting on lyrics
means zero compute is spent speculatively.

(The alternative — start on audio and park on `wait_for_external_event("LyricsUploaded")` — is
more elegant but leaves orchestrations open indefinitely and accumulates task hub storage.
Rejected for v1.)

---

## 5. Change to the C# app (small)

The web app already writes the lyrics blob. When lyrics are saved, enqueue one message to a new
queue, `MediaProcessing:LyricsAlignQueueName`:

```json
{
  "songId": "…",
  "artistId": "…",
  "mp3BlobPath": "media/…/track.mp3",
  "lyricsBlobPath": "staging/…/lyrics.txt",
  "requestedUtc": "2026-08-15T…Z"
}
```

Whether this is emitted from the web app or from a C# function is your call — the web app is the
natural producer since it owns the lyrics write. Either way, the Python app is purely a consumer.

Also add a poison-queue handler mirroring `HandleTranscodePoisonFunction.cs` so failed alignments
surface the same way transcode failures already do.

---

## 6. New project layout

```
MusicSalesApp.LyricsFunctions/
├── function_app.py            # v2 model: all triggers/orchestrator/activities registered here
├── host.json
├── requirements.txt
├── local.settings.sample.json
├── activities/
│   ├── prep_audio.py
│   ├── separate_vocals.py
│   ├── force_align.py
│   ├── map_to_lines.py
│   └── write_outputs.py
├── services/
│   ├── blob_store.py          # mirrors MediaBlobStore.cs responsibilities
│   ├── callback_client.py     # mirrors MediaProcessingCallbackClient.cs semantics
│   └── options.py             # mirrors FunctionOptions.cs
├── lyrics/
│   ├── normalize.py           # tokenization, contraction expansion, punctuation
│   ├── align_map.py           # the ASR-token → lyric-line mapping algorithm
│   └── formats.py             # JSON + Enhanced LRC serialization
└── tests/
```

### Use the Python v2 programming model

Decorators (`@app.orchestration_trigger`, `@app.activity_trigger`), **not** the legacy
`function.json` folder-per-function layout. Package: `azure-functions-durable`.

### ffmpeg on Linux

Two options, in order of preference:

1. **Azure Files mount.** Flex Consumption supports mounting Azure Files shares specifically so
   apps can reach large binaries and ML models without packaging them. Put the Linux `ffmpeg`
   static build and the model weights on a share. This is the recommended path because it also
   solves the deployment-size problem below.
2. **`imageio-ffmpeg`** pip package, which bundles a binary. Simpler, but adds weight to the
   deployment package.

### Deployment size — the main technical risk

Flex Consumption is zip-deploy, not custom containers. PyTorch plus Demucs weights plus an
alignment model is multiple gigabytes and **may not fit**.

Mitigations, apply all three:

- Install the **CPU-only torch wheel** (`--index-url https://download.pytorch.org/whl/cpu`). The
  default wheel drags in CUDA and is enormous.
- **Do not bundle model weights.** Mount them from Azure Files and point
  `TORCH_HOME` / Demucs's model dir at the mount.
- Pin every version. These libraries move fast.

**Validate this early — before writing pipeline code.** Build a hello-world Flex app that does
nothing but `import torch; import demucs` and deploy it. If that won't deploy, the whole plan
shifts to Container Apps and it's better to learn that on day one.

### host.json

```json
{
  "version": "2.0",
  "extensions": {
    "durableTask": {
      "hubName": "LyricsAlignHub"
    }
  }
}
```

A distinct task hub name is required — sharing a hub with anything else produces confusing,
hard-to-diagnose failures. Make sure we also have a dev hub.

---

## 7. The orchestrator

```python
@app.orchestration_trigger(context_name="ctx")
def align_orchestrator(ctx: df.DurableOrchestrationContext):
    job = ctx.get_input()

    prepped = yield ctx.call_activity("prep_audio", job)
    yield ctx.call_activity("post_progress", {**job, "stage": "separating", "percent": 20})

    vocals = yield ctx.call_activity("separate_vocals", prepped)
    yield ctx.call_activity("post_progress", {**job, "stage": "aligning", "percent": 60})

    words = yield ctx.call_activity("force_align", {**prepped, "vocals": vocals})
    result = yield ctx.call_activity("map_to_lines", {**job, "words": words})

    yield ctx.call_activity("write_outputs", {**job, "result": result})
    return {"status": "completed", "songId": job["songId"]}
```

Notes:

- Orchestrator code must be **deterministic** — no `datetime.now()`, no random, no direct I/O.
  All of that belongs in activities.
- Use `ctx.create_timer` for any waiting, never `time.sleep`.
- Retry policy: retry `prep_audio` and `write_outputs` (transient I/O). Do **not** blindly retry
  `separate_vocals` — it's expensive, and a failure there is usually deterministic.

---

## 8. The mapping algorithm (the part worth getting right)

This is where quality is won or lost, and it's pure Python — fast, testable, no ML.

The aligner's output will not match the artist's text exactly. Handle:

1. **Normalize and tokenize** both sequences: lowercase, strip punctuation, expand contractions.
   Keep a map from each normalized token back to its original (line index, word index).
2. **Align the two token streams** with `difflib.SequenceMatcher` first (simple, good enough to
   validate the pipeline). Upgrade to Needleman-Wunsch with tuned gap penalties if accuracy
   demands it.
3. **Copy timestamps** for matched tokens. For unmatched lyric tokens, **interpolate linearly**
   between the nearest matched neighbours.
4. **Repeated choruses:** enforce monotonically increasing time and bias the DP toward the current
   position, so the second chorus maps to the later occurrence rather than snapping back to the first.
5. **Ad-libs:** aligner tokens with no lyric counterpart are dropped.
6. **Instrumental sections:** use the `silencedetect` output from step 1 as hard constraints — no
   lyric word may be placed inside a detected instrumental window. This kills a whole class of
   drift where words get smeared across a solo.
7. **Roll up to lines:** each line's start is its first word's start, its end is its last word's
   end, clamped to the track duration from ffprobe.
8. **Emit a confidence score.** Mean aligner confidence plus the ratio of interpolated to matched
   tokens. If it's below threshold, flag the song for review instead of publishing bad timings.

Write unit tests for this module with synthetic inputs — dropped words, extra words, a repeated
chorus, a long instrumental bridge. It should be the best-tested code in the project.

---

## 9. Output format

Emit **both**:

**JSON (primary, consumed by the player):**

```json
{
  "songId": "…",
  "durationMs": 214000,
  "confidence": 0.87,
  "lines": [
    {
      "text": "original line as the artist typed it",
      "startMs": 12400,
      "endMs": 15900,
      "words": [
        { "text": "original", "startMs": 12400, "endMs": 12850 }
      ]
    }
  ]
}
```

Preserve the artist's original capitalization and punctuation in `text` — normalization is for
matching only, never for display.

**Enhanced LRC (`.lrc`, secondary):** line tags with inline `<mm:ss.xx>` per-word tags. Costs
almost nothing to produce and gives you export/portability for free.

Both are derived artifacts. Write them to a derived path, never over a primary blob, then post the
terminal callback and let the web app update the database.

---

## 10. Frontend (separate phase, sketch only)

- Drive highlighting from a `requestAnimationFrame` loop reading `audio.currentTime`. The
  `timeupdate` event fires only ~4×/second — too coarse for smooth word-level fill.
- Re-derive the active line/word from `currentTime` every frame. Never accumulate your own clock;
  that's what makes drift and buffering self-correcting.
- On `seeking`/`seeked`, binary-search the timestamp array to find the active index.
- Highlighting: per-word `.sung` / `.active` / `.upcoming` classes, or a `background-clip: text`
  gradient animated by the active word's elapsed fraction.
- Accessibility: don't signal state by colour alone; keep the full lyric text available to screen
  readers; let users toggle the panel off.

---

## 11. Build order

| Phase | Deliverable | Done when |
|---|---|---|
| 0 | Deployment spike: empty Flex app importing torch + demucs | It deploys and the import succeeds |
| 1 | Project scaffold, options, blob store, callback client, queue trigger → no-op orchestration | A queued job runs end to end and posts a callback |
| 2 | `prep_audio` with Linux ffmpeg (resample, loudnorm, silencedetect) | Produces a 16 kHz mono WAV + silence map |
| 3 | `separate_vocals` + `force_align` | Produces word timestamps for a real song |
| 4 | `map_to_lines` + formats, with unit tests | JSON + LRC for a real song, timings visually sane |
| 5 | Progress callbacks, poison handling, confidence gating | Failures and low-confidence runs surface in the UI |
| 6 | Frontend player integration | Lyrics highlight in time on the site |
| 7 | Artist correction path | Artists can upload their own `.lrc` or nudge timings |

Phase 0 gates everything. Do it first.

---

## 12. Things that will bite

- **Sung vocals are much harder than speech.** Even good pipelines land ~150–300 ms average word
  onset error, with a worse tail on melisma, harmonies, rap, and dense mixes. Vocal separation
  before alignment is the single biggest accuracy lever — don't skip it to save time.
- **Plan for human correction.** Phase 7 isn't optional polish; for some songs the automatic result
  will simply be wrong. Accepting artist-supplied `.lrc` is the cheap version and worth shipping early.
- **Durable storage transactions add up.** Every orchestrator poll, activity call, and checkpoint is
  a storage operation. At low compute volume they can rival the compute bill. Keep timer intervals
  at 15–30 seconds, not 2.
- **The free grant is per subscription**, and Flex's grant (100,000 GB-s) is smaller than the
  Consumption grant the C# app enjoys. They're separate meters, but check nothing else is eating it.
- **Apple Silicon local dev.** Demucs and torch will use MPS locally and CPU in Azure. Timings you
  measure on the MacBook will not predict Azure runtimes. Benchmark in Azure before tuning.
- **Verify pricing and Flex limits against current Azure docs** before committing — instance sizes,
  timeout behaviour, and rates all change.
