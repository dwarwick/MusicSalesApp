# Creator upload — manual test plan

What automated tests cannot reach. Everything here needs a real browser, real storage, or a real
creator, and most of it fails **silently in every server-side log we own** when it fails — a browser
PUT that is rejected never reaches anything of ours.

Run against **davidtest.dev first**. Blob CORS is account-wide and shared with Production, so the
`DirectToStorageUploadEnabled` flag is the only thing keeping the two rollouts independent.

Storage to watch while testing (staging is `musicuploads-dev` / `musicuploads`):

| Signal | Means |
|---|---|
| `batchDirs` 0 → 1 → 0 | Images staged for matching, then swept once songs were created |
| `src` climbing | Audio landing in each song's staging folder |
| `cover` | How many songs got artwork copied in — compare against what you paired |
| `playback` | The Function's transcode landed |
| `audio-transcode-poison` off 0 | **Should never happen.** Five failed dequeues |

---

## 1. Batch shapes

| # | Setup | Expected |
|---|---|---|
| 1.1 | 2–3 songs + matching images, checkbox **on** | Review step appears; every pairing correct; publishes |
| 1.2 | Same, checkbox **off** | **No review step.** Matching still runs — `batchDirs` still goes 1→2→1 |
| 1.3 | Audio only, checkbox **on** | **No review step.** `batchDirs` and `cover` never move; the Function is never asked to match |
| 1.4 | Images + one that matches nothing (e.g. a photo of a person) | The odd image arrives **unmatched**, in the box above the table. It must not be force-paired |
| 1.5 | One song whose title is blank or duplicates an existing song | Review step appears with that row highlighted, whatever the checkbox says |
| 1.6 | **Exactly one audio + one image, with unrelated filenames** (e.g. `track-final-v3.wav` + `artwork.png`) | Paired **automatically**. Still pauses if the checkbox is on, so the title can be edited or the image removed |

**1.6 never calls the model.** One song and one image is a pair whatever they are called, so asking
would spend an OCR pass and ~25 seconds on a question with one possible answer — and could answer it
*wrongly*, since the prompt is now firmly told not to pair on weak evidence. Watch that `cover-art-match`
never moves and the pairing appears near-instantly. Filenames deliberately should **not** match, or the
test proves nothing.

**1.4 is the OpenAI prompt fix.** Before it, the matcher treated pairing as an assignment problem and
handed the leftover image to the leftover song on no evidence at all.

## 2. Cover-art re-pairing (review step)

| # | Action | Expected |
|---|---|---|
| 2.1 | Drag a pooled image onto a song row's Cover Art cell | Assigned; leaves the pool |
| 2.2 | **Tap** a pooled image, then **tap** a song's Cover Art cell | Same. This is the only path that works on a touch screen |
| 2.3 | Drag one song's artwork onto another song that already has artwork | They **swap**. Neither ends up in the pool |
| 2.4 | Drag a song's artwork onto a song with none | Source row goes bare; nothing pooled |
| 2.5 | Drag a song's artwork to the box above the table | Returns to the pool; song goes bare |
| 2.6 | Click the ✕ on a row's artwork | Same as 2.5 |
| 2.7 | Assign every image, so the pool empties | The box disappears. Picking a row's artwork up brings it back as a drop target |
| 2.8 | Untick the checkbox mid-review | Pairing controls disappear **for the batch on screen** — the label says so |

**Every empty Cover Art cell must show a green dashed box reading "Tap or Drop Cover Art Here".**
The instruction above the table names that box; the two are edited together.

⚠️ **2.1 and 2.3 could not work at all before the guard fix.** A document-level listener forced
`dropEffect = 'none'` outside the drop box, so internal drags showed the not-allowed cursor, and
dropping on the upload box was read as a new file selection that discarded the batch.

## 3. Large files and the block path

| # | Setup | Expected |
|---|---|---|
| 3.1 | One file **over 4 MB** (`SINGLE_PUT_THRESHOLD`) | Put Block ×N + Put Block List. Any block-ID mistake shows as a failed song with an `x-ms-error-code` |
| 3.2 | A file near the admin cap (150 MB) on a slow connection | Completes. If it runs past 30 minutes the token is renewed rather than the transfer being lost |
| 3.3 | A file **over** the admin cap | Whole batch rejected up front, naming the file. Nothing reaches Azure |
| 3.4 | A cover image over the image cap | **The song still publishes, without artwork.** Its audio is already staged and valid |

