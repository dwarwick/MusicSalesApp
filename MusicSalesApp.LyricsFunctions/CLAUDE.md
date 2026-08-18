# CLAUDE.md — MusicSalesApp.LyricsFunctions

Agent-facing notes for the lyric-alignment Function app. `README.md` beside this file is the
operational guide (settings, provisioning, deploying, running locally) — this one covers the
architecture, the invariants, and the traps.

**Read the "Hard-won constraints" section before changing anything about memory, model choice, or
concurrency.** Every entry there cost a failed run on Azure to discover, and several of them fail in
ways that look like infrastructure problems rather than configuration errors.

## What this app is

One Azure Durable Functions orchestration, in Python, that turns *a creator's pasted lyrics* plus
*their song* into **word-level timings** — the artifact a karaoke display reads.

It is the only Python in the solution, and the only component that is not in `MusicSalesApp.slnx`
(it cannot be — `dotnet test` does not see its tests; run `pytest` separately).

| Function | Trigger | Does |
|---|---|---|
| `start_lyrics_alignment` | HTTP `POST api/lyrics/align`, `authLevel=FUNCTION` | Schedules an orchestration, returns 202 with the instance id and its management URLs |
| `align_lyrics_orchestrator` | Orchestration | Calls the work, and — critically — reports failure if it throws |
| `align_lyrics` | Activity | All the compute: decode, separate, align, map, write |
| `report_lyrics_result` | Activity | POSTs the terminal callback to the web app |

### Why HTTP-started rather than queue-triggered

A queue-triggered starter has no response channel. `create_check_status_response` hands back
`id`, `statusQueryGetUri`, `terminatePostUri` and friends, and the web app stores the instance id.
That buys two things a queue could not: the reconciler can ask Azure for a run's **actual** runtime
status instead of inferring death from a stale timestamp, and Cancel can genuinely terminate a
running orchestration. Hangfire on the web-app side supplies the durable retry a queue would have.

### Why the compute is ONE activity, not five

Durable makes no guarantee that two activities in the same orchestration run on the same instance,
so a chain of `prep_audio` → `separate_vocals` → `force_align` cannot pass local file paths between
its steps — each would find the previous one's temp files missing. The alternatives were shuttling an
8 MB WAV and a vocal stem through blob storage between every step, or keeping the chain local. Local
wins: the step boundary was hypothetical, the files are real.

## End to end, one alignment

```
Creator pastes lyrics (Blazor)
   └─ SongLyricsService.SubmitAsync
        ├─ writes {guid}-lyrics.txt to the MEDIA container   (creator content, primary blob)
        ├─ inserts SongLyrics (Pending) + LyricsAlignmentJob
        └─ Hangfire → LyricsAlignmentInvoker → HTTP POST here
                                                    │
   ┌────────────────────────────────────────────────┘
   ▼
align_lyrics (one activity, minutes)
   1. download the MP3 + the lyrics .txt
   2. prep_audio.decode_for_separation   → 44.1 kHz STEREO wav      (Demucs' native format)
   3. separate_vocals.separate           → vocal stem, IN CHUNKS    (see memory, below)
   4. prep_audio.prepare_for_alignment   → 16 kHz MONO + loudnorm + silencedetect, ON THE STEM
   5. force_align.align                  → per-word spans, model chunked / alignment global
   6. lyrics.align_map.map_to_lines      → words mapped back onto the artist's own lines
   7. lyrics.formats                     → timings.json + lyrics.lrc into STAGING
   ▼
report_lyrics_result → POST api/media-processing/lyrics-complete
   ▼
Web app: copies staging → media, writes SongLyrics as NeedsReview, emails the creator
   ▼
Creator opens the timing editor, listens, tunes, presses Publish   ← the only route to listeners
```

**Nothing this app produces is ever shown to a listener directly.** It reports facts; the web app
judges. The app cannot publish — see "The pipeline cannot publish" below.

## Hard-won constraints

Each of these was found by a failed run on Azure. None is obvious from the code alone.

### 1. Flex Consumption caps at 4096 MB. There is no larger instance.

Allowed values are **512, 2048, 4096** — ARM rejects `8192` by name. Every memory decision below
follows from this being a hard ceiling rather than a starting point.

### 2. Demucs' peak memory scales with TRACK LENGTH, not `--segment`

A 29-second clip separates comfortably; the 3 min 41 s track it was cut from is OOM-killed after
about four minutes. Halving `--segment` from 7 to 5 changed nothing, which is what identified length
rather than segment as the driver — Demucs holds full-length tensors for all four sources.

