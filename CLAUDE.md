# Claude Instructions

## Working Branches

- **Before editing any file**, check the current branch with `git rev-parse --abbrev-ref HEAD`.
- If it is `master`, create and switch to a working branch **first** — do not start editing and branch later.
- Use task-based names such as `work/azure-storage-backup-restore`.
- Never make code edits on `master` unless the user explicitly asks for that.

## Git Commits

- Do not assume the user wants changes committed.
- Only run `git commit` when the user explicitly asks for a commit.
- If work is ready but the user has not asked for a commit, leave the changes uncommitted and report the status.

## Tests

- Anytime new functionality is added or existing functionality is modified, add or update tests that help prevent regressions.
- If a change is difficult to test directly, add the closest practical focused regression test and clearly report any remaining manual test coverage.

## What this is

Blazor Server web app for StreamTunes — a music platform with two audiences: **creators** (upload/sell via streaming, receive tips/payouts) and **listeners** (browse/stream via subscription). Deployed to two sites:

- **Production**: https://streamtunes.net
- **Test**: https://davidtest.dev

The web app is hosted on **smarterasp.net** (IIS-based Windows host), *not* Azure App Service — Azure is used for Blob Storage, Data Protection key persistence, and (since audio processing moved off the web server) two **Azure Functions** apps and the queues that feed them. Deployment is manual for both; there is no CI/CD pipeline in this repo (no `.github/workflows`). `.vscode/tasks.json` has the four publish tasks — two MSDeploy profiles for the site, two `func azure functionapp publish` for the Functions.

This app is also the **backend API for the sibling MAUI mobile app** (`../MusicSalesApp_Maui`, listeners only — see "Sibling repo" below).

## Solution structure

| Project | Purpose |
|---|---|
| `MusicSalesApp/` | The Blazor Server UI, Razor Pages, all API controllers, EF Core DbContext, services, SignalR hubs, Hangfire jobs. Deployed to smarterasp.net. |
| `MusicSalesApp.Functions/` | Azure Functions (isolated, Windows Consumption) — **all FFmpeg work, plus the image work that used to run on the Blazor circuit**. Four queue triggers: transcode a creator upload (and build its cover-art renditions), decode-probe a stored blob for the maintenance jobs, pair a batch of cover art with the audio dropped beside it, and report an upload whose message reached the poison queue. Owns `ffmpeg.exe`. See its README. |
| `MusicSalesApp.Imaging/` | The SkiaSharp rendition rules (`ImageVariantEncoder`), shared by the web app and the Function so a cover art's ladder has the same shape whichever built it. Deliberately **not** in `Common`, which the MAUI repo references and which must stay free of SkiaSharp. |
| `MusicSalesApp.Common/` | Shared constants/helpers (`Helpers/*.cs` — `Roles`, `Permissions`, `SubscriptionStatuses`, `BillingSources`, `AuthStorageKeys`, etc.). Also referenced directly by the sibling MAUI repo. |
| `MusicSalesApp.Tests/` | NUnit + Moq unit tests (Controllers, Services, Helpers, Middleware, Authorization). Uses EF Core InMemory and a hand-rolled `TestFactory : IDbContextFactory<AppDbContext>` re-declared per file. |
| `MusicSalesApp.ComponentTests/` | bUnit Blazor component tests. |
| `MusicSalesApp.LyricsFunctions/` | **Python** Azure Functions (Linux, Flex Consumption) — Durable orchestration that aligns pasted lyrics to a song's audio: Demucs vocal separation, forced alignment, then token-to-line mapping. Outside the solution: a Function app is pinned to one language runtime, and this one is PyTorch. Invoked over HTTP rather than a queue so its orchestration instance id comes back. Tests run under `pytest`, not `dotnet test`. |

There's no separate Data/Identity/Infrastructure project — everything lives inside `MusicSalesApp/` by folder: `Data/AppDbContext.cs` (single EF Core context), `Models/`, `Services/` (~90 services, interface + impl pairs), `Controllers/` (18 API controllers), `Hubs/` (5 SignalR hubs), `Components/` (`Base/BlazorBase.cs` is the base class every page/component code-behind inherits from — see it for the full set of services injectable without `@inject`).

