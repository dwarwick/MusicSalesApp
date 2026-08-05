# MusicSalesApp.Functions

FFmpeg audio processing for StreamTunes, moved off the web server.

This file is the operational guide — settings, provisioning, deploying, running locally.
For the architecture, the end-to-end flow and the invariants that must not be broken, see
[CLAUDE.md](CLAUDE.md) beside it.

## Why this exists

The Blazor app runs on SmarterASP.NET shared hosting, where every FFmpeg pass ran synchronously
inside the request thread. Uploading one WAV cost three full passes — decode-validate the source,
transcode to MP3, decode-validate the result — before a byte reached Azure, which is what produced
the long freeze creators saw after "100% received". Two Hangfire jobs decoded as well: the
media-integrity audit re-decodes every playback blob in the catalogue, and the nightly track-length
repair.

All of that now runs here, on instances that scale out horizontally, so several creators' songs
process in parallel and the web server never runs FFmpeg at all.

## The two functions

| Function | Queue | Does |
|---|---|---|
| `ProcessAudioUpload` | `audio-transcode{-env}` | Transcodes one staged creator upload to MP3 and reports its duration. Posts live progress as it goes. |
| `ProbeAudio` | `audio-probe{-env}` | Decodes an already-stored playback blob without producing anything, for the audit and track-length repair jobs. No progress — nobody is watching. |

Neither function touches the database. The site and its SQL Server are on shared hosting and are not
reachable from here, so everything this app learns goes back through
`api/media-processing/*` on the web app, authorised by the `X-Media-Processing-Key` header.

**The Function reports raw facts; the web app makes every judgement.** A probe returns blob
properties, the detected container, a decoded duration and a three-way playable/unplayable/
inconclusive verdict — deciding what counts as healthy, or when to quarantine a song, stays server
side so those rules can change without redeploying this.

## Two storage accounts

Song media lives on a **Premium** account, and no premium account type offers the Queue service, so
the queues and the upload staging container are on the **Standard general-purpose** account. Media
never moves. This app therefore needs both connection strings:

- `StagingStorageConnectionString` — Standard: queues, and reading/writing staged upload blobs.
- `MediaStorageConnectionString` — Premium: read-only, for the probe path.

## Hosting

Windows Consumption (Y1), .NET 10 isolated worker.

- Windows because `ffmpeg.exe` is a Windows binary and travels in the deployment package. Linux
  Consumption is being retired and does not support .NET 10 anyway.
- `WEBSITE_RUN_FROM_PACKAGE=1` mounts the package **read-only**. Running ffmpeg from there is fine;
  writing beside it is not, which is why `Program.cs` points FFMpegCore's temporary folder at
  `%TEMP%` rather than the content root the way the web app used to.
- `functionTimeout` is 10 minutes — the Consumption ceiling, not a preference. It cannot be raised.
- Local temp is ~500 MB and it is **per instance** — the disk is local to the node and not shared
  with other nodes, so ten queued songs get ten separate disks rather than splitting one. Combined
  with `batchSize: 1`, an instance only ever holds one song, so the limit is per-file, not
  per-batch. The upload cap is an admin setting
  (`AppSettingsService.GetMaxAudioUploadSizeMBAsync`), currently 150 MB → ~170 MB peak; a single
  file would have to approach ~400 MB to threaten it.

### Why `batchSize: 1`

The queue extension defaults to `batchSize: 16` / `newBatchThreshold: 8`, i.e. 24 concurrent
executions per instance. For CPU-bound FFmpeg that means 24 transcodes fighting over one small VM.
`host.json` pins `batchSize: 1` and `newBatchThreshold: 0` so target-based scaling adds **one
instance per queued message** instead — which is the entire point of moving this here.

## Settings

**There is exactly one settings file, it is local-only, and it is gitignored.**

| Where | How settings arrive |
|---|---|
| Local | `local.settings.json` (gitignored). `local.settings.sample.json` is the tracked template. |
| Test / Production | The Function App's **Application Settings** in Azure. No files. This is the per-environment mechanism. |

