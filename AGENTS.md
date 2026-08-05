# Agent Instructions - MusicSalesApp

## Working Branches

- Before editing files, always check the current branch with `git branch --show-current`.
- If the current branch is `master`, create and switch to an appropriately named working branch before making changes.
- Use clear task-based branch names such as `work/featured-playback-queue-rotation`.
- Do not make code edits on `master` unless the user explicitly asks for that.

## String Constants and Magic Strings

**CRITICAL:** Never use inline "magic" string literals for values that are written in one place and read/compared in another. Always define string constants in a shared class in the `MusicSalesApp.Common` project under the `Helpers` folder.

**Why:** A mismatch between a writer (e.g., `RecordUserHistoryAsync(..., "Registration", ...)`) and a reader (e.g., `.Where(uh => uh.EventType == "AccountCreated")`) will silently fail with no compiler error. This class of bug is extremely hard to catch.

**Rules:**
1. **Event types, status names, setting keys, role names, and any string used as a lookup key** must be defined as `public const string` in a static class in `MusicSalesApp.Common\Helpers\`.
2. Both the code that **writes** the value and the code that **reads/queries** it must reference the **same constant**.
3. When adding a new event type or key, add the constant **first**, then use it everywhere.
4. Existing constant classes to be aware of:
   - `UserHistoryEventTypes` — event types for `UserHistory.EventType` (Registration, EmailConfirmed, etc.)
   - `Roles` — user role names
   - `Permissions` — authorization policy names
   - `CustomClaimTypes` — custom claim type strings
   - `PriceDefaults` — default pricing values
   - `MusicFileExtensions` — file extension constants

**Example:**
```csharp
// ✅ CORRECT — use the constant
await RecordUserHistoryAsync(userId, email, UserHistoryEventTypes.Registration, ...);
// query also uses the same constant
.Where(uh => uh.EventType == UserHistoryEventTypes.Registration)

// ❌ WRONG — inline string that can drift out of sync
await RecordUserHistoryAsync(userId, email, "Registration", ...);
.Where(uh => uh.EventType == "AccountCreated")  // silent bug!
```

## UI Framework and Component Conventions

### Syncfusion Blazor Components
This application uses Syncfusion Blazor components for all UI elements to provide a consistent, professional look and feel. When adding or modifying UI components:

- **Always use Syncfusion components** instead of standard HTML or Bootstrap controls
- Use light theme: `bootstrap5.css` from Syncfusion.Blazor.Themes
- Common Syncfusion components used:
  - `SfButton` instead of `<button>` or Bootstrap buttons
  - `SfTextBox` instead of `<input type="text">`
  - `SfDialog` instead of Bootstrap modals
  - `SfGrid` for data tables
  - `SfCard` for card layouts
  - `SfToast` or `SfMessage` for alerts and notifications
  - `SfAppBar` for navigation bar
  - `SfSidebar` for side navigation

### Site Theme Button and Control Styling

Syncfusion components should still look like StreamTunes, not Syncfusion defaults:

- For prominent page actions on public, account, creator setup, and creator settings surfaces, prefer the existing site CTA classes over plain `IsPrimary="true"` when `IsPrimary` renders the default Syncfusion blue.
- Use the existing home-page CTA classes for purple primary creator/account actions: `cta-secondary hero-secondary-cta`, plus a page-specific hook when needed (for example `creator-settings-cta`).
- Keep destructive actions on `e-danger`; do not restyle stop/delete/destructive buttons as purple CTAs.
- Keep secondary utility/navigation actions visually quieter unless the workflow clearly treats them as the primary next step.
- If Syncfusion checkboxes, text boxes, or focused inputs appear in a themed page, scope overrides to the page container and put the purple/brand color values in `light.css` and `dark.css`. Checked checkbox states, hover states, and input focus rings should follow the site palette instead of the default blue.
- Put only structural/layout pieces for those controls in `app.css`; put spacing/sizing in the breakpoint CSS files.

### Component Code-Behind Pattern
All Blazor components and pages must follow these conventions:

- **Always create code-behind files** for Razor components (e.g., `Home.razor` with `Home.razor.cs`)
- **Code-behind class naming**: Use `[ComponentName]Model` pattern (e.g., `HomeModel` for `Home.razor`)
- **Inheritance**: Code-behind classes must inherit from `BlazorBase`
- **Razor inheritance**: Components must use `@inherits [ComponentName]Model` directive
- **No direct service injection**: Never use `@inject` in components or code-behind files
- **Use services from BlazorBase**: All services are injected into `BlazorBase` and available to derived classes

Example:
```razor
@* Home.razor *@
@page "/"
@inherits HomeModel