**3.2 is the SAS renewal path.** Hard to trigger deliberately — throttle the connection, or shorten
`StagingUploadSasLifetime` in config to a minute or two and upload anything sizeable. Look for
`Renewed the upload token for slot N` in the log.

## 4. Interruption and cleanup

| # | Action | Expected |
|---|---|---|
| 4.1 | Navigate away mid-upload, confirm the prompt | Browser transfers **stop**. Queued songs finish regardless |
| 4.2 | Close the tab mid-upload | Same, after the 3-minute disconnected-circuit window |
| 4.3 | Cancel at the review step | `batchDirs` returns to its baseline — staged images swept |
| 4.4 | At the review step, drop a **different** set of files | Previous batch abandoned **and its staged images swept** |
| 4.5 | Any song fails | Staging holds no orphan folder for it |

⚠️ **4.1 is not covered by the cancellation token.** The bytes move browser→Azure with the server
not in the path; only `abortAll` stops them.

## 5. Things that must not regress

| # | Check | Why |
|---|---|---|
| 5.1 | The drop box is **hidden** during the review step | A creator dragging a cover toward the table hit it and wiped their batch |
| 5.2 | The batch bar is **still and grey** while reviewing, not striped | Animated stripes read as "working, wait", so the upload button looked like decoration |
| 5.3 | Rows read **"Not uploaded"**, not "Pending" | "Pending" reads as queued behind work already running |
| 5.4 | The message names the button — *"…then choose 'Upload N Songs'"* | |
| 5.5 | Upload button appears **above** the table as well as below | At 50 rows the only copy scrolls off screen |
| 5.6 | Upload two batches in one visit without reloading | The completion email for batch B lists **only B's songs** |
| 5.7 | An image named `Cover.PNG` paired with audio named `cover.wav` | Assigned exactly once — never both pooled and assigned |
| 5.8 | A batch bounced back for a bad title | Unplaced images are **still in the pool**. They used to be silently discarded |

## 6. Admin control

| # | Action | Expected |
|---|---|---|
| 6.1 | Admin → Settings shows **Creator Upload Route** | Reflects the current database value |
| 6.2 | Tick/untick it | Save button enables; Cancel reverts it |
| 6.3 | Save | Takes effect on the **next** upload-page load. Anyone mid-batch keeps their route |
| 6.4 | Check the log after saving | `Direct-to-storage creator uploads turned ON/OFF by an administrator` at Warning |

**6.3 is the rollback drill.** Worth doing once deliberately before you need it in anger — it beats
discovering the flag is SQL-only while creators are failing.

## 7. Deployment order

1. **Provision** — `.\Provision-FunctionApp.ps1 -Environment Test -WhatIf`, then without `-WhatIf`.
   The CORS check now requires an existing rule to permit `PUT` **and** `*` headers, not merely name
   the origin. An already-correct account should report *"Blob CORS already allows PUT from …"*; if it
   suddenly reports the origins as missing, the check is over-strict — find out here, not on Production.
2. **Blazor app**, before the Function — it must understand every step the Function can report before
   the Function starts reporting them.
3. **Function** — `pwsh ./Invoke-FunctionPublish.ps1 -FunctionAppName streamtunes-media-test`
4. Run sections 1–6.
5. Repeat for Production, `-Environment Production` and `streamtunes-media-prod`.

### Verifying CORS and the write token without uploading anything

Both of these are otherwise only provable by a real creator upload, and both fail invisibly:

```powershell
# Preflight, exactly as the browser sends it. Expect Access-Control-Allow-Origin echoing the site.
Invoke-WebRequest -Uri "https://<account>.blob.core.windows.net/<container>/probe.bin" -Method Options -Headers @{
  "Origin" = "https://streamtunes.net"
  "Access-Control-Request-Method" = "PUT"
  "Access-Control-Request-Headers" = "x-ms-blob-type,content-type"
} -SkipHttpErrorCheck
```

Then mint a `cw` SAS with `az storage blob generate-sas` and PUT a few bytes — once as a single PUT,
once as Put Block ×2 + Put Block List with zero-padded base64 block IDs. Both must return `201`.
Delete the probes afterwards.
