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

Hosted on **smarterasp.net** (IIS-based Windows host), *not* Azure App Service — Azure is used only for Blob Storage and Data Protection key persistence. Deployment is manual; there is no CI/CD pipeline in this repo (no `.github/workflows`).

This app is also the **backend API for the sibling MAUI mobile app** (`../MusicSalesApp_Maui`, listeners only — see "Sibling repo" below).

## Solution structure

| Project | Purpose |
|---|---|
| `MusicSalesApp/` | The only deployable project — Blazor Server UI, Razor Pages, all API controllers, EF Core DbContext, services, SignalR hubs, Hangfire jobs. |
| `MusicSalesApp.Common/` | Shared constants/helpers (`Helpers/*.cs` — `Roles`, `Permissions`, `SubscriptionStatuses`, `BillingSources`, `AuthStorageKeys`, etc.). Also referenced directly by the sibling MAUI repo. |
| `MusicSalesApp.Tests/` | xUnit unit tests (Controllers, Services, Helpers, Middleware, Authorization). |
| `MusicSalesApp.ComponentTests/` | bUnit Blazor component tests. |

There's no separate Data/Identity/Infrastructure project — everything lives inside `MusicSalesApp/` by folder: `Data/AppDbContext.cs` (single EF Core context), `Models/`, `Services/` (~90 services, interface + impl pairs), `Controllers/` (18 API controllers), `Hubs/` (5 SignalR hubs), `Components/` (`Base/BlazorBase.cs` is the base class every page/component code-behind inherits from — see it for the full set of services injectable without `@inject`).

## Tech stack

