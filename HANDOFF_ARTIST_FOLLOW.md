# Handoff — Artist follow & listener engagement

Branch: `work/artist-follow-engagement`, in **both** repos.
Written 2026-09-05. Server + web are done and tested; the MAUI client is not started.

The commit messages on this branch carry the reasoning for each change and are worth reading
(`git log master..HEAD`). This file covers only what is **not** recoverable from the code, the
tests or the commit history: what is still open, what has to happen in what order, and the traps
that already cost time once.

---

## 1. Picking this up on another machine

Both repos need the same branch, and they need to be **siblings on disk**. The MAUI csproj
references the Blazor repo by relative path:

```
<ProjectReference Include="..\..\MusicSalesApp\MusicSalesApp.Common\MusicSalesApp.Common.csproj" />
```

so the layout must be `<parent>/MusicSalesApp` and `<parent>/MusicSalesApp_Maui`. Nothing builds
otherwise, on either side.

```
git -C <parent>/MusicSalesApp        fetch && git -C <parent>/MusicSalesApp        checkout work/artist-follow-engagement
git -C <parent>/MusicSalesApp_Maui   fetch && git -C <parent>/MusicSalesApp_Maui   checkout work/artist-follow-engagement
```

Both working trees were clean at the time of writing.

### Files that are NOT in git and have to be restored per machine

All gitignored, all needed for push to do anything:

| Repo | Path | Where it comes from |
|---|---|---|
| Blazor | `MusicSalesApp/App_Data/Secrets/firebase-service-account.test.json` | Google Cloud service account, Test Firebase project |
| Blazor | `MusicSalesApp/App_Data/Secrets/firebase-service-account.production.json` | same, Production project |
| MAUI | `MusicSalesApp.Maui/Platforms/Android/google-services.Test.json` | Firebase console, Test project |
| MAUI | `MusicSalesApp.Maui/Platforms/Android/google-services.Production.json` | Firebase console, Production project |

Both builds are `Exists()`-guarded, so **a fresh clone builds fine and simply has no push**. Do not
read a silent build as proof push is broken — check the files are there first.

The two iOS plists (`GoogleService-Info.{Test,Production}.plist`) do not exist yet anywhere; see
§4.1.

### Verification

```
dotnet build MusicSalesApp.slnx
dotnet test  MusicSalesApp.Tests           # 2691 passing
dotnet test  MusicSalesApp.ComponentTests  # 655 passing, 6 skipped
```

Both were green at `dcf9b44` / `01a8248`.

---

## 2. What is done

**Server + web (Blazor), complete and covered by tests:**

- Follow/unfollow keyed on `CreatorPersona`, soft-deleted on unfollow so the pseudonym survives a
  re-follow. Self-follow refused.
- The privacy boundary in both directions — creators see `Listener #4817` and nothing else;
  listeners see a persona name and nothing about the account behind it.
- Opt-in consent for a creator to be **named** to artists they follow, with a "Follow as" picker,
  read-time resolution so withdrawal is retroactive, and pseudonym rotation on withdrawal.
- One thank-you per follower, guarded by `ArtistMessageContentPolicy` (in `Common`, so the mobile
  client inherits it) and a filtered unique index.
- Release notifications, artist messages, per-artist mute, block, report, and the
  `/admin/artist-messages` moderation queue.
- Creator dashboard at `/creator/followers`; listener sections inside `/manage-account`.
- Push dispatch to FCM, covering Android end to end.
- Mobile API at `api/mobile/follows` — built, unconsumed.

**MAUI:** push *registration* only (Android). No follow UI of any kind.

---

## 3. Do this first — the release-email path has never been proven end to end

An attempt on 2026-09-05 produced no email. Diagnosed from the server log; not yet retried.
Three separate things were wrong, and they have to be fixed in this order.

### 3.1 Turn the preference back on, and check it is the right one

`DefaultFollowNotificationsOff` (migration `20260905205920`) runs

```sql
UPDATE AspNetUsers SET ReceiveArtistReleaseEmails = 0, ReceiveArtistMessageEmails = 0,
                       ReceiveArtistReleasePush   = 0, ReceiveArtistMessagePush   = 0
```

wholesale. That was judged safe because the columns had never shipped — but **davidtest.dev had
been used for testing, so any preference set there before deploying that migration was wiped.**
Re-tick the box after deploying, not before.

There are three similar checkboxes on `/manage-account` and only the middle one drives this
feature:

| Label | Column | Job |
|---|---|---|
| Receive email notifications when new music is added | `ReceiveNewSongEmails` | `send-new-song-notification-emails` — a **different**, older, site-wide feature |
| Email me when artists I follow release new music | `ReceiveArtistReleaseEmails` | the follow chain below |
| Email me when an artist I follow sends me a message | `ReceiveArtistMessageEmails` | `send-artist-message-emails` |