<SfButton>Click Me</SfButton>
```

```csharp
// Home.razor.cs
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class HomeModel : BlazorBase
{
    // Access services via properties inherited from BlazorBase
    // e.g., NavigationManager, CartService, AuthenticationService
}
```

### Testing Conventions
- Tests must also follow the BlazorBase pattern
- Use `BUnitTestBase` for component tests which provides all necessary service mocks
- Syncfusion components may require additional test setup or assertions

## CSS Organization and DRY Principles

**IMPORTANT:** This project follows DRY (Don't Repeat Yourself) principles for CSS to minimize code duplication and improve maintainability.

### DO NOT Use Component-Scoped .razor.css Files for Shared Styles

- **Avoid creating CSS rules in `.razor.css` files** when those styles may be reused across components
- Component-scoped CSS leads to significant code duplication across the application
- When the same styles are needed in multiple components, they must be duplicated in each `.razor.css` file
- Changes to shared styles require updating multiple files, increasing maintenance burden

### DO Use Global CSS Files for Shared and Responsive Styles

**For shared/reusable styles:**
- Place in `wwwroot/app.css` or create theme-specific files like `light.css` or `dark.css`
- This allows styles to be defined once and reused everywhere

**For responsive/breakpoint styles:**
- Do not place `@media` CSS rules inside component-scoped `.razor.css` files
- Use the global breakpoint files in `wwwroot` for responsive styles:
  - `xl_app.css` (wide/desktop defaults)
  - `lg_app.css` (`@media (max-width: 1200px)`)
  - `md_app.css` (`@media (max-width: 992px)`)
  - `sm_app.css` (`@media (max-width: 768px)`)
  - `xs_app.css` (`@media (max-width: 576px)`)

**Benefits of this approach:**
- Single source of truth for each style
- Changes propagate automatically across all components
- Easier to maintain consistency across the application
- Reduces CSS bundle size
- Follows industry best practices for responsive design

### When to Use .razor.css Files

Component-scoped `.razor.css` files should be used ONLY for:
- Truly component-specific styles that are never reused
- Styles that are unique to a single component's internal structure
- Layout styles that are intrinsically tied to that component's markup

### CSS Property Organization Rules

When adding or modifying CSS rules, organize properties by type into the appropriate files:

**Color Properties → Theme Files (`light.css` / `dark.css`)**
```css
/* Properties that go in theme files */
- color
- background-color
- fill
- border-color
- box-shadow (when it involves color)
```
**Rule**: Light colors → `light.css`, Dark colors → `dark.css`

**Layout/Position/Animation Properties → `app.css`**
```css
/* Properties that go in app.css */
- position
- display (flex, grid, etc.)
- flex / grid properties
- animation
- transition
- transform
- z-index
- Non-responsive structural properties
```

**Responsive/Spacing/Size Properties → Breakpoint Files**
```css
/* Properties that go in breakpoint files */
- width / height
- padding / margin
- gap
- font-size
- max-width / min-width / max-height / min-height
- Any property that changes based on screen size
```

### CSS File Organization

Ensure these files are linked in `Components/App.razor` via `<link rel="stylesheet" href="@Assets["<file>"]" />` so they apply app-wide.

Example structure:
```
wwwroot/
├── app.css           # Global base styles (layout, position, animation)
├── light.css         # Light theme color overrides
├── dark.css          # Dark theme color overrides
├── xs_app.css        # Extra small breakpoint (<576px)
├── sm_app.css        # Small breakpoint (<768px)
├── md_app.css        # Medium breakpoint (<992px)
├── lg_app.css        # Large breakpoint (<1200px)
└── xl_app.css        # Extra large breakpoint (≥1200px)
```

### Known CSS Duplications (As of Current State)

The following CSS classes exist in multiple `.razor.css` files with **identical rules**. These are candidates for consolidation when refactoring is deemed safe:

**Player Components (AlbumPlayer.razor.css & SongPlayer.razor.css):**
- `.play-button-large` - Identical in both files
- `.cart-button-large` - Identical in both files (also partial definition in app.css)
- `.controls-wrapper` - Identical in both files
- `.player-controls-row` - Identical in both files
- `.player-controls` - Identical in both files
- `.volume-controls` - Identical in both files
- `.control-button` - Identical in both files
- `.progress-container` - Identical in both files
- `.progress-bar-container` - Identical in both files
- `.volume-bar-container` - Identical in both files
- `.owned-badge` - Identical in both files
- `.preview-label` - Identical in both files
- All player bar related styles

**Container Styles with Different Colors:**
- `.spotify-container` - Used in AlbumPlayer and SongPlayer but with **different gradient backgrounds**
  - AlbumPlayer: `#1a4b30` green theme
  - SongPlayer: `#5c4b30` brown theme
  - **DO NOT consolidate** - intentionally different per component

