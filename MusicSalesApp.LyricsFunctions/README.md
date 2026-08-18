# MusicSalesApp.LyricsFunctions

Word-level karaoke timings for a song, from the lyrics the artist pasted and the audio they already
uploaded. Python on Linux, Flex Consumption, Durable Functions.

**This project is not in `MusicSalesApp.slnx`** and cannot be — it is Python. `dotnet test` does not
see its tests; run `pytest` (below) separately.

## Why it is a second Function app

`MusicSalesApp.Functions` already exists and owns every FFmpeg invocation in StreamTunes. It could
not host this: a Function app is pinned to one language runtime, Demucs and the forced aligner are
PyTorch, and Python on Azure Functions is Linux-only. So this is a separate app on separate hosting,
sharing both storage accounts and the same callback secret.

## How a run happens

1. A creator pastes lyrics on `/creator/songs`. The web app writes them to the **media** container
   (creator content, so a primary blob, so the web app's to write), records a `LyricsAlignmentJob`
   row, and enqueues a Hangfire job.
2. The Hangfire job POSTs to `api/lyrics/align` here with an `x-functions-key`. The starter schedules
   an orchestration and answers with its **instance id** plus `statusQueryGetUri` and
   `terminatePostUri`; the web app stores all three on a `DurableFunctionTask` row.
3. `align_lyrics` downloads the MP3 and the lyrics, prepares the audio, separates the vocal, aligns,
   maps the result back onto the artist's own lines, and writes `timings.json` and `lyrics.lrc` to
   **staging**.
4. It POSTs the result to `api/media-processing/lyrics-complete`. The web app copies both artifacts
   into the media container, decides whether the confidence clears the published threshold, and
   updates the song's `SongLyrics` row.

### Why HTTP rather than a queue

The audio pipeline is queue-triggered. This one is not, and the reason is the instance id: a
queue-triggered starter has no response channel, so nothing could ever be handed back. With the id
recorded, the web app's reconciler can **ask Azure** what happened rather than inferring death from a
stale timestamp — which is what lets it tell a failed run from a cancelled one from a run that
succeeded but whose callback was lost. Hangfire supplies the durable retry the queue used to.

## Invariants

- **The web app is the sole writer of the database and of every primary blob.** This app reads media,
  writes only to staging, and reports. It never touches a blob a row already points at.
- **Terminal callbacks raise on a non-2xx; progress callbacks swallow everything.** The first is what
  makes the orchestrator's retry meaningful; the second is what stops a cosmetic ping costing a
  forty-minute run.
- **The Function reports facts; the web app judges.** Confidence is a number here. Whether it is good
  enough to show a listener is decided server-side against an admin-tunable threshold, so the rules
  can change without redeploying the slowest component in the system.
- **A failed orchestration tells nobody by itself.** Its trigger message was deleted the moment the
  run was scheduled, so there is no poison queue and no platform event. The `try/except` in
  `align_lyrics_orchestrator` is the only prompt detector; the web app's reconciler is the backstop.

## Traps

- **The compute is one activity, not five.** Durable does not guarantee two activities run on the
  same instance, so a chain passing local file paths between `prep` → `separate` → `align` would find
  its temp files missing. The alternative was shuttling an 8 MB WAV and a vocal stem through blob
  storage between every step. `karaoke-lyrics-plan.md` §7 sketches the chain version; it does not
  work as written.
- **`LyricsTaskHubName` must differ between Test and Production.** They share one storage account —
  that is why the provisioning script *merges* its lifecycle rule rather than replacing it — so a
  shared hub name has the two environments stealing each other's orchestrations.
- **`WEBSITE_RUN_FROM_PACKAGE` must NOT be set on Flex.** It breaks Flex's own deployment mechanism.
  The C# app sets it and the provisioning script must not copy that across.
- **Model weights are not in the package.** CPU-only torch is already several hundred megabytes;
  htdemucs plus the MMS_FA bundle would put a zip deployment well past what it can carry. They live
  on an Azure Files mount, with `TORCH_HOME` and `XDG_CACHE_HOME` pointed at it.
- **`--extra-index-url` in `requirements.txt` is load-bearing.** Without it pip resolves CUDA wheels,
  which are enormous and useless — Flex has no GPU.
- **`maxConcurrentActivityFunctions: 1`.** The Durable equivalent of the C# app's `batchSize: 1`.
  Without it one 4 GB instance runs several separations at once and is killed for memory, and
  separation is the one step the orchestrator deliberately does not retry.
- **`lyrics/constants.py` is a hand copy.** Everything else crossing a process boundary in this
  repository is shared by compilation. `tests/test_constants_drift.py` reads the C# source and
  asserts they agree — if you change one side, change the other.

## Running the tests

Everything worth testing here is pure Python — the mapping algorithm, tokenisation, the output
formats, and the constants-drift check. None of it needs torch, ffmpeg, Azure or a model.

```bash
cd MusicSalesApp.LyricsFunctions
python3 -m venv .venv
.venv/bin/python -m pip install pytest
.venv/bin/python -m pytest tests/ -q
```

`tests/test_align_map.py` is deliberately the densest file in the project: dropped words, ad-libs,
repeated choruses, long instrumental bridges, contractions, non-monotonic aligner output and
degenerate input. Everything the aligner does wrong in practice, asserted against synthetic input so
the suite runs in milliseconds.

## Local development

`func start` needs `local.settings.json`, generated from the web app's per-environment appsettings —
never hand-edited, so the storage connection strings have one source of truth. See
`local.settings.sample.json` for what each value is and why.

Running the full pipeline locally needs a Linux/macOS `ffmpeg` on `PATH` and will download model
weights on first use. **Timings measured on a developer machine do not predict Azure**: torch uses
MPS on Apple Silicon and CPU on Flex. Benchmark in Azure before tuning anything.

## Deploying

```
pwsh ./Provision-LyricsFunctionApp.ps1 -Environment Test    # bootstrap / disaster recovery
pwsh ./Invoke-FunctionPublish.ps1 -FunctionAppName streamtunes-lyrics-test -Runtime python
```

After the provisioning run, copy the function key it prints into the web app's
`appsettings.{Environment}.json` as `LyricsFunctions:FunctionKey`, alongside
`LyricsFunctions:BaseUrl`. Without both, the web app reports lyric timing as unavailable and
everything else carries on working.