So the track is separated **in chunks** (`DEMUCS_CHUNK_SECONDS`, default 30) with a margin either
side (`DEMUCS_CHUNK_MARGIN_SECONDS`, default 5) that is separated and then **discarded**. The model
gets real audio as context across every boundary, and only the interior it was most confident about
is kept. Crossfading was the alternative and is worse: it blends two estimates of the same audio,
where discarding simply never uses the weaker one.

`separate_vocals._concatenate` checks the joined stem's length against the source and warns on more
than 250 ms of drift. That check is cheap and catches the failure that would otherwise be silent —
every piece is a valid WAV and the join always succeeds, so a boundary arithmetic error produces a
stem that is merely the wrong length, and timings that drift further out of step the longer the song
goes on.

### 3. `htdemucs` is a TRANSFORMER and refuses a segment longer than 7.8 s

```
FATAL: Cannot use a Transformer model with a longer segment than it was trained for.
       Maximum segment is: 7.8
```

`DEMUCS_SEGMENT` was 8 and every run died — **after** paying to download the model, which made a
one-character config error look like an infrastructure problem. It is 7 now. Note the direction is
the opposite of the memory concern: too *large* fails 100% of the time and immediately. Raising it
needs a non-transformer model (`mdx_extra` and friends have no such cap).
`LyricsFunctionConcurrencyTests` and a pytest case both pin this.

### 4. wav2vec2 attention is O(T²) — the alignment chunk matters as much as the separation one

At 16 kHz a frame is 20 ms, so a 120 s chunk is ~6000 frames and one attention matrix is
6000² × 16 heads × 4 bytes ≈ **2.3 GB**, on top of a ~1.2 GB model. That was the original value and
it OOM-killed the run *after* separation had already finished — the most expensive possible moment to
fail. `_CHUNK_SECONDS` is 45.

### 5. The MODEL runs in chunks; the ALIGNMENT does not. This is the subtlest bug in the app.

Chunking the alignment as well looks equivalent and is not, because **forced alignment is forced**:
CTC must emit a monotonic path consuming every target it is given, so handing a 45 s slice all 416
words of a song obliges it to fit all 416 into those 45 seconds. It does. Nothing raises. The run
reports `Aligned 416 of 416 tokens` with `isMonotonic: true` and every timing is wrong — measured on
a 4:07 track, the whole lyric landed inside the first 79 seconds with the closing line stretched
across the remaining minute and a half.

The fix is to run the acoustic model in chunks (that is what costs memory) but **concatenate the
emissions and align once over the whole song**. An emission row is one probability per vocabulary
symbol — about 30 floats — so a four-minute song is roughly 12,000 × 30, on the order of a megabyte,
against the ~300 MB of attention that produced it. Global alignment is nearly free and is the only
way the timings can be right.

Measured on the same song, before and after: confidence **0.000 → 0.519**, words landing inside
instrumental windows **115 → 0**, last word **2:34 → 3:58** against a 4:07 track.

### 6. The Demucs subprocess does not inherit the worker's import path

`sys.executable -m demucs.separate` fails with a bare
`ModuleNotFoundError: No module named 'demucs'` from a package that is demonstrably installed. On the
Functions Python worker `sys.executable` is the host's interpreter at `/usr/local/bin/python`, while
the app's dependencies live under `/home/site/wwwroot/.python_packages` — on `sys.path` for the
in-process worker, invisible to anything it spawns. `separate_vocals._run_demucs` hands the
subprocess the parent's own `sys.path` as `PYTHONPATH`, which needs no knowledge of where the
platform puts packages this year.

### 7. `maxConcurrentActivityFunctions: 1` is what makes concurrency safe

Two creators pasting at once produce two orchestrations. One Demucs run needs most of a 4 GB
instance, so a second on the same instance is an OOM kill — and the orchestrator deliberately never
retries separation, so every crowded run fails *permanently*. This setting makes the second song
scale **out** to its own instance instead. Beyond `maximumInstanceCount` (10) the work queues, which
is slower rather than fatal. Total cost is unchanged either way: Flex bills GB-seconds, so ten songs
cost the same whether they run one after another or all at once.

Pinned by `MusicSalesApp.Tests/Services/LyricsFunctionConcurrencyTests.cs`, which reads this file's
`host.json` directly rather than restating its values.

### 8. `host.json` needs `extensionBundle`, and its absence is silent

A Python app has no project file to reference NuGet packages from, so every non-built-in binding
comes from the bundle — including `durableTask`. Without it the host still **indexes** the functions
(they appear in `az functionapp function list`) but cannot bind `orchestrationTrigger`,
`activityTrigger` or `durableClient`, so the HTTP starter never reaches the dispatcher and every
request 404s. Not 401, not 500 — a 404 that looks exactly like a wrong route.

### 9. The failure code does not survive the activity → orchestrator boundary on its own

