# CLAUDE.md — MusicSalesApp.Functions

Agent-facing notes for the audio-processing Function app. `README.md` beside this file is the
operational guide (settings, provisioning, deploying, running locally) — this one covers the
architecture, the invariants, and the traps.

## What this app is

Every FFmpeg invocation in StreamTunes, plus the image work that used to run on the Blazor circuit.
Four queue triggers, no HTTP triggers, no database access.

| Function | Queue | Does |
|---|---|---|
| `ProcessAudioUpload` | `audio-transcode{-env}` | One staged creator upload → playback MP3 + duration, plus the cover art's WebP renditions. Posts live progress. |
| `ProbeAudio` | `audio-probe{-env}` | Decode an already-stored blob, produce nothing. For the media-integrity audit and nightly track-length repair. No progress — nobody is watching. |
| `MatchCoverArt` | `cover-art-match{-env}` | Pair a batch of staged cover art with the audio dropped beside it, using vision OCR. Runs **before** any song exists. Posts live progress. |
| `HandleTranscodePoison` | `audio-transcode{-env}-poison` | Report an upload whose message exhausted `maxDequeueCount` as failed. See below — this is what lets the reconciler be a backstop. |

It exists because the Blazor app runs on SmarterASP shared hosting where every FFmpeg pass blocked a
request thread. A single WAV upload cost three passes before a byte reached Azure.

## End-to-end, one upload

Three processes, in order. Nothing here is a database write from this app — it has none.

1. **Web app** (`SongUploadJobService.CreateAsync`) — validates the title, sniffs magic bytes
   (`AudioContainerSniffer`, cheap, no FFmpeg), runs the ownership/collision check, mints a
   `MediaGuid`, uploads the raw bytes to `musicuploads{-env}/{guid}/`, writes a `SongUploadJob` row,
   enqueues. **Returns before the song exists.**
2. **This app** (`ProcessAudioUploadFunction`) — downloads the source, decode-probes it (proves it
   plays *and* measures duration), transcodes to MP3 unless already MP3, sniffs the result,
   decode-probes the result, uploads `playback.mp3` back to staging. Then, if the upload had cover
   art, decodes it once and writes its WebP renditions straight into the media container. POSTs
   `AudioTranscodeResult` carrying the duration *and* the rendition widths.
3. **Web app** (`MediaProcessingCompletionService`) — copies staging → `musiccontainer{-env}/{guid}/`
   (cross-account, see below), writes the `SongMetadata` row *including the rendition widths the
   Function reported*, generates the OG image, marks the job Completed, deletes staging.

`SongMetadata` is written **only on success**, which is why no catalogue, playlist or mobile-API
query has to filter out half-built songs. In-flight state lives entirely in `SongUploadJob`.

The probe path is the same shape: `MediaIntegrityAuditService` / `TrackLengthRepairService` dispatch
messages, this app decodes, `AudioProbeResultHandler` interprets. An audit run completes on its
**last callback**, not when its Hangfire job returns.

## Invariants — break these and the failures are silent

**Throw vs. report is load-bearing.** `AudioDecodeStatus` is three-way on purpose:

- `Unplayable` → the *file* is bad. POST a failure; the creator is told. Message is done.
- `Inconclusive` → the *decoder* could not run (disk full, no memory, binary missing). **Throw**, so
  the queue retries on another instance. Blaming the upload here would quarantine good songs during
  an infrastructure blip.
- Collapsing these two either condemns good files or retries corrupt ones until they poison.

**The image path is deliberately NOT the same rule, and this asymmetry is load-bearing.** Cover-art
rendition failures never fail the song and never throw: `CoverArtRenditionGenerator` returns an empty
width set with a diagnostic code, and the song publishes with `Outcome = Playable`. There is no image
equivalent of `Inconclusive → throw`.

The reason is that the two failures cost different things. For audio, "the decoder could not run"
means the song *cannot be published*, so retrying is the only way to save it. For images, "the
encoder could not run" means the song publishes and serves its full-size master — a completely
working song — and retrying the message would re-run a transcode that already succeeded, costing
minutes to salvage a 40 KB thumbnail. Renditions are derived data that
`ImageVariantBackfillService` can rebuild at any time; a song's audio is not.

`ProcessAudioUploadImageFailureTests` pins this. If you are here because the two paths look
inconsistent: they are, on purpose, and the test will tell you so.