There is deliberately no `appsettings.{Environment}.json` here, and adding one would not work: the
`QueueTrigger` attributes resolve their queue names (`%MediaProcessing:*QueueName%`) and their
storage connection (`StagingStorageConnectionString`) through the Functions **host**, before the
worker's `IConfiguration` exists. A per-environment JSON file loaded by the worker could not supply
them, so splitting settings across two mechanisms would leave half of them in the wrong place.

Don't hand-copy the connection strings — they already live in the web app's
`appsettings.{Environment}.json`. Generate this file from there:

```powershell
pwsh ./Sync-FunctionSettings.ps1 -Environment Test     # or Development
```

VS Code tasks: **Sync Function settings (Development)** and **(Test)**. There is intentionally no
Production task — `local.settings.json` is only read when running on your machine, so a Production
sync points your dev box at the real queues and the real callback URL. The script still accepts
`-Environment Production` and warns loudly.

For the Azure side, `-ShowAzureCli` prints the matching
`az functionapp config appsettings set` command:

```powershell
pwsh ./Sync-FunctionSettings.ps1 -Environment Test -ShowAzureCli
```

## Running locally

```powershell
pwsh ./Sync-FunctionSettings.ps1 -Environment Development
cd MusicSalesApp.Functions
func start
```

**The Development settings point at the real Azure accounts, not Azurite** — the same way the web
app's Development environment already uses `musiccontainer-local` on the live premium account. Two
consequences worth knowing before you hit F5:

- Running `func start` makes your machine **poll a real queue** (`audio-transcode-local`). If two
  developers run it at once they will steal each other's messages.
- Because the accounts really are different, the cross-account SAS copy *is* exercised locally,
  which is the one part of the pipeline Azurite could not have tested.

`CallbackBaseUrl` is your local site, so the site has to be running too or every callback fails and
the message retries to the poison queue.

## First-time Azure setup

**Almost nothing needs creating by hand.** `Provision-FunctionApp.ps1` creates the Function App,
Application Insights and the two queues. The poison queues and the staging container are still made
on demand — by the Functions runtime and by `SongUploadJobService` respectively, both of which call
`CreateIfNotExistsAsync`.

```powershell
az login --scope https://management.core.windows.net//.default

pwsh ./Provision-FunctionApp.ps1 -Environment Test -WhatIf   # dry run first
pwsh ./Provision-FunctionApp.ps1 -Environment Test
```

The script sets the subscription itself (`WebsitesSubscription` by default, override with
`-Subscription`) rather than inheriting whatever `az account set` was last run for some other
project. That selection is ambient global state, and it silently decides where production resources
get created.

It reads the connection strings out of `MusicSalesApp/appsettings.Test.json` — the standard account
from the existing **`AzureLowSpeed`** section, the premium one from `Azure` — derives the resource
group and region from the storage account, then creates the Function App (Windows Consumption,
Functions v4, .NET 10 isolated) plus Application Insights.

App settings are seeded **only on the run that creates the app** — see *Which script touches what*
below. On any later run the portal is authoritative and the script only reports drift.

Note the setting names differ on each side deliberately: the web app groups them by *storage
account* (`AzureLowSpeed`, `Azure`), while the Function App groups them by *role*
(`StagingStorageConnectionString`, `MediaStorageConnectionString`). The scripts are the mapping, so
neither side has to adopt the other's naming.

It also **refuses to continue** if the queue account is not Standard general-purpose v2 — that is
the constraint that forced two storage accounts, and failing here with a clear message beats
failing at runtime with an obscure binding error. It warns if the media account is in a different
region from the Function App, since the audit path downloads every playback blob.

The script is **idempotent** — safe to re-run against an environment that already exists. That
matters because Test and Production share one storage account, and a storage management policy is
account-wide: provisioning Production *merges* its lifecycle rule alongside Test's rather than
replacing it.

## Tearing an environment down

`Remove-FunctionApp.ps1` removes everything provisioning created, so the whole path can be
rehearsed from a clean slate before it is run against Production for the first time:

```powershell
pwsh ./Remove-FunctionApp.ps1 -Environment Test -WhatIf
pwsh ./Remove-FunctionApp.ps1 -Environment Test
```