Durable re-wraps whatever an activity raises, so `LyricsPipelineError.code` is gone by the time the
orchestrator's `except` runs and only the message text remains. The code is stamped into the message
as a `[Code]` prefix and recovered by `_recover_failure_code`, **matched bracketed** — diagnostics
quote Demucs stderr and Python tracebacks verbatim, so a bare substring test would let the word
`AlignmentFailed` appearing in a stack frame relabel an unrelated failure.

Without this, every failure reaches the creator as the same generic message.

## The two audio formats, and why there are two

`prep_audio` produces **two different WAVs** and handing the wrong one to the wrong consumer is a
silent quality bug rather than an error:

| Consumer | Format | Function |
|---|---|---|
| Demucs | 44.1 kHz **stereo**, unfiltered | `decode_for_separation` |
| wav2vec2 aligner | 16 kHz **mono** + loudnorm | `prepare_for_alignment` |

These were originally one pass producing the aligner's format, which was then handed to Demucs as
well — so separation, the single biggest accuracy lever in the pipeline, ran on a 16 kHz mono downmix
it had to resample back up. Nothing failed; the stem was just quietly worse.

**`silencedetect` runs on the VOCAL STEM, not the mix**, and that is what makes it work at all. An
instrumental break is not silent in the mix — that is what makes it instrumental — so detecting
silence there finds the count-in and the fade-out and essentially nothing else, leaving the "no word
inside an instrumental window" constraint switched on and inert. On the isolated vocal, every stretch
where nobody is singing registers.

## The pipeline cannot publish

`LyricsAlignmentCompletionService.Classify` (web app) never returns `Published`, at any confidence
including 1.0. Every successful alignment lands as `NeedsReview` and waits for its creator to hear it
in the timing editor and press Publish.

The admin confidence threshold survives as **advice**: it decides which of two messages the creator
reads, and therefore whether they arrive expecting to do any work. It does not gate anything a
listener sees. A number computed from the aligner's own scores was never able to answer "are these
good enough to show to listeners" — it only ever answered "did the aligner think it did well", and
sung vocals land 150–300 ms out on a good day.

## Contracts shared with the web app, by transcription

`lyrics/constants.py` is a **hand copy** of `MusicSalesApp.Common` constants. The C# Function app and
the web app share compiled constants and cannot drift; this one can. `tests/test_constants_drift.py`
reads the C# source directly and asserts the two agree — crude, and much better than finding out in
production.

The timing document is produced here by `lyrics/formats.py::to_timing_json` and consumed in C# by
`MusicSalesApp.Common/Contracts/LyricsTimingsContracts.cs`:

```json
{ "songId": 1, "durationMs": 247360, "confidence": 0.5193,
  "lines": [ { "text": "Came home salty", "startMs": 10465, "endMs": 11685,
               "words": [ { "text": "Came", "startMs": 10465, "endMs": 10765 } ] } ] }
```

**Untimed lines keep `null` times and are kept, not dropped.** Blank separators and section markers
(`[Chorus]`, `Final Chorus`) are part of how the artist laid the song out. A non-nullable `long` on
the C# side would read every one of them as `0` — "sung at the very start" — lighting up every
heading during the intro and then failing the monotonicity check on the way back out.

`LyricsLrcWriter` in `MusicSalesApp.Common` duplicates `formats.py::to_enhanced_lrc` so the web app
can regenerate the `.lrc` when a creator publishes edited timings. Its tests assert against this
writer's **verbatim** output, including the double space a line with an untimed middle word produces.
That looks like a bug and is one; the two files agreeing matters more than tidying it.

## Dependencies

See `requirements.txt` — every version pinned exactly, because these libraries move fast and a
resolver picking a newer torch on a redeploy is how a working app stops importing.

- **`--extra-index-url .../whl/cpu`** is not optional. The default torch wheels drag in CUDA and run
  to several gigabytes; Flex is zip-deploy. There is no GPU on Flex, so nothing is lost.
- **Model weights are NOT vendored.** `htdemucs` is ~80 MB and the MMS_FA bundle is over a gigabyte.
  `TORCH_HOME` and `XDG_CACHE_HOME` point at `/mnt/models`, so the libraries download their own
  checkpoints onto the Azure Files mount on first use (~40 s) and every instance afterwards reads
  them from there.
- **ffmpeg and ffprobe** are a Linux static build on `/mnt/tools`, staged by
  `Stage-LyricsMounts.ps1`. They are 76 MB each and would not fit the package alongside torch. The
  SMB mount presents 0777 and honours the executable bit, so they run straight off it.

### The mounts are SHARED between Test and Production