### 3.2 Run BOTH jobs, in order

The release path is two Hangfire jobs and the first is what makes the second possible:

1. **`create-artist-release-notifications`** — stamps `SongMetadata.FirstPublishedAtUtc` and
   inserts the notification rows.
2. **`send-artist-release-notification-emails`** — sends them.

`CreatePendingNotificationsAsync` is the **only** thing in the codebase that writes
`FirstPublishedAtUtc` — nothing stamps it at upload — so until job 1 runs, a newly uploaded song
has no publish date and is invisible to the whole feature. Running job 2 alone can never do
anything.

The failed attempt triggered `send-artist-message-emails`, which is neither of them.

### 3.3 Know that a wrong-order run consumes the notification

`SendPendingEmailsAsync` deliberately stamps `EmailSentDateUtc` on rows it decides **not** to send
(opted out, unconfirmed email, suspended, song withdrawn) so the nightly job stops reconsidering
them. That means:

> Running the send job while the preference is off consumes the notification permanently. Ticking
> the box afterwards will not resend it.

Recovering a burnt row means clearing `EmailSentDateUtc` by hand, or uploading another song.

### 3.4 The remaining gates, if it still does not fire

Song must be `IsActive`, `IsEnabled`, not an album cover, have a non-empty `Mp3BlobPath`
(**a song still processing does not qualify**) and a non-null `PersonaId`.

Listener must have `EmailConfirmed`, not be suspended; the follow must be active, unblocked, with
`ReleaseNotificationsEnabled`, and `FollowedDateUtc <= FirstPublishedAtUtc`.

### 3.5 A diagnostic gap worth closing

`CreatePendingNotificationsAsync` logs only when it stamps or creates something. A run that
qualifies nothing is **silent** — indistinguishable from the job not running at all. Adding a
"considered N songs, created 0" line at Information would make this self-explaining. Not done;
it was offered and not yet decided.

---

## 4. Open work

### 4.1 iOS push — four things missing

The APNs half is complete: `AppDelegate` binds both selectors and hands the raw token to
`ApplePushTokenBroker`, authorization and `RegisterForRemoteNotifications` are correct, and the
entitlement is in `Platforms/iOS/Entitlements.plist` wired via `CodesignEntitlements`.

What is missing:

1. **The Firebase iOS SDK is not referenced at all.** `Xamarin.Firebase.Messaging` sits inside the
   `== 'android'` ItemGroup. This is the actual blocker: FCM on iOS is a *relay*, so the device
   gets an APNs token — which it does — but Firebase must exchange that for an FCM registration
   token, and the FCM token is what the server stores.
2. **`ApplePushRegistrationService.IsSupported` is hard-coded `false`**, deliberately. Flipping it
   today would register raw APNs tokens that FCM rejects on every send, which look exactly like
   uninstalled devices from the dispatcher's side. Once the binding lands this becomes: set
   `Messaging.SharedInstance.ApnsToken` from the AppDelegate callback, and return
   `Messaging.SharedInstance.FcmToken` from `GetTokenAsync`.
3. **Neither `Platforms/iOS/GoogleService-Info.{Test,Production}.plist` exists.** The csproj
   already carries the `Exists()`-guarded `BundleResource` items; they just need downloading from
   the two Firebase consoles.
4. **Console configuration** — the App ID needs "Push Notifications" enabled and the provisioning
   profile **regenerated afterwards**; the APNs auth key (Key ID `9RTLMRH4GX`, Team ID
   `K7ZGP97YV6`) must be uploaded under Cloud Messaging in **both** Firebase projects. A missing
   key fails silently, on iOS only.

### 4.2 `aps-environment` is never rewritten — decision needed

`Platforms/iOS/Entitlements.plist` carries a comment saying the value "is rewritten per
configuration rather than being switched by hand". **No such rewrite exists** — the csproj,
targets and publish scripts were searched and nothing touches it. The file ships a literal
`development`.

Harmless for Debug and TestFlight, and harmless today because iOS registration is off. It bites
the moment iOS push ships: an App Store build carrying `development` gets tokens APNs rejects as
`BadDeviceToken`, which reads as a server misconfiguration rather than a build one.

Either add the MSBuild rewrite keyed on configuration, or correct the comment to say it is manual.
Do not leave the comment claiming something the build does not do.

### 4.3 The MAUI follow client — not started

Nothing exists: `SongDto` has no `PersonaId`, there is no `IFollowService`, no follow button, no
Following page, no Artist Messages page, no in-app preference toggles, and no deep-link routing
for a notification tap. The push payload already carries `PushDataKeys.Kind` / `PersonaId` /
`SongId` / `EntityId`, and `StreamTunesFirebaseMessagingService` puts them on the launch intent,
so the routing has everything it needs.

