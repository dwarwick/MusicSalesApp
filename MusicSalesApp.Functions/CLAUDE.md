# CLAUDE.md — MusicSalesApp.Functions

Agent-facing notes for the audio-processing Function app. `README.md` beside this file is the
operational guide (settings, provisioning, deploying, running locally) — this one covers the
architecture, the invariants, and the traps.

## What this app is

Every FFmpeg invocation in StreamTunes. Two queue triggers, no HTTP triggers, no database access.

| Function | Queue | Does |
|---|---|---|
| `ProcessAudioUpload` | `audio-transcode{-env}` | One staged creator upload → playback MP3 + duration. Posts live progress. |
| `ProbeAudio` | `audio-probe{-env}` | Decode an already-stored blob, produce nothing. For the media-integrity audit and nightly track-length repair. No progress — nobody is watching. |

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
   decode-probes the result, uploads `playback.mp3` back to staging, POSTs `AudioTranscodeResult`.
3. **Web app** (`MediaProcessingCompletionService`) — copies staging → `musiccontainer{-env}/{guid}/`
   (cross-account, see below), writes the `SongMetadata` row, generates the OG image and WebP
   renditions, marks the job Completed, deletes staging.

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

**Terminal callbacks throw on failure; progress callbacks never do.** `PostTranscodeResultAsync` and
`PostProbeResultAsync` must throw on non-2xx so the queue retries — that is what stops a song being
lost because the site was restarting. `PostProgressAsync` swallows everything: a failed cosmetic
update must never re-run an entire transcode.

**The Function reports facts; the web app judges.** A probe returns blob properties, detected
container, duration, and the three-way verdict. Deciding Healthy / MetadataRepairable /
ConfirmedUnplayable stays server-side so the audit's rules can change without redeploying this.

**Progress only moves forward.** `AudioProcessingStep` values are ordinals; a receiver discards
anything at or below what it already recorded. `AudioProcessingProgressCalculator` in Common owns
the band table and is the single definition shared by the upload page, this app and the API.

**Two storage accounts, not interchangeable.** Media is on a **Premium** account, which offers no
Queue service at all — that is the only reason there are two. Queues and staging are on the Standard
account. Consequence: staging → media is a **cross-account copy needing a source SAS**, not a
same-account server-side rename.

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
- **`ffmpeg.exe` is ~95 MB of a ~112 MB deployment package.** Every deploy uploads ~40 MB compressed
  and takes minutes. That is inherent to bundling the binary.
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