**Terminal callbacks throw on failure; progress callbacks never do.** `PostTranscodeResultAsync` and
`PostProbeResultAsync` must throw on non-2xx so the queue retries — that is what stops a song being
lost because the site was restarting. `PostProgressAsync` swallows everything: a failed cosmetic
update must never re-run an entire transcode.

**The Function reports facts; the web app judges.** A probe returns blob properties, detected
container, duration, and the three-way verdict. Deciding Healthy / MetadataRepairable /
ConfirmedUnplayable stays server-side so the audit's rules can change without redeploying this.

**The poison queue is the authoritative failure signal, not a timestamp.** `HandleTranscodePoison`
turns Azure's exhausted-retries event into a reported failure through the ordinary terminal callback,
so `FailAsync` does the rest. This exists because the alternative was actively harmful:
`SongUploadJobReconciler` infers death from a stale `StepUpdatedAt`, and that field is refreshed
**only** by progress pings — which swallow their own failures by design, since a cosmetic ping must
never fail a transcode. A web app restarting mid-batch is therefore indistinguishable from a dead
Function, and at the old twenty-minute timeout it failed every song in flight during a deploy,
deleting the staging that healthy Functions were still using.

Consequence to preserve: **`StalledJobTimeout` (2 h, in the web app's `MediaProcessingOptions`) must
stay well clear of the worst case time to poison** — `maxDequeueCount` x `functionTimeout`, i.e.
3 x 10 min. If the reconciler can fire first, the whole arrangement collapses back into the bug.
`PoisonHandlerBeatsTheReconcilerTests` pins it, because the relationship spans two files that never
mention each other: a `TimeSpan` in the web app and two numbers in this app's `host.json`. Changing
either of those numbers means rechecking that test.

**Progress only moves forward.** `AudioProcessingStep` values are ordinals; a receiver discards
anything at or below what it already recorded. `AudioProcessingProgressCalculator` in Common owns
the band table and is the single definition shared by the upload page, this app and the API.

**Two storage accounts, not interchangeable.** Media is on a **Premium** account, which offers no
Queue service at all — that is the only reason there are two. Queues and staging are on the Standard
account. Consequence: staging → media is a **cross-account copy needing a source SAS**, not a
same-account server-side rename.

**Staging is read/write. Media is read/write for derived artefacts only.** This app writes the WebP
renditions of a song's cover art straight into the song's GUID folder, because it has the decoded
bitmap in hand and shipping it back for the web app to re-download and re-decode would double the
work. It writes **nothing else** — the playback MP3, the original audio, the cover-art master and the
original cover art are all still copied in by `MediaProcessingCompletionService`, and this app still
has no database access.

So the rule is **"no primary blob, no row"** rather than "no write": nothing here may create,
overwrite or delete a blob a `SongMetadata` row already points at. That is the real invariant the
older "media is read-only" wording was standing in for. Two consequences worth knowing:

- Renditions are written **before** the master is copied in and before the row exists. That is safe
  because `MusicController`'s public whitelist resolves a rendition back to its master and then looks
  that master up — with no row, the renditions are simply unreachable for the few seconds involved.
- Renditions written for a job that never publishes would otherwise be orphaned **forever**. Staging
  has a 7-day lifecycle rule; the media account has none and cannot safely be given one, because its
  prefixes are the live catalogue. So `MediaProcessingCompletionService` sweeps them on the terminal
  failure path (`FailAsync`, and the already-terminal guard in `CompleteAsync`). The assembly `catch`
  still leaves them alone deliberately — that path keeps the job retryable, and the redelivery
  rewrites the same paths anyway.

## Traps specific to this project

- **`WEBSITE_RUN_FROM_PACKAGE=1` mounts the package read-only.** Executing `ffmpeg.exe` from there is
  fine; writing beside it is not. `Program.cs` points FFMpegCore's temp folder at `%TEMP%`. The web
  app's old pattern (a `fftemp` folder under the content root) would fail here.
- **`host.json` pins `batchSize: 1` / `newBatchThreshold: 0`.** The default is 24 concurrent
  executions per instance, which for CPU-bound FFmpeg means 24 transcodes fighting over one small
  VM. `batchSize: 1` makes target-based scaling add one instance per message — the entire point.
- **`functionTimeout` is 10 minutes** — the Consumption ceiling, not a preference. Cannot be raised.
- **Local temp is ~500 MB, per instance.** The disk is local to the node running the function and
  is not shared with other nodes, so scaling out *adds* disk rather than dividing it: ten queued
  songs become ten instances with ten separate disks. With `batchSize: 1` an instance only ever
  holds one song's source plus its transcode, so **the constraint is per-file, never per-batch**.
  (Azure's limits table footnotes this as "across all apps in the same App Service plan" — that is
  Dedicated-plan wording, where apps really do share a VM. It does not apply to Dynamic/Y1.)
  The upload cap is an admin setting (`AppSettingsService.GetMaxAudioUploadSizeMBAsync`, currently
  150 MB → ~170 MB peak). One file would have to approach ~400 MB to threaten the limit, and
  nothing enforces the relationship.
- **`ffmpeg.exe` is ~95 MB of a ~144 MB deployment package**, and the three Windows SkiaSharp natives
  are another ~32 MB. Every deploy takes minutes. That is inherent to bundling both binaries.
  **`TrimUnusedSkiaSharpAssets` in the csproj is load-bearing** — without it the package is 412 MB,
  because SkiaSharp ships a `.pdb` several times the size of each native plus a macOS `.dylib` this
  app can never load. If the package size jumps, check that target first.
- **Cover-art matching has no job row, deliberately.** A match batch exists only while the creator's
  upload page is waiting on it: its staged images are swept by the container's 7-day lifecycle rule,
  and a lost message costs a worse pairing rather than a lost song, because the page falls back to
  exact base-name matching when its deadline expires. The consequence is that `match-complete` has
  no free idempotency — a queue redelivery really does broadcast the same pairing twice — which is
  why the page resolves its waiting task with `TrySetResult`. The batch's creator id also has to
  round-trip through the message and back, and the receiving controller must use it for the SignalR
  group name and nothing else.
- **Queue-trigger bindings resolve through the Functions *host*.** `%MediaProcessing:*QueueName%` and
  `Connection = "StagingStorageConnectionString"` are read before the worker's `IConfiguration`
  exists — which is why every setting lives in app settings / `local.settings.json` and there is no
  `appsettings.{Environment}.json` here.

## The repo-root scripts

All in `../`, all dot-sourcing `AzureCli.ps1`.

| Script | When |
|---|---|
| `Invoke-FunctionPublish.ps1` | **Routine.** Deploys code. Never touches settings. |
| `Provision-FunctionApp.ps1` | Bootstrap / disaster recovery. Idempotent. |
| `Remove-FunctionApp.ps1` | Tear an environment down to rehearse the above. |
| `Sync-FunctionSettings.ps1` | Generate `local.settings.json` for local dev. Its only job. |
| `Get-MediaProcessingSettings.ps1`, `AzureCli.ps1` | Dot-sourced helpers. |

⚠️ **Two settings must be added by hand in the portal on the already-deployed Test and Production
apps**, because of the settings rule below: `MediaProcessing:MatchQueueName` and `OpenAI:ApiKey`.
The first is not optional — it is referenced as `%...%` from a trigger attribute, so without it the
binding cannot resolve and the **whole Function App fails to start**, not just `MatchCoverArt`. Add
both before publishing; the drift check reports them as missing, which is the safety net rather than
the plan. `OpenAI:ApiKey` genuinely is optional: without it matching falls back to filenames.

**Settings rule:** `Provision-FunctionApp.ps1` pushes app settings *only on the run that creates the
app* — a new Function App has no connection strings, so its triggers cannot bind without them.
Afterwards the **Azure portal is authoritative** and the script only reports drift. Do not add
tooling that syncs settings from files to a live app; `-ApplySettings` exists for the rare
deliberate case.

`AzureCli.ps1` carries hard-won defensive helpers — read its comments before writing another
az-driven script. `Set-StrictMode` plus the CLI's state-dependent JSON shape produced four separate
bugs in one session (`Get-JsonPath` and `Remove-NullProperty` exist because of them), and captured
output silently hides the CLI's extension-install prompt.

## Testing

Tests live in `../MusicSalesApp.Tests` (this project is referenced there; `InternalsVisibleTo` is
set). `FfmpegAudioProcessorTests` covers duration parsing and the playable/unplayable/inconclusive
classification without spawning FFmpeg. `AudioProcessingProgressCalculatorTests` pins the
progress-bar monotonicity.

Two things unit tests cannot reach, so verify them on a deployed environment: the **cross-account
SAS copy** and the **SignalR progress hub**.