## Tech stack

- **.NET 10**, **Blazor Server** (Interactive Server render mode — not WASM, not Auto) alongside classic Razor Pages.
- **EF Core / SQL Server** — single `AppDbContext`; both a scoped `DbContext` and `IDbContextFactory<AppDbContext>` are registered (the factory exists specifically to avoid Blazor Server's concurrent-DbContext-per-circuit problems). Migrations auto-apply at startup.
- **Auth**: ASP.NET Core Identity (`ApplicationUser : IdentityUser<int>`) with **cookie auth for the web UI and JWT Bearer for the mobile app simultaneously** (combined default policy). Claims-based `Permissions` policies built dynamically via reflection. Google external login (web + mobile "exchange token" flow) and **Sign in with Apple** (mobile only, native — see `MobileAuthController` below). WebAuthn/FIDO2 passkeys via Fido2NetLib.
- **Background jobs**: Hangfire (SQL Server storage, `/hangfire` dashboard gated by `HangfireAuthorizationFilter`).
- **Real-time**: SignalR — `StreamCountHub`, `LikeCountHub`, `WebhookStatusHub`, `MaintenanceHub`, `AdminMessageHub`, `UploadProgressHub` (per-creator groups, carries live upload progress).
- **UI**: Syncfusion Blazor components exclusively (see `AGENTS.md` for CTA/theme conventions).
- **Storage**: **Two Azure storage accounts, and they are not interchangeable.** `Azure` is the *Premium* `highspeedstorageaccount` holding all song media and Data Protection keys. `AzureLowSpeed` is the *Standard general-purpose* `musicsalesstorageaccount` — once a dead config section, now the home of the audio-processing queues and the `musicuploads{-env}` staging container, because premium accounts offer no Queue service at all. One section per storage account, so each connection string exists once. A **third container**, `musicstreaming{-env}`, sits on the premium account
alongside the media one and holds the AES-128 encrypted HLS segments, which are worthless without a
key the API gates. It is **private** like every other container - segments reach listeners under a
container read SAS - because both storage accounts set `allowBlobPublicAccess: false`. Media never moves between them, so staging→media copies are **cross-account** and need a source SAS. `MediaProcessingOptions` is bound by hand from `AzureLowSpeed` plus the top-level `MediaProcessingApiKey` (see `Program.cs`) — the property names differ from the JSON because the section describes an account while the options describe what processing does with it.
- **Media processing**: almost none in this app. FFmpeg lives entirely in `MusicSalesApp.Functions`, which also builds a song's cover-art renditions and pairs dropped cover art with its audio; here there is only header sniffing (`AudioContainerSniffer` in `Common`) plus SkiaSharp re-encoding on the admin/creator art-replace paths and the rendition backfill (`ImageVariantCoordinator`, still inline). An upload is staged + queued by `SongUploadJobService`, then assembled by `MediaProcessingCompletionService` when the Function calls back to `api/media-processing/*`. `SongMetadata` is written **only on success**, so no catalogue query has to filter out half-built songs — in-flight state lives in `SongUploadJob`.
- **Payments/Billing**: PayPal (Expanded Checkout, Subscriptions API, webhooks), Google Play Billing (server-side purchase/subscription verification + real-time developer notifications), Apple App Store (StoreKit server API + App Store Server Notifications v2).
- **Tax**: TaxBandits integration for creator W-9/W-8BEN forms and 1099 reporting.
- **Logging**: Serilog (console + rolling file sink under `logs/`).
- **AI**: `OpenAI` NuGet package is still present/configured (despite a commit titled "Remove Supabase/OpenAI") — used for cover-art OCR and pairing, not recommendations. That now runs in `MusicSalesApp.Functions`; what remains here is `FileMatchingService`, the deterministic exact-base-name fallback used whenever the Function cannot answer.


## Audio delivery is encrypted HLS, and the plaintext routes are closed

Audio is served as **AES-128 encrypted HLS**, never as an MP3 URL. This replaced a delivery model
with three separate holes in it, all of which are now shut — read this before adding anything that
hands out an audio URL.

**What it replaced.** `GET api/music/songs` had *no authentication of any kind* and returned a
24-hour read SAS for every song in the catalogue, with a 5-minute `[ResponseCache]` on a response
carrying per-caller credentials. `GET api/music/{*path}` was an anonymous byte proxy whose whitelist
admitted `Mp3BlobPath`, so `curl .../api/music/{guid}/{guid}-music.mp3` returned the whole file.
`GET api/music/song-by-title/{title}` turned a public song title into a SAS for the same file. And
the 60-second free preview was enforced only in JavaScript — a non-subscriber was sent the entire
song and asked to stop.

**How it works now.** Three pieces, and only the middle one is a secret:

| Piece | Where | Gate |
|---|---|---|
| Manifest | `GET api/stream/{songId}/index.m3u8` | A manifest token (24 h) **or** an authenticated session. Generated per listener: a non-subscriber's copy lists only the preview segments, so the rest of the song is not merely unplayed, it is never named. |
| Content key | `GET api/stream/{songId}/key` | A **key token, ~60 seconds**, minted fresh into each manifest. Plus an `Origin`/`Referer` check. This is the whole security boundary. |
| Segments | `musicstreaming{-env}` blob container, **private** | A container read SAS, stamped onto each URL by the manifest builder. They are ciphertext, so the SAS being visible in dev tools leaks nothing playable. Originally this was to be a public container; both storage accounts set `allowBlobPublicAccess: false`, and that guardrail was worth keeping - the premium account holds every song master and the Data Protection key rings, for Production as well as Test. |


**Segments need CORS on the storage account, and this is the trap.** A native `<audio src>` load is
not CORS-checked, but hls.js fetches every segment by **XHR**, which is. Without a GET rule on the
*premium* account, storage serves the segment perfectly and the browser then refuses to let the page
read it — the console shows `ERR_FAILED 200 (OK)` and playback simply never starts, which reads as a
player bug rather than a storage-configuration one. `Provision-FunctionApp.ps1` now sets it
(`Set-BlobCorsRule`, GET/HEAD/OPTIONS), alongside the pre-existing PUT rule the staging account needs
for direct browser uploads. Note CORS is **account-and-service scoped**, so Test and Production share
these rules on both accounts — which is why each environment appends its own origins rather than
clearing.

The two token kinds are never interchangeable (`HlsTokenKind`), and that check is what allows their
lifetimes to differ by three orders of magnitude. Collapse them and the 24-hour manifest lifetime
becomes the key's lifetime.

**Tokens travel in the query string, not a header**, and that is a requirement rather than a
shortcut: ExoPlayer's HLS key loader and AVPlayer both fetch the `#EXT-X-KEY` URI through their own
HTTP stack with no way to attach a header. It is what will let the mobile apps play encrypted audio
without custom native plumbing.

**The content key is not protected with ASP.NET Data Protection**, and must not be.
`StorageBackupService.GetConfiguredContainerNames` excludes the Data Protection key ring from backup
on purpose, because everything it protects is transient and regenerating it merely signs everyone
out. A content key is neither transient nor reproducible. Keys are wrapped with AES-256-GCM under
`Hls:ContentKeyWrappingKey` (bound to the song id as associated data) — **losing that config value
means re-encoding the entire catalogue**. Rotating it is a database re-wrap, not a re-encode: the
stored value carries a `v{n}` prefix. The short-lived stream tokens *do* use Data Protection, which
is exactly the disposability that ring is designed for.

**Two audio SAS call sites survive, both deliberate.** `MobileSongMapper` still emits `StreamUrl` for
the MAUI app, which has not moved to HLS yet (see below), and `LyricsTimingEditor` mints one for the
creator's own song on a page gated to that creator. Everything else goes through
`IHlsStreamUrlFactory`.


**A truncated preview manifest makes the media element's duration wrong, and that has to be handled
per player.** A free-preview listener is served 60 seconds of segments, so the element honestly
reports a one-minute song — which would mislabel every track and peg the preview marker at the far
right of the progress bar, saying the opposite of what the marker means. Every web player therefore
takes its displayed duration from `SongMetadata.TrackLength` and passes it to JS
(`effectiveDuration` in `wwwroot/js/hls-player.js`), which uses it for the reported duration and for
seek maths alike — the bar and the audio must agree, or a click 25% along a four-minute bar seeks to
15s instead of 60s.

**This is a landmine for the mobile phase.** MAUI is correct today only because it plays the whole
MP3, so nominal and media duration coincide. Moving it to encrypted HLS makes them differ, and
`PlaybackService._playbackDuration` is not merely cosmetic: `GetSeekPosition` scales against it, and
the `MediaItemFinished` guard discards a finish event when `position + tolerance < duration`. Set it
to the nominal length while the media holds only the preview and a preview ending at 0:60 is judged
"not near the track end", so the event is dropped and playback stalls instead of advancing. Mobile
HLS therefore needs a *display* duration separate from the *media* duration, not a substitution.

**Mobile is still on MP3.** `api/music/songs` is now behind `[RequireMobileApiKey]`, but that key
ships inside a distributed app binary — so the catalogue remains obtainable by someone who unpacks
the APK/IPA. What closed is the trivial browser/`curl` path. The DTOs already carry `HlsUrl` and
`AudioVersion` for the mobile phase.

**Packaging and the backfill.** `MusicSalesApp.Functions`' `PackageAudio` produces the packages;
`MediaProcessingCompletionService` queues one per new upload (best-effort — a song with no package is
served the way songs were before this existed). The one-time pass over the existing catalogue is
`IHlsPackagingBackfillService`, at `/admin/hls-packaging-backfill`, run **once per environment**. Its
`RepairMissing` scope is the disaster-recovery path: after a blob restore that did not bring the
streaming container back, every row still carries an `HlsStreamId` pointing at a folder that is gone,
and the database looks perfectly healthy while nothing plays. The manifest endpoint answers **503,
not 404**, for exactly that state.

**`AudioContentVersion` starts at 0, not 1.** It is the audio counterpart of
`CoverArtVariantVersion`, but the MAUI client folds a version into its cache key only when it is
greater than zero (`StableRemoteAssetKey.GetPathHash`). Shipping it at 1 would change every audio
cache key at once and silently re-download every user's offline library.

## Domain model summary

- **Identity/creators**: `ApplicationUser` (Identity, int key), `Creator` (1:1 with a user — onboarding status, PayPal payout email, `StreamPayRate`/`StreamQualifyingSeconds` locked in at onboarding, tax residency fields, `IsFullyOnboarded` computed from onboarding+tax+PayPal-affirmation state), `CreatorPersona` (artist alias), `Passkey`, `MobileVerificationCode`.
- **Catalog**: `SongMetadata` is the single source of truth for track/album/cover metadata — classifies rows as album-cover / album-track / standalone-song via presence of `Mp3BlobPath`/`AlbumName`. `GetEffectiveArtistName()` priority chain: Persona > ArtistName > Creator.DisplayName > Creator.User.Email (email-domain stripped for public display).
  - **Song Profile**: A song has a *complete profile* when it has all four of: Cover Art (`ImageBlobPath` not null/empty), Genre (not null/empty), Persona Name (linked `CreatorPersona` with `IsEnabled=true` and non-null/empty `Name`), and Persona Image (linked persona with non-null/empty `ImageBlobPath`). Only songs with complete profiles are eligible for automatic home-page featuring via the `rotate-featured-songs` Hangfire job. The profile-completeness check lives in `SongMetadataQueryExtensions.WhereHasCompleteProfile()` — use this filter when selecting songs for any feature that requires a polished presence (e.g., curated playlists, editors' picks).
- **Playlists**: `Playlist` (`IsSystemGenerated` flags the auto "Liked Songs" playlist), `UserPlaylist` (references `SongMetadataId` directly), `RecommendedPlaylist`.
- **Billing**: `Subscription` — multi-provider via `BillingSource` (PayPal / GooglePlay / Apple), each with its own ID fields (`PayPalSubscriptionId`, `GooglePlayPurchaseToken`, `AppStoreTransactionId`, etc.), plus trial fields and store-reported price fields (`StoreFormattedPrice`/`StorePriceCurrencyCode`).
- **Payouts**: `StreamPayout` (per-creator stream-royalty batches), `Tip` (listener→creator, 7-day hold before payout, fraud-limited via `BlockedTipAttempt`, `ChargebackLog`).
- **Admin/ops**: `AppSettings` (single-row config, e.g. active PayPal plan selection), `AdminMessage`/`AdminMessageRecipient`, `MediaIntegrityAuditRun`/`Item`/`Notification` (background blob/DB integrity audits).

**Important — subscription-only model (no more purchases):** `CartItem` and `OwnedSong` tables, and `SongMetadata.AlbumPrice`/`SongPrice` columns, were **dropped** by migration `Migrations/20260108000000_RemoveCartAndOwnedSongsTables.cs`. There is no more permanent per-song/album ownership — access is gated entirely on active subscription status. `Controllers/CartController.cs` is an intentional dead shim now: every mutating endpoint returns 400 with "Individual song purchases have been removed." **Do not extend it.** A later migration (`20260713020755_RemoveSubscriptionPriceSetting`) also removed the admin-configurable global subscription price setting — the store (PayPal plan price, or the Google Play/Apple StoreKit-reported price) is now the source of truth for what's charged/shown, not an app-level setting.

## Creator vs listener split

- **Roles** (`MusicSalesApp.Common.Helpers.Roles`): `Admin`, `User`, `NonValidatedUser` (pre-email-verification), `Creator`.
- **Permissions** (claims-based): `ManageUsers`, `ValidatedUser`, `NonValidatedUser`, `UploadFiles`, `UseHangfire`, `ManageSongs`, `ManageOwnSongs`, `ManageAllCreatorSongs`.
- The `Creator` role and the `Creator` onboarding record are **separate concepts** — a user can hold the role while still mid-onboarding and not yet payout-eligible (`Creator.IsFullyOnboarded` additionally requires active status, completed onboarding, completed tax form, and PayPal account affirmation).
- Money flows to creators via per-stream payout (rate locked at onboarding, default $0.005/stream after a qualifying playback duration) and direct listener tips, reconciled through `StreamPayoutService` and reported to the IRS via TaxBandits.
- **Listener gating is subscription-status-based, not role-based** — any authenticated user is a "listener" for consumption purposes; non-subscribers get a 60-second preview.

## Mobile API surface

Controllers under `Controllers/` gated by `[RequireMobileApiKey]` (header `X-Api-Key` matched against config) plus JWT Bearer `[Authorize]`:

- `MobileAuthController` (`api/mobile-auth`) — register/login/verify (6-digit codes)/reset password/change email/delete account, Google OAuth (`google/start`, `exchange`, `register`) via `streamtunes://auth` deep link, and Sign in with Apple (`apple/token`, `apple/register`).

  The Apple pair is shaped differently from Google on purpose, and the difference is the whole point: Google is **server-brokered** (we run the OAuth dance and redirect into the app), whereas Apple is **native on the device** — the app drives the sheet and posts us a finished identity token, so there is no `start`/`callback`/`exchange`, no `AddApple` handler, and no `[SkipMobileApiKey]`. `AppleIdentityTokenValidator` just verifies the JWT against Apple's JWKS. Both providers converge on `CompleteExternalRegistrationAsync` and `BuildLoginResponseAsync`.

  > **Apple sends the email and name on the FIRST authorization only.** Every later sign-in carries just the stable `sub`, so the lookup keys on `sub` (the `AspNetUserLogins` provider key) and the email is persisted at first contact. `TryGetGoogleEmail` can re-read the email every time; there is deliberately no Apple equivalent.

  `ApplicationUser.AppleRefreshToken` exists solely so `AccountDeletionService` can revoke the user's Apple grant on deletion, which Apple requires. It is obtained by exchanging the sheet's authorization code — immediately, because that code is single-use and expires in five minutes, which is why a new user's token is parked in `MobilePendingExternalRegistrationTokenPayload.ExternalRefreshToken` until there is a row to attach it to. All of this is inert until `Authentication:Apple:TeamId`/`KeyId`/`PrivateKey*` are set (a **Sign in with Apple** key, a different key from the App Store Connect one beside it in `App_Data/Secrets`); sign-in works without them, only revocation is skipped.
- `MobilePlaylistController` (`api/mobile/playlists`) — home playlists, custom playlist CRUD, subscription-gated song access.
- `MobileAdminMessageController`, `MobileContactController`, `MobileSettingsController`, `MobileTipController` — supporting mobile features.
- `GooglePlaySubscriptionController` (`api/subscription/google-play`) + `GooglePlayWebhookController` — Google Play purchase verification + real-time developer notifications.
- `AppleAppStoreSubscriptionController` + `AppleAppStoreNotificationsController` (`api/subscription/app-store`) — StoreKit transaction verification + App Store Server Notifications v2.
- `SubscriptionController` (`api/subscription`) — shared web+mobile subscription status/management.
- `PasskeyController` (`api/passkey`) — WebAuthn, used by both web and mobile.

The MAUI app also calls `MusicController` (`api/music`) directly. It is gated **per action** rather
than at the class level, so check the action rather than assuming. Relevant ones: `songs`,
`likes/bulk`, `likes/user-status`, `like/{id}`, `dislike/{id}`, `like-state/{id}`, `report/{id}`,
`stream/{id}`.

`songs` and `song-by-title/{title}` carry `[RequireMobileApiKey]` **plus** `[Authorize(schemes)]`
with `[AllowAnonymous]` - the schemes are listed so the bearer token is honoured and
`HttpContext.User` is populated for the entitlement check, and anonymous is still allowed so in-app
browsing before sign-in works. Both were completely unauthenticated until encrypted HLS landed, and
both handed out playable links to the plaintext MP3s; see the audio-delivery section above before
loosening either.

- `PUT api/music/like-state/{id}` (body `{ "status": true | false | null }`) is the **idempotent** counterpart to the `like`/`dislike` toggles, added for the mobile offline queue: the outcome depends only on the requested state, so a queued intent can be replayed safely. The toggle endpoints remain for the Blazor `LikeDislikeButtons` component, which depends on their flip semantics — keep the flip decision in the toggle; do not express one endpoint's semantics in terms of another's.
  - All three share one private writer, `SongLikeService.ApplyLikeStateAsync`, which takes an already-decided terminal state. It recovers from a concurrent writer losing the unique `(UserId, SongMetadataId)` race, and raises `SongNotFoundException` for a song that has since been deleted.
  - That exception maps to **400, not 404 and never a 500** — deliberately, on all three routes. The MAUI client reads a 404 on `like-state` as "this server predates the endpoint" and falls back to the toggles; it retries any 5xx forever, and its flush stops at the first failure, so a permanent error dressed as a transient one strands every intent queued behind it. 400 is in the client's drop set.

**Known wart**: route naming is inconsistent (`api/mobile/...` vs `api/mobile-auth` vs `api/mobile-settings`) with no versioning scheme anywhere.

## Environment configuration

- `appsettings.json` (tracked) has safe placeholders only.
- `appsettings.{Development,Test,Production}.json` are **gitignored** — real secrets live only on disk locally/on-server. A `.sample` template exists for local dev.
- **Test** (davidtest.dev): separate SQL DB, separate Azure Blob containers (`-dev` suffix, same storage account), PayPal **sandbox**, Apple **storekit-sandbox**, separate `MobileApiKey`, `Logging:Default = Debug`.
- **Production** (streamtunes.net): live PayPal, live StoreKit, `Logging:Default = Information`.
- Fido2 `ServerDomain`/`Origins` are set per-environment (required for passkeys, which are origin-bound).
- `Hls:ContentKeyWrappingKey` is per-environment and **irreplaceable** - see the audio-delivery
  section above. Generate with `openssl rand -base64 32`. It is the one secret in this app whose
  loss is not merely inconvenient: without it every packaged song is undecryptable and the entire
  catalogue has to be re-encoded. Back it up wherever the other environment secrets are kept.
- Three new per-environment values arrive with encrypted HLS: `Azure:StreamingContainerName`,
  `AzureLowSpeed:PackageQueueName`, and the `Hls` section. The Function App needs the first two
  under its own names (`MediaProcessing:StreamingContainerName`, `MediaProcessing:PackageQueueName`)
  - added **in the portal**, since the portal is authoritative for a deployed Function App - but
  **not** the wrapping key, which only the web app uses.

## Migrations run themselves

`Program.cs` calls `db.Database.Migrate()` at startup, behind a `CanConnect()` check, and
**rethrows on failure so the app will not start half-migrated**. A deployment therefore applies
whatever is pending on its own — there is no `dotnet ef database update` step to remember, and
no need to tell anyone to run one.

Two consequences worth knowing before writing one:

- **A bad migration takes the site down**, rather than leaving it up on a half-changed schema.
  That is the right trade, but it means a migration is a deployment-blocking change: prefer
  additive ones (new nullable columns, new tables) so an old instance overlapping a new one
  during a deploy keeps working.
- **Web Deploy may ALSO publish the database**, and that path is not the same. `Migrate()` sends
  each operation as its own command; the Web Deploy script puts the whole migration in one batch
  inside one transaction. SQL Server compiles a batch before running any of it, so a `Sql()`
  backfill naming a column the `AddColumn` above it creates fails to parse with
  `Invalid column name` — even though the same migration applies cleanly at startup.

### Writing one

- Let `dotnet ef migrations add` generate it, and ship what it generates. A schema change is a
  diff of the model, and hand-writing that is how it drifts from the snapshot.
- **Anything you add by hand is where the risk is**, and a data backfill always is one — EF
  cannot infer it, because "what existing rows should say" is not in the model.
- **Wrap a hand-added `Sql()` that touches new columns in `EXEC(N'...')`.** Dynamic SQL is
  compiled when it runs, by which point the columns exist. `AddUserPlaylistSortOrder` and
  `AddCreatorStatusAnnouncementFlags` both do this; the second one learned it the hard way, by
  failing a publish.
- **Then read the script**: `dotnet ef migrations script <from> <to> --idempotent`. That is the
  artifact Web Deploy runs, and it is the only place the batching problem is visible.
- **Think about what null means to the code you just wrote.** A new nullable column is null for
  every existing row, so if your code reads null as "not done yet", every existing row is about
  to look undone. `AddCreatorStatusAnnouncementFlags` backfills for exactly this reason: without
  it, every creator who had already been welcomed would have been welcomed again — analytics
  event, audit row and all — the next time they opened the page.

## Key files

| File | Why it matters |
|---|---|
| `MusicSalesApp/Program.cs` | Composition root — dual cookie+JWT auth, ~90 DI registrations, Hangfire/SignalR/Fido2/DataProtection setup, middleware order, migrate-on-startup. |
| `MusicSalesApp/Data/AppDbContext.cs` | The single EF Core context — full entity graph and cascade-delete configuration. |
| `MusicSalesApp/Components/Base/BlazorBase.cs` | Base class for every page/component code-behind; defines the injected-service surface. |
| `MusicSalesApp.Common/Helpers/*.cs` | ~45 constants classes — check here before introducing a new status/event/key string (see `AGENTS.md`). |
| `MusicSalesApp/Models/Subscription.cs`, `Services/SubscriptionService.cs`, `Services/PayPalSubscriptionManagementService.cs`, `Services/PayPalSubscriptionAnomalyService.cs` | Multi-provider billing/entitlement core — also the most actively-changing area. |
| `MusicSalesApp/Controllers/MobileAuthController.cs`, `MobilePlaylistController.cs` | Best reference examples for the mobile API pattern (API key + JWT + DTOs + subscription gating). |
| `MusicSalesApp/Controllers/StreamController.cs`, `Services/HlsManifestBuilder.cs`, `Services/HlsContentKeyProtector.cs` | The whole audio security boundary. The manifest is generated per listener and the key is gated by a ~60-second token; everything else about encrypted delivery follows from these three. |
| `MusicSalesApp/Controllers/CartController.cs` | Intentionally dead shim — read before touching, don't extend. |
| `MusicSalesApp.Functions/CLAUDE.md` | **Read first for anything audio-processing.** End-to-end upload flow across the three processes, the throw-vs-report invariant, progress monotonicity, and the traps (read-only package mount, `batchSize: 1`, 10-min ceiling). |
| `MusicSalesApp.Functions/README.md` | The operational half: settings, provisioning, deploying, running locally, tearing an environment down. |
| `MusicSalesApp.Common/Contracts/AudioProcessingProgressCalculator.cs` | The single definition of the upload progress bar's bands, shared by the upload page, the Function and the API. |
| `MusicSalesApp/Services/MediaProcessingCompletionService.cs` | Assembles a finished transcode into a live song — the cross-account copy, the metadata write, and the idempotency that makes a queue retry safe. |
| `Migrations/20260108000000_RemoveCartAndOwnedSongsTables.cs`, `Migrations/20260713020755_RemoveSubscriptionPriceSetting.cs` | Read these before trusting any doc/comment describing "ownership" or "pricing settings" — this area changed significantly and older references lag. |

## Where to look next

- **`AGENTS.md`** — the living engineering handbook: magic-string/constants convention, Syncfusion CTA/theme CSS rules, the code-behind pattern (`[Component]Model : BlazorBase`, no `@inject` in components), Blazor Server DbContext-threading guidance (why `OnAfterRenderAsync(firstRender)` not `OnInitializedAsync`), the "call services directly, don't round-trip through HTTP APIs from Blazor Server" rule, passkey implementation notes, email template conventions.
- **`HANDOFF.md`** — current PayPal subscription-status reconciliation edge cases (`ACTIVE`/`SUSPENDED`/`CANCELLED`/`EXPIRED` semantics) and an open, not-yet-implemented "Refresh Subscription" mismatch-resolution plan.
- **`MusicSalesApp.Functions/CLAUDE.md` § "Encrypted HLS packaging"** — the producing half of audio
  delivery: why packaging is its own queue, why the output directory holds a plaintext key, and why
  its poison handler is load-bearing.
- **`PAYPAL_EXPANDED_CHECKOUT.md`** — PayPal Expanded Checkout / 3D Secure integration details, now relevant to the subscription checkout flow (not song purchases).
- **`LIKED_SONGS_IMPLEMENTATION.md`** — the system-generated "Liked Songs" auto-playlist.
- **`SIGNALR_RECONNECTION_TESTING.md`**, **`FACEBOOK_SHARING_TESTING.md`** — manual test guides for real-time reconnection and OpenGraph sharing.

## Sibling repo

The MAUI listener app lives at `../MusicSalesApp_Maui` (dual-root VS Code workspace). It:

- References `MusicSalesApp.Common` directly via project reference — changes to shared constants/helpers affect both repos at once.
- Consumes the mobile API controllers listed above; auth/subscription semantics must stay in sync with `MobileAuthController`/`SubscriptionController`.
- Talks to whichever of `streamtunes.net`/`davidtest.dev` its own build configuration resolves to (see that repo's `CLAUDE.md` for details).