It deletes the Function App, its Consumption plan (only once nothing else is on it), Application
Insights, the Smart Detection action group Azure creates alongside it, the content file share, the
`azure-webjobs-*` runtime containers, and **only this environment's** lifecycle rule.

It never deletes a storage account, any media/persona/dataprotection container, or the other
environment's lifecycle rule. The queues and the staging container are also left alone unless you
pass `-IncludeQueuesAndStaging`, since they can hold songs mid-processing.

One `-WhatIf` artifact worth knowing: it will report *"App Service plan still hosts 1 site(s);
leaving it"*, because under `-WhatIf` the Function App is not actually deleted, so the plan really
is still occupied at that moment. On a real run the app is gone by then and the empty plan is
removed.

## Deploying

```powershell
pwsh ./Invoke-FunctionPublish.ps1 -FunctionAppName streamtunes-media-test    # davidtest.dev
pwsh ./Invoke-FunctionPublish.ps1 -FunctionAppName streamtunes-media-prod    # streamtunes.net
```

### Which script touches what

| | Deploys code | Writes app settings |
|---|---|---|
| `Invoke-FunctionPublish.ps1` | yes | **no** |
| `Provision-FunctionApp.ps1` | no | **yes** |

Routine code updates go through `Invoke-FunctionPublish.ps1`, which runs `func azure functionapp
publish` with no `--publish-local-settings`. **Settings changed in the Azure portal survive any
number of code redeploys.**

`Provision-FunctionApp.ps1` writes settings **only on the run that creates the app** — a brand-new
Function App has no connection strings, so its triggers cannot bind without them. Once an
environment exists, **the Azure portal is authoritative** and the script does not touch settings.

That is deliberate. Keeping the same nine values in both `appsettings.{Environment}.json` and Azure
is how a portal edit ends up silently reverted by the next re-provision, which is a miserable thing
to debug. One place wins, and for a deployed environment it is the portal.

What the script still does on a re-run is a **read-only drift check**:

```
Leaving application settings alone; the portal is authoritative.
WARNING: Azure and appsettings.Test.json disagree on 1 setting(s):
    CallbackBaseUrl differs from appsettings.Test.json
```

It reports key names only, never values, and changes nothing. That matters because the web app reads
the file while the Function reads Azure, and the two must agree on the queue names, the staging
container and the API key — if they diverge, messages land in a queue nobody polls, or every callback
401s. Neither failure announces itself.

`-ApplySettings` forces the file back over Azure when you do want that.

Rotating `MediaProcessingApiKey` still has to happen on both sides, or callbacks 401 and jobs pile up
until the reconciler fails them ~20 minutes later. Order: change it in the portal for the Function,
change it in `appsettings.{Environment}.json` for the web app, then deploy the web app.

### Deploy the Function before the web app

For a first deployment to an environment, the Function goes first. The reverse leaves a window where
creators can upload: jobs stage and enqueue with nothing to process them, and after
`StalledJobTimeout` the reconciler marks them Failed and tells the creator their upload broke when
nothing was wrong. A Function deployed early just polls an empty queue and costs nothing.

Two apps rather than one, so a bad deploy cannot take down production processing and test at the
same moment. There is no CI/CD in this repo; `.vscode/tasks.json` has both as tasks.

The script wraps `func azure functionapp publish` for one reason: **Core Tools installs onto the
machine PATH, but an already-running VS Code keeps the environment it started with.** For the rest
of that session `func` is on disk and on the machine PATH yet invisible to every terminal VS Code
spawns — and *Reload Window does not fix it*, only fully quitting and reopening the application
does. A plain `Get-Command func` check reports "not installed" in that window, which sends you off
to reinstall a tool you already have. The script resolves `func.exe` directly, publishes anyway, and
tells you to restart VS Code; it only claims Core Tools is missing when no copy exists anywhere.

The web app must be deployed too — the `AddSongUploadJobs` migration creates the table the whole
pipeline reports into, and `api/media-processing/*` is where the Function posts its results. A
Function deployed against a site that predates those will get 404s on every callback and retry until
its messages poison.