- **.NET 10**, **Blazor Server** (Interactive Server render mode — not WASM, not Auto) alongside classic Razor Pages.
- **EF Core / SQL Server** — single `AppDbContext`; both a scoped `DbContext` and `IDbContextFactory<AppDbContext>` are registered (the factory exists specifically to avoid Blazor Server's concurrent-DbContext-per-circuit problems). Migrations auto-apply at startup.
- **Auth**: ASP.NET Core Identity (`ApplicationUser : IdentityUser<int>`) with **cookie auth for the web UI and JWT Bearer for the mobile app simultaneously** (combined default policy). Claims-based `Permissions` policies built dynamically via reflection. Google external login (web + mobile "exchange token" flow). WebAuthn/FIDO2 passkeys via Fido2NetLib.
- **Background jobs**: Hangfire (SQL Server storage, `/hangfire` dashboard gated by `HangfireAuthorizationFilter`).
- **Real-time**: SignalR — `StreamCountHub`, `LikeCountHub`, `WebhookStatusHub`, `MaintenanceHub`, `AdminMessageHub`.
- **UI**: Syncfusion Blazor components exclusively (see `AGENTS.md` for CTA/theme conventions).
- **Storage**: Azure Blob Storage for media files and ASP.NET Data Protection keys (two configs: `Azure` "high speed" + `AzureLowSpeed` fallback).
- **Media processing**: FFMpegCore + a bundled `ffmpeg.exe` (used to extract track length on upload).
- **Payments/Billing**: PayPal (Expanded Checkout, Subscriptions API, webhooks), Google Play Billing (server-side purchase/subscription verification + real-time developer notifications), Apple App Store (StoreKit server API + App Store Server Notifications v2).
- **Tax**: TaxBandits integration for creator W-9/W-8BEN forms and 1099 reporting.
- **Logging**: Serilog (console + rolling file sink under `logs/`).
- **AI**: `OpenAI` NuGet package is still present/configured (despite a commit titled "Remove Supabase/OpenAI") — used for other purposes now (e.g. cover-art OCR), not recommendations.

## Domain model summary

- **Identity/creators**: `ApplicationUser` (Identity, int key), `Creator` (1:1 with a user — onboarding status, PayPal payout email, `StreamPayRate`/`StreamQualifyingSeconds` locked in at onboarding, tax residency fields, `IsFullyOnboarded` computed from onboarding+tax+PayPal-affirmation state), `CreatorPersona` (artist alias), `Passkey`, `MobileVerificationCode`.
- **Catalog**: `SongMetadata` is the single source of truth for track/album/cover metadata — classifies rows as album-cover / album-track / standalone-song via presence of `Mp3BlobPath`/`AlbumName`. `GetEffectiveArtistName()` priority chain: Persona > ArtistName > Creator.DisplayName > Creator.User.Email (email-domain stripped for public display).
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

- `MobileAuthController` (`api/mobile-auth`) — register/login/verify (6-digit codes)/reset password/change email/delete account, Google OAuth (`google/start`, `exchange`, `register`) via `streamtunes://auth` deep link.
- `MobilePlaylistController` (`api/mobile/playlists`) — home playlists, custom playlist CRUD, subscription-gated song access.
- `MobileAdminMessageController`, `MobileContactController`, `MobileSettingsController`, `MobileTipController` — supporting mobile features.
- `GooglePlaySubscriptionController` (`api/subscription/google-play`) + `GooglePlayWebhookController` — Google Play purchase verification + real-time developer notifications.
- `AppleAppStoreSubscriptionController` + `AppleAppStoreNotificationsController` (`api/subscription/app-store`) — StoreKit transaction verification + App Store Server Notifications v2.
- `SubscriptionController` (`api/subscription`) — shared web+mobile subscription status/management.
- `PasskeyController` (`api/passkey`) — WebAuthn, used by both web and mobile.

**Known wart**: route naming is inconsistent (`api/mobile/...` vs `api/mobile-auth` vs `api/mobile-settings`) with no versioning scheme anywhere.

## Environment configuration

- `appsettings.json` (tracked) has safe placeholders only.
- `appsettings.{Development,Test,Production}.json` are **gitignored** — real secrets live only on disk locally/on-server. A `.sample` template exists for local dev.
- **Test** (davidtest.dev): separate SQL DB, separate Azure Blob containers (`-dev` suffix, same storage account), PayPal **sandbox**, Apple **storekit-sandbox**, separate `MobileApiKey`, `Logging:Default = Debug`.
- **Production** (streamtunes.net): live PayPal, live StoreKit, `Logging:Default = Information`.
- Fido2 `ServerDomain`/`Origins` are set per-environment (required for passkeys, which are origin-bound).

## Key files

| File | Why it matters |
|---|---|
| `MusicSalesApp/Program.cs` | Composition root — dual cookie+JWT auth, ~90 DI registrations, Hangfire/SignalR/Fido2/DataProtection setup, middleware order, migrate-on-startup. |
| `MusicSalesApp/Data/AppDbContext.cs` | The single EF Core context — full entity graph and cascade-delete configuration. |
| `MusicSalesApp/Components/Base/BlazorBase.cs` | Base class for every page/component code-behind; defines the injected-service surface. |
| `MusicSalesApp.Common/Helpers/*.cs` | ~45 constants classes — check here before introducing a new status/event/key string (see `AGENTS.md`). |
| `MusicSalesApp/Models/Subscription.cs`, `Services/SubscriptionService.cs`, `Services/PayPalSubscriptionManagementService.cs`, `Services/PayPalSubscriptionAnomalyService.cs` | Multi-provider billing/entitlement core — also the most actively-changing area. |
| `MusicSalesApp/Controllers/MobileAuthController.cs`, `MobilePlaylistController.cs` | Best reference examples for the mobile API pattern (API key + JWT + DTOs + subscription gating). |
| `MusicSalesApp/Controllers/CartController.cs` | Intentionally dead shim — read before touching, don't extend. |
| `Migrations/20260108000000_RemoveCartAndOwnedSongsTables.cs`, `Migrations/20260713020755_RemoveSubscriptionPriceSetting.cs` | Read these before trusting any doc/comment describing "ownership" or "pricing settings" — this area changed significantly and older references lag. |

## Where to look next

- **`AGENTS.md`** — the living engineering handbook: magic-string/constants convention, Syncfusion CTA/theme CSS rules, the code-behind pattern (`[Component]Model : BlazorBase`, no `@inject` in components), Blazor Server DbContext-threading guidance (why `OnAfterRenderAsync(firstRender)` not `OnInitializedAsync`), the "call services directly, don't round-trip through HTTP APIs from Blazor Server" rule, passkey implementation notes, email template conventions.
- **`HANDOFF.md`** — current PayPal subscription-status reconciliation edge cases (`ACTIVE`/`SUSPENDED`/`CANCELLED`/`EXPIRED` semantics) and an open, not-yet-implemented "Refresh Subscription" mismatch-resolution plan.
- **`PAYPAL_EXPANDED_CHECKOUT.md`** — PayPal Expanded Checkout / 3D Secure integration details, now relevant to the subscription checkout flow (not song purchases).
- **`LIKED_SONGS_IMPLEMENTATION.md`** — the system-generated "Liked Songs" auto-playlist.
- **`SIGNALR_RECONNECTION_TESTING.md`**, **`FACEBOOK_SHARING_TESTING.md`** — manual test guides for real-time reconnection and OpenGraph sharing.

## Sibling repo

The MAUI listener app lives at `../MusicSalesApp_Maui` (dual-root VS Code workspace). It:

- References `MusicSalesApp.Common` directly via project reference — changes to shared constants/helpers affect both repos at once.
- Consumes the mobile API controllers listed above; auth/subscription semantics must stay in sync with `MobileAuthController`/`SubscriptionController`.
- Talks to whichever of `streamtunes.net`/`davidtest.dev` its own build configuration resolves to (see that repo's `CLAUDE.md` for details).