**Note**: When these duplications are consolidated, move layout/animation properties to `app.css` and color properties to theme files according to the organization rules above.

## Metadata Storage and File Management

This document provides comprehensive guidance for AI agents working with the MusicSalesApp codebase, specifically around metadata storage and file classification.

## Core Concepts

### Metadata Storage

**IMPORTANT:** The application uses SQL Server database (SongMetadata table) to store all music metadata. Azure Blob Storage is used ONLY for file storage, NOT for metadata via index tags.

**DO NOT:**
- Query or use Azure Blob Storage index tags for metadata
- Store metadata in blob index tags
- Use methods like `ListFilesByAlbumAsync()` that query index tags

**DO:**
- Use `SongMetadataService` for all metadata operations
- Query the SQL Server `SongMetadata` table for album names, track numbers, prices, genres, etc.
- Use `GetByAlbumNameAsync()`, `GetAllAsync()`, `GetByBlobPathAsync()` methods

### File Classification System

Music files are classified into three distinct types based on their metadata in the SQL database:

### File Type 1: Album Cover Image

**Purpose:** Represents the album artwork for a collection of tracks.

**Characteristics:**
- File extension: `.jpeg`, `.jpg`, or `.png`
- Database field `IsAlbumCover: true`
- Has `AlbumName` in database
- Does NOT have an associated MP3 file (it's just the cover art)

**Required Database Fields:**
```
IsAlbumCover: true
AlbumName: [album name]
AlbumPrice: [price as decimal]
ImageBlobPath: [path to image file]
```

**Validation Requirements:**
- ✅ Album cover image file must exist
- ✅ Album price must be set and valid
- ❌ NO track number (N/A for cover images)
- ❌ NO genre (N/A for cover images)
- ❌ NO song price (album price is used instead)

**Code Example:**
```csharp
// Identifying an album cover entry
var isAlbumCoverEntry = song.IsAlbum && string.IsNullOrEmpty(song.Mp3FileName);
```

### File Type 2: Album Track MP3

**Purpose:** A music track that is part of an album.

**Characteristics:**
- File extension: `.mp3`
- Has `AlbumName` in database (associates it with an album)
- Has sequential `TrackNumber` within the album
- May have associated album art (JPEG or PNG) in the same folder

**Required Database Fields:**
```
AlbumName: [album name]
TrackNumber: [integer 1-N, unique within album]
TrackLength: [duration in seconds]
SongPrice: [price as decimal]
Genre: [genre string]
Mp3BlobPath: [path to MP3 file]
```

**Validation Requirements:**
- ✅ Track number is REQUIRED
- ✅ Track number must be >= 1
- ✅ Track number must be <= total number of tracks in the album
- ✅ Track number must be UNIQUE within the album (no duplicates)
- ✅ Song price is REQUIRED
- ✅ Genre is REQUIRED
- ✅ Track length must be present (auto-extracted, read-only)

**Code Example:**
```csharp
// Identifying an album track
var isAlbumTrack = !string.IsNullOrEmpty(song.Mp3FileName) && 
                   !string.IsNullOrEmpty(song.AlbumName);

// Validating track number uniqueness
var albumTracks = await _songMetadataService.GetByAlbumNameAsync(song.AlbumName);
var duplicateTrackNumber = albumTracks.Any(t => 
    t.TrackNumber == song.TrackNumber && 
    t.Id != song.Id);

if (duplicateTrackNumber)
{
    // Validation error: duplicate track number
}
```

### File Type 3: Standalone Song MP3

**Purpose:** A standalone music track that is NOT part of any album.

**Characteristics:**
- File extension: `.mp3`
- Does NOT have `AlbumName` in database
- Has associated album art (JPEG or PNG) with `IsAlbumCover: false`
- Independent pricing and metadata

**Required Database Fields (MP3):**
```
TrackLength: [duration in seconds]
SongPrice: [price as decimal]
Genre: [genre string]
Mp3BlobPath: [path to MP3 file]
```

**Required Database Fields (Associated Image):**
```
IsAlbumCover: false
ImageBlobPath: [path to image file]
```

**Validation Requirements:**
- ✅ Song cover image (JPEG or PNG) must exist
- ✅ Song price is REQUIRED
- ✅ Genre is REQUIRED
- ✅ Track length must be present (auto-extracted, read-only)
- ❌ NO track number (not part of an album)
- ❌ NO album name
- ❌ NO album price

**Code Example:**
```csharp
// Identifying a standalone song
var isStandaloneSong = !string.IsNullOrEmpty(song.Mp3FileName) && 
                       string.IsNullOrEmpty(song.AlbumName);
```

## Database Metadata Fields

All metadata fields are stored in the `SongMetadata` SQL table:

| Field | Description | Used For |
|-------|-------------|----------|
| `AlbumName` | Name of the album | Album covers, Album tracks |
| `IsAlbumCover` | Boolean flag | All image files |
| `AlbumPrice` | Album purchase price | Album covers |
| `SongPrice` | Individual track price | All MP3 files |
| `Genre` | Music genre | All MP3 files |
| `TrackNumber` | Sequential track position (1-based) | Album tracks only |
| `TrackLength` | Duration in seconds | All MP3 files (auto-extracted) |
| `Mp3BlobPath` | Path to MP3 file in blob storage | All MP3 files |
| `ImageBlobPath` | Path to image file in blob storage | All image files |
| `BlobPath` | Legacy field (deprecated) | Backward compatibility |

## Validation Logic Implementation

### In AdminSongManagement.razor.cs

When validating user input for saving:

```csharp
protected async Task SaveEdit()
{
    // Step 1: Determine file type
    var hasMP3 = !string.IsNullOrEmpty(_editingSong.Mp3FileName);
    var isAlbumCoverEntry = _editingSong.IsAlbum && !hasMP3;
    var isAlbumTrack = hasMP3 && !string.IsNullOrEmpty(_editingSong.AlbumName);
    var isStandaloneSong = hasMP3 && string.IsNullOrEmpty(_editingSong.AlbumName);

    // Step 2: Apply type-specific validation
    if (isAlbumCoverEntry)
    {
        ValidateAlbumCover();
    }
    else if (isAlbumTrack)
    {
        ValidateAlbumTrack();
    }
    else if (isStandaloneSong)
    {
        ValidateStandaloneSong();
    }

    // Step 3: Save to SQL database (NOT blob index tags)
    await _songMetadataService.UpsertAsync(new SongMetadata
    {
        AlbumName = _editingSong.AlbumName,
        IsAlbumCover = isAlbumCoverEntry,
        AlbumPrice = _editingSong.AlbumPrice,
        SongPrice = _editingSong.SongPrice,
        Genre = _editingSong.Genre,
        TrackNumber = _editingSong.TrackNumber,
        // ... other fields
    });
}
```

## Audio Processing and Track Length

### FFmpeg does not run in this app

**There is no ffmpeg binary in `MusicSalesApp` and no `FFMpegCore` reference.** All transcoding and
decode validation runs in the `MusicSalesApp.Functions` Azure Functions app - see that project's
README. This app runs on SmarterASP shared hosting, where every FFmpeg pass used to block a request
thread; a single WAV upload cost three of them.

What this app still does is header-level container sniffing: `AudioContainerSniffer` in
`MusicSalesApp.Common` reads 64 bytes and compares magic numbers. Cheap enough for a request thread,
and it catches a renamed or empty file instantly - but it cannot prove a file decodes.

### How a song gets uploaded now

1. `SongUploadJobService.CreateAsync` validates the title, sniffs the headers, runs the
   ownership/collision check, stages the raw bytes to the `musicuploads{-env}` container and writes
   a `SongUploadJob` row. It returns as soon as the queue message is sent - **the song does not
   exist yet.**
2. The Function transcodes to MP3, measures the duration, and POSTs to `api/media-processing/*`.
3. `MediaProcessingCompletionService` copies the staged blobs into the song's GUID folder, writes
   the `SongMetadata` row (including `TrackLength`), and generates the sharing image and renditions.

`SongMetadata` is therefore only ever written on success, which is why no catalogue query has to
filter out half-built songs.

### Two storage accounts

Song media is on a **Premium** account. No premium account type offers the Queue service, so the
queues and the staging container are on the **Standard general-purpose** account. Media never
moves. The practical consequence: staging-to-media copies are cross-account and need a source SAS
(`MediaProcessingCompletionService`), not a same-account server-side copy.

### Progress

One bar per song spans the whole lifecycle, 0-100, across all three processes.
`AudioProcessingProgressCalculator` in `MusicSalesApp.Common` owns the band table and is the single
place the percentages are defined. Progress posts are best-effort in both directions - senders
swallow failures, and the receiver drops stale or out-of-order updates so the bar cannot run
backwards.

### Track Length UI Display

- Track length is displayed in admin grid and form
- Formatted as `m:ss` or `h:mm:ss` using `TimeSpan.FromSeconds()`
- Field is READ-ONLY in UI (cannot be edited)
- Automatically populated during upload

## Common Pitfalls to Avoid

### ❌ Don't: Use IsAlbum flag for validation logic
The `IsAlbum` flag is set when an entry represents an album (has album cover), but this doesn't tell you if it's an MP3 file that needs track number validation.

```csharp
// WRONG
if (_editingSong.IsAlbum)
{
    // This catches both album covers AND album tracks
    ValidateTrackNumber(); // BUG: Album covers don't need track numbers
}
```

### ✅ Do: Check for MP3 file presence
Always verify if the entry has an MP3 file before applying MP3-specific validation:

```csharp
// CORRECT
if (_editingSong.IsAlbum && !string.IsNullOrEmpty(_editingSong.Mp3FileName))
{
    // This is an album track MP3
    ValidateTrackNumber(); // OK: Only MP3s need track numbers
}
```

### ❌ Don't: Forget to check AlbumName for track number validation
Track numbers are only required for MP3s that are PART OF AN ALBUM:

```csharp
// WRONG
if (!string.IsNullOrEmpty(_editingSong.Mp3FileName))
{
    ValidateTrackNumber(); // BUG: Standalone songs don't need track numbers
}
```

### ✅ Do: Check both MP3 presence AND album name
```csharp
// CORRECT
if (!string.IsNullOrEmpty(_editingSong.Mp3FileName) && 
    !string.IsNullOrEmpty(_editingSong.AlbumName))
{
    ValidateTrackNumber(); // OK: Only album tracks need track numbers
}
```

## Testing Considerations

When writing tests for metadata functionality:

1. **Mock SongMetadataService** to return predictable data
2. **Test all three file types** separately
3. **Verify database values** after save operations
4. **Test validation rules** for each file type
5. **Test track number uniqueness** within albums
6. **Test track number bounds** (>= 1, <= total tracks)

## References

- `MusicSalesApp.Models.SongMetadata` - Database model for metadata
- `MusicSalesApp.Services.SongMetadataService` - Service for metadata operations
- `MusicSalesApp.Services.MusicUploadService` - Upload and metadata saving logic
- `MusicSalesApp.Services.MusicService` - Track length extraction
- `MusicSalesApp.Components.Pages.AdminSongManagement.razor.cs` - Validation logic
- `MusicSalesApp.Components.Pages.AlbumPlayer.razor.cs` - Track display and playback
- `MusicSalesApp.Components.Pages.MusicLibrary.razor.cs` - Album grouping logic

## Blazor Server Component Lifecycle and DbContext Threading

### Issue: "A second operation was started on this context instance before a previous operation completed"

This error occurs in Blazor Server when multiple async operations try to use the same DbContext instance concurrently. This commonly happens during page refreshes or circuit reconnections.

### Root Cause

- `OnInitializedAsync()` can be called multiple times in Blazor Server (e.g., during circuit reconnection)
- Multiple concurrent calls to services using the same DbContext can cause threading issues
- Even with `IDbContextFactory`, rapid sequential calls during initialization can overlap

### Solution: Use OnAfterRenderAsync with firstRender

**❌ WRONG - Using OnInitializedAsync:**
```csharp
protected override async Task OnInitializedAsync()
{
    // This can be called multiple times, causing DbContext threading issues
    await LoadData();
}
```

**✅ CORRECT - Using OnAfterRenderAsync with firstRender:**
```csharp
private bool _hasLoadedData = false;

protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (firstRender && !_hasLoadedData)
    {
        _hasLoadedData = true;
        try
        {
            await LoadData();
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }
}
```

### Key Points

1. **Use `OnAfterRenderAsync(bool firstRender)`** for data loading operations
2. **Guard with `firstRender` check** to ensure it only runs once
3. **Use a `_hasLoadedData` flag** to prevent multiple loads
4. **Call `StateHasChanged()`** after data loads to update UI
5. **Use `InvokeAsync()`** when updating UI from async context

### When to Use Each Lifecycle Method

- **OnInitializedAsync**: Only for setting up event handlers or initializing non-data fields
- **OnAfterRenderAsync(firstRender)**: For data loading, API calls, and DbContext operations
- **OnParametersSetAsync**: For reacting to parameter changes (NON-database operations only)

**CRITICAL**: Never perform database operations in `OnParametersSetAsync` as it can be called multiple times during component lifecycle, leading to concurrent DbContext access issues.

### Example Pattern

```csharp
public partial class MyPageModel : BlazorBase
{
    protected bool _loading = true;
    private bool _hasLoadedData = false;
    
    protected override void OnParametersSet()
    {
        // Only set flags or simple state, NO database calls
        _isPlaylistMode = PlaylistId.HasValue;
    }
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user.Identity?.IsAuthenticated == true)
                {
                    var appUser = await UserManager.GetUserAsync(user);
                    if (appUser != null)
                    {
                        await LoadUserData(appUser.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _error = $"Error loading data: {ex.Message}";
            }
            finally
            {
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }
}
```

## Blazor Server: Prefer Direct Service Calls Over HTTP API Endpoints

### Key Principle

This is a **Blazor Server** application — all C# component code runs on the server. Components should call injected services directly instead of making HTTP requests to the app's own API controllers.

### Why This Matters

- **No HTTP round-trips needed**: Since component code already runs on the server, calling `Http.GetFromJsonAsync("api/...")` creates an unnecessary loopback HTTP request from the server to itself.
- **Avoids CDN/rate-limiting issues**: HTTP calls from Blazor Server go through the full HTTP pipeline, including any CDN (e.g., Cloudflare). Firing many parallel HTTP requests can trigger 429 (Too Many Requests) rate limiting. Cached 429 responses then persist in the browser/CDN cache, requiring users to manually clear their cache.
- **Better performance**: Direct service calls avoid HTTP serialization/deserialization overhead and network latency.
- **Simpler error handling**: No need to handle HTTP-specific errors (status codes, network failures) for server-side operations.

### Rules

1. **Never use `Http.GetFromJsonAsync` or `Http.PostAsJsonAsync`** to call the app's own API endpoints from Blazor Server components when the equivalent service is available in `BlazorBase`.
2. **Use injected services directly** — all services are available via `BlazorBase` properties (e.g., `AzureStorageService`, `SubscriptionService`, `SongMetadataService`, etc.).
3. **API controllers** are only needed for:
   - JavaScript interop calls from the browser (e.g., audio player, PayPal SDK)
   - External webhook endpoints (e.g., PayPal, TaxBandits)
   - Truly client-side HTTP requests that originate from browser JavaScript

### Example

```csharp
// ❌ WRONG — unnecessary HTTP round-trip through Cloudflare
var result = await Http.GetFromJsonAsync<StreamUrlResponseDto>($"api/music/url/{fileName}");
_streamUrl = result?.Url;

// Also WRONG — firing many parallel HTTP requests triggers CDN rate limiting
var tasks = tracks.Select(t => Http.GetFromJsonAsync<StreamUrlResponseDto>($"api/music/url/{t.Name}"));
var results = await Task.WhenAll(tasks); // 429 Too Many Requests!

// ✅ CORRECT — call the storage service directly (no HTTP, no CDN, no rate limits)
var uri = AzureStorageService.GetReadSasUri(fileName, TimeSpan.FromHours(24));
_streamUrl = uri.ToString();

// ✅ CORRECT — generate all URLs directly without any HTTP calls
var urls = tracks.Select(t => AzureStorageService.GetReadSasUri(t.Name, lifetime).ToString()).ToList();
```

### Available Services in BlazorBase

All of these can be called directly from component code-behind without HTTP:

- `AzureStorageService` — Blob storage operations (SAS URLs, uploads, downloads)
- `SubscriptionService` — Subscription status checks
- `SongMetadataService` — Song metadata queries
- `PlaylistService` — Playlist operations
- `StreamCountService` — Stream count operations
- `CreatorService` — Creator lookups
- `SongLikeService` — Like/dislike operations
- See `BlazorBase.cs` for the full list of injected services

## Handling Optional Foreign Keys and Navigation Properties

### Issue: Filtering by navigation properties when foreign key may be null

When querying entities with optional foreign keys, the navigation property may be null even with `.Include()`. Always handle this case gracefully.

### Example: SongMetadata with an optional Creator

**❌ WRONG - Assuming navigation property is always populated:**
```csharp
var songs = await context.SongMetadata
    .Include(sm => sm.Creator)
    .Where(sm => sm.Creator != null && sm.Creator.IsActive)
    .ToListAsync();
// This silently filters out songs whose CreatorId is null
```

**✅ CORRECT - Handling null navigation property with fallback:**
```csharp
var allSongs = await context.SongMetadata
    .Include(sm => sm.Creator)
    .Where(sm => sm.IsActive && sm.IsEnabled)
    .ToListAsync();

var filteredSongs = allSongs
    .Where(sm =>
    {
        // If we have a creator, only include active creators
        if (sm.Creator != null)
        {
            return sm.Creator.IsActive;
        }

        // Fallback: no creator on record (e.g. legacy import) — include by default
        return true;
    })
    .ToList();
```

### Key Points

1. **Load all data first**, then filter in memory when navigation properties might be null
2. **Always provide a fallback** when navigation property is null
3. **Document the fallback logic** so it's clear why it exists

## Playlists and Subscription Logic

**Access model: subscription-only.** There is no per-song ownership anymore — the `OwnedSong`/`CartItem` tables and the `PayPalOrderId`-based ownership tiers described in earlier versions of this doc were removed by migration `Migrations/20260108000000_RemoveCartAndOwnedSongsTables.cs`. `UserPlaylist` now references `SongMetadataId` directly.

### Playlist Access Rules

- **With an active subscription**: can add any active/enabled, non-album-cover song from the catalog to a playlist.
- **Without an active subscription**: `PlaylistService.GetAvailableSongsForPlaylistAsync` returns an empty list — no songs can be added, full stop. There is no "own at least one song" fallback.

See `Services/PlaylistService.cs` (`GetAvailableSongsForPlaylistAsync`) for the current implementation.

### Playlist Cleanup Service

`Services/PlaylistCleanupService.cs` (`RemoveNonOwnedSongsFromLapsedSubscriptionsAsync`) runs as a background job to clean up access once a subscription lapses:

**What it does, per user with a lapsed subscription (`CANCELLED`/`EXPIRED`, 48-hour grace period, and no other currently-active subscription):**
1. Removes **every** `UserPlaylists` row for that user (not just some — since there's no owned/purchased tier to preserve).
2. Deletes all of that user's **custom** (`IsSystemGenerated == false`) playlists outright.
3. **Preserves** the system-generated "Liked Songs" playlist — it isn't deleted, and re-populates from the user's actual likes on next sync.

**Grace Period:** 48 hours after subscription end date, to account for job execution delays.

### Implementation Notes

- `PlaylistService.GetAvailableSongsForPlaylistAsync` checks `ISubscriptionService.HasActiveSubscriptionAsync` — no subscription means no available songs.
- Cleanup is automatic via the `PlaylistCleanupService` background job (Hangfire-scheduled).

## Passkey Authentication

### Overview

This application implements WebAuthn/FIDO2 passkey authentication using the Fido2 library. Passkeys provide a secure, passwordless authentication method using biometric authentication or security keys.

### Implementation Details

**Database Model:**
- `Passkey` table stores user passkeys with credentials and metadata
- Links to `ApplicationUser` via `UserId` foreign key
- Unique index on `CredentialId` to prevent duplicate passkeys
- Fields: `Name`, `CredentialId`, `PublicKey`, `AttestationObject`, `ClientDataJSON`, `SignCount`, `AAGUID`, `CreatedAt`, `LastUsedAt`

**Services:**
- `IPasskeyService` / `PasskeyService` - Core passkey operations
  - `BeginRegistrationAsync()` - Start passkey registration flow
  - `CompleteRegistrationAsync()` - Complete passkey registration
  - `BeginLoginAsync()` - Start passkey login flow
  - `CompleteLoginAsync()` - Complete passkey login
  - `GetUserPasskeysAsync()` - Get user's passkeys
  - `DeletePasskeyAsync()` - Delete a passkey
  - `RenamePasskeyAsync()` - Rename a passkey

**Controllers:**
- `PasskeyController` - API endpoints for passkey operations
  - POST `/api/passkey/register/begin` - Begin registration
  - POST `/api/passkey/register/complete` - Complete registration
  - POST `/api/passkey/login/begin` - Begin login
  - POST `/api/passkey/login/complete` - Complete login
  - GET `/api/passkey/list` - List user passkeys
  - DELETE `/api/passkey/{passkeyId}` - Delete passkey
  - PUT `/api/passkey/{passkeyId}/rename` - Rename passkey

**Pages:**
- `ManageAccount.razor` - Passkey management UI
  - Add new passkeys with custom names
  - View list of registered passkeys with creation/last use dates
  - Rename existing passkeys
  - Delete passkeys
  - Also includes password change and account deletion
- `Login.razor` - Updated with passkey login option
  - Shows "Login with Passkey" button
  - Requires username/email first to identify user

**JavaScript Integration:**
- `ManageAccount.razor.js` - JavaScript helper for WebAuthn API calls
  - `passkeyHelper.registerPasskey()` - Handles credential creation
  - `passkeyHelper.loginWithPasskey()` - Handles credential assertion
  - Base64 encoding/decoding helpers for binary data

**Configuration:**
- `appsettings.json` includes Fido2 configuration:
  ```json
  "Fido2": {
    "ServerDomain": "localhost",
    "Origins": ["https://localhost:5001", "http://localhost:5000"],
    "TimestampDriftTolerance": 300000
  }
  ```

**Testing:**
- `BUnitTestBase` includes `MockPasskeyService` for component testing
- `ManageAccountTests` - Tests for passkey management UI
- Updated `LoginTests` - Tests for passkey login button

### Important Notes

1. **Session Storage**: Current implementation uses in-memory dictionary for storing options during registration/login flow. In production, use distributed cache (Redis) with user session.

2. **Browser Support**: Passkeys require browser support for WebAuthn API. Modern browsers (Chrome, Edge, Firefox, Safari) all support this.

3. **User Experience**: 
   - Users must enter username/email before clicking "Login with Passkey"
   - Passkey names help users identify which device/authenticator they used
   - Users can have multiple passkeys (e.g., laptop, phone, security key)

4. **Security**: 
   - Passkeys use public key cryptography - private keys never leave the device
   - SignCount prevents replay attacks
   - User verification (biometric or PIN) required by default

5. **Fallback**: Password authentication remains available alongside passkeys

## Email Templates and Conventions

### Logo in Emails
**IMPORTANT:** All emails sent to users must include the StreamTunes logo for brand consistency. The logo should be placed at the top of the email body.

**Logo URL Pattern:**
```
{baseUrl}/images/logo-light-small.png
```

**Example Email Header:**
```html
<div style='text-align: center; margin-bottom: 20px;'>
    <img src='{logoUrl}' alt='StreamTunes Logo' style='max-width: 150px; height: auto;' />
</div>
```

**Emails that include the logo:**
- Email verification emails
- Password reset emails
- Purchase confirmation emails
- Subscription confirmation emails
- New song notification emails

### Email Preferences Link
All marketing/notification emails (not transactional like password reset) should include a "Manage email preferences" link in the footer that points to the Manage Account page.

**Example Footer:**
```html
<p style='color: #999; font-size: 12px;'>
    <a href='{baseUrl}/manage-account' style='color: #666; text-decoration: underline;'>Manage your email preferences</a>
</p>
```

### Avoiding Spam Filters
To minimize the chance of notification emails being flagged as spam:
- Send individual emails (not bulk BCC)
- Send in small batches (10 emails at a time)
- Add delays between individual emails (5 seconds)
- Add delays between batches (60 seconds)
- Only send to users who have opted in and confirmed their email
- Include clear unsubscribe/preferences links

### Customer Service Email Address

**CRITICAL:** Never hardcode email addresses (e.g., `support@streamtunes.net` or `customerservice@streamtunes.net`) in code that sends emails programmatically. Always read the customer service email from configuration.

**Configuration Key:** `EmailSettings:CustomerServiceEmail` in `appsettings.json`

**Usage by context:**
- **Services with `IConfiguration` injection:** `_configuration["EmailSettings:CustomerServiceEmail"]`
- **Classes inheriting `BlazorBase`:** `Configuration["EmailSettings:CustomerServiceEmail"]`
- **Standalone `ComponentBase` classes:** Inject `[Inject] protected IConfiguration Configuration { get; set; }` and use `Configuration["EmailSettings:CustomerServiceEmail"]`

**Exceptions:** Static legal/informational pages (TermsOfUse, PrivacyPolicy, CreatorAgreement, LearnMore) and the NavMenu "Contact Us" link display the email directly in HTML as user-facing contact information — these are acceptable as hardcoded.

**Important:** `support@streamtunes.net` does NOT exist as a real email address. Never use it anywhere.

## References