Three server rules the client has to mirror rather than rediscover:

- **Self-follow is refused** (`CannotFollowSelf`, returned as a 400 like every other domain
  refusal), so the button must be absent on your own songs rather than present and failing.
- **One artist owns many cards**, so following from one card has to move every other card for that
  persona on screen. The web does this with a shared followed-persona set on the parent, not a
  broadcast; the equivalent here is a notifier the card ViewModels subscribe to.
- **"Follow as" needs a server endpoint that does not exist.**
  `PUT api/mobile/follows/{personaId}` accepts `followAsPersonaId`, but nothing exposes
  `GetFollowAsOptionsAsync` — it is service-only and the web reads it directly. Until that
  endpoint is added, the client should send nothing and follow anonymously, which fails in the
  safe direction.

### 4.4 Reciprocal blocking — a design decision, deliberately not taken

Blocking is **one-directional**. It governs what reaches the listener: messages, release
notifications, follower-list membership. It does **not** stop the blocked creator following that
listener's own persona back, and it does not touch the catalogue — a blocked artist's music still
plays, likes and adds to playlists.

This surprised the person who asked, so it is easy to assume otherwise; a test now pins it
(`SetBlocked_DoesNotStopTheArtistFollowingTheListenerBack`). The listener-facing copy is silent on
it rather than claiming protection that does not exist.

Making it mutual would be a new guard in `SetFollowStateAsync` plus a copy change. Not done — it
is a product call.

---

## 5. Before any push testing

**`PushNotificationsEnabled` is off by default and must be switched on at `/admin/settings`**
("Phone Notifications"). While it is off, `ArtistPushDispatchService` returns before it looks at
anything, and the phone checkboxes are hidden from `/manage-account` entirely.

Two things about it that are easy to misread:

- Off leaves every row **unstamped**, exactly like an unconfigured transport, so turning it on
  later delivers the backlog rather than a silence that has already eaten it.
- **Device registration keeps working while it is off**, on purpose — registering is how the round
  trip gets proven before delivery is switched on.

So a device that registers cleanly and receives nothing is the expected state until that flag is
on *and* the listener has opted in. Both default off.

---

## 6. Traps already paid for

Each of these cost real time once. They are also recorded in the two `CLAUDE.md` files.

**EF scaffolds nothing for column defaults set by hand in an earlier migration.** The model diff
cannot see them, so `AlterColumn` calls have to be written manually, in both directions. This bit
twice on this branch.

**EF's sentinel for `bool` is `false`.** A model-level `HasDefaultValue(true)` makes EF skip
writing an explicit `false`, so a listener unchecking a box would silently keep receiving. If one
of these is ever defaulted on again, set the column default in a migration, not in the model.

**`EXEC(N'...')` is required only when a migration statement names a column that same migration
creates** — Web Deploy batches the whole migration into one transaction and SQL Server parses the
batch before running any of it. Statements against columns that already exist do not need it.

**The follow bell has no CSS of its own.** It is styled by joining the existing `.like-button`
selector groups, in every sheet. Bespoke rules were tried first and produced a visible square,
because card and player overrides applied to like/dislike and not to the newcomer.
`FollowBellCssTests` reads the stylesheets as text and fails if a group is missed.

**The players are dark in both site themes.** Anything inside `.song-player-container` /
`.playlist-player-container` must take `--st-player-*` colours, defined once in `app.css`. Putting
a light variant in `light.css` paints a white box on a near-black hero — that is exactly what the
bell was reported for.

**Sibling components on `/manage-account` do not know about each other.** Artist messages raises
`OnMessagesChanged` and the page relays it to the Following section's unread counts. A section
that changes something another section displays has to say so, or the other goes stale until
reload.

**Tests must state their preconditions now that the defaults are off.** "Has a registered device"
is no longer enough to be sent anything — the listener has to have asked. See
`ArtistFollowTestHarness.OptListenerIntoEmailsAsync` and the equivalent inside
`ArtistPushDispatchServiceTests`; seven tests failed at once when the defaults flipped and the
cause was not where it appeared to be.

---

## 7. Deployment notes

- **davidtest.dev is a Web Deploy of the working tree**, not of a commit. Verify what is actually
  running via the PubTmp DLL rather than assuming the branch state matches.
- **Migrations auto-apply at startup.** Deploying this branch applies `DefaultFollowNotificationsOff`
  and resets the four notification columns for every user — see §3.1.
- Test and Production are **separate Firebase projects**, so a test broadcast can never reach a
  production device. The server picks by environment config; the app bakes it in at build time via
  the `FirebaseEnvironment` MSBuild property.