`lyrics-models` and `lyrics-tools` are named with bare literals and both environments resolve
`StagingStorageConnectionString` to the same storage account. So Production inherits whatever Test
downloads — no copy step, nothing to remember at go-live. That is safe because the weights are
immutable and content-addressed by the libraries' own cache layouts.

The task hub name **is** suffixed per environment (`LyricsAlignHub` / `LyricsAlignHubDev`) precisely
because it is the opposite case: shared orchestration state means two environments stealing each
other's runs.

`Remove-LyricsFunctionApp.ps1` refuses to delete the shared shares or the shared
`lyrics-deployment` container while the sibling environment's app still exists.

## Settings

Pushed by `Get-LyricsProcessingSettings.ps1` / `Provision-LyricsFunctionApp.ps1`. Notable ones:

| Setting | Why it is what it is |
|---|---|
| `DEMUCS_SEGMENT=7` | Transformer cap is 7.8. Above it, every run fails. |
| `DEMUCS_CHUNK_SECONDS=30` / `_MARGIN_SECONDS=5` | Bounds memory by chunk rather than track length |
| `OMP_NUM_THREADS` / `MKL_NUM_THREADS=4` | Measured from the deployed app — a 4096 MB Flex instance reports four cores, not two |
| `LyricsTaskHubName` | Per-environment. A literal would put Test and Production on one hub. |
| `TORCH_HOME=/mnt/models/torch`, `XDG_CACHE_HOME=/mnt/models` | Weights land on the mount, not in the package |
| `FFMPEG_BINARY=/mnt/tools/ffmpeg` | ffprobe is found by string-replacing `ffmpeg` in this path |
| `MediaProcessing__*` | Double underscore, not `:`. **Flex rejects `:` in setting names outright.** |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | The deprecated `APPINSIGHTS_INSTRUMENTATIONKEY` alone wires up nothing on Flex, and the failure is silent — queries return zero rows rather than an error, so the app reads as quiet rather than unmonitored |

Flex also **rejects** `FUNCTIONS_WORKER_RUNTIME` and `FUNCTIONS_EXTENSION_VERSION` as app settings —
not ignores, rejects, failing the whole push. They stay in the shared hashtable because
`local.settings.json` genuinely needs them for `func start`; only the Azure push filters them out.

## Testing

```bash
cd MusicSalesApp.LyricsFunctions
python3 -m venv .venv
.venv/bin/python -m pip install pytest
.venv/bin/python -m pytest tests/ -q          # 99 tests, no torch required
```

Every test runs without torch, ffmpeg, Azure or a model. That is deliberate — a test that needs a
model to run is a test nobody runs. The mapping algorithm (`lyrics/align_map.py`) is where alignment
quality is actually won and it is pure Python; `test_align_map.py` exercises dropped words, extra
words, repeated choruses and instrumental bridges against synthetic aligner output.

`tests/test_separate_chunking.py` stubs ffmpeg and Demucs entirely and asserts the boundary
arithmetic — that the kept spans tile the timeline exactly. That is the one piece of the separation
stage that can be wrong *quietly*.

## Reading a failed run

1. **App Insights first** — `traces | where timestamp > ago(30m)`. The app narrates each stage
   (`Decoded … ms`, `Separated Xs-Ys (chunk N…)`, `Joined N pieces`, `Aligned N of M tokens`).
2. **`python exited with code 137` is an OOM kill**, not an app error — SIGKILL, no traceback. It
   means something in this file's memory section has been changed or a longer song than any tested.
3. **The Durable status payload** carries the outcome even when the orchestration "completed":
   `statusQueryGetUri` returns `runtimeStatus: Completed` with an `output` whose `outcome` may be
   `Unusable` or `Inconclusive`. Completed means "the orchestration finished", not "it worked".
4. `Unusable` = tell the creator, do not retry unchanged. `Inconclusive` = the tooling failed rather
   than the submission, so a Re-run is worth offering.

## Deploying

```bash
pwsh ./Provision-LyricsFunctionApp.ps1 -Environment Test -ApplySettings
pwsh ./Stage-LyricsMounts.ps1 -Environment Test          # ffmpeg; weights self-download
pwsh ./Invoke-FunctionPublish.ps1 -ProjectPath ./MusicSalesApp.LyricsFunctions \
     -FunctionAppName streamtunes-lyrics-test -Runtime python
```

The publish warns that the local Python version differs from the app's 3.11. It is harmless — Flex
builds remotely with Oryx, which is why torch and demucs resolve correctly in Azure despite a
mismatched local interpreter.

**App settings changes restart the worker, and a run started immediately afterwards can still be the
old worker with the old values.** That cost one wasted run diagnosing a setting that had, on paper,
just been corrected. Allow a minute.
