# Agent Instructions - MusicSalesApp

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

## Track Length Extraction

### Implementation Details

Track length is automatically extracted during file upload using FFMpeg:

1. **When:** During upload, after MP3 conversion (if needed)
2. **How:** Uses `MusicService.GetAudioDurationAsync()`
3. **Storage:** Saved in SQL `SongMetadata.TrackLength` field as double (e.g., 245.67)
4. **Scope:** ALL MP3 files (album tracks AND standalone songs)

### FFMpeg Approach

```csharp
// Primary method: Use FFMpeg with null output
var duration = await FFMpegArguments
    .FromFileInput(tempFilePath)
    .OutputToFile("NUL", true, options => options.WithCustomArgument("-f null"))
    .NotifyOnProgress(progress => duration = progress)
    .ProcessAsynchronously(throwOnError: false);

// Fallback: Try FFProbe if available
var mediaInfo = await FFProbe.AnalyseAsync(tempFilePath);
```

### UI Display

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
- **OnParametersSetAsync**: For reacting to parameter changes

### Example Pattern

```csharp
public partial class MyPageModel : BlazorBase
{
    protected bool _loading = true;
    private bool _hasLoadedData = false;
    
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

## Handling Optional Foreign Keys and Navigation Properties

### Issue: Filtering by navigation properties when foreign key may be null

When querying entities with optional foreign keys, the navigation property may be null even with `.Include()`. Always handle this case gracefully.

### Example: OwnedSong with optional SongMetadataId

**❌ WRONG - Assuming navigation property is always populated:**
```csharp
var songs = await context.OwnedSongs
    .Include(os => os.SongMetadata)
    .Where(os => os.SongMetadata != null && !os.SongMetadata.IsAlbumCover)
    .ToListAsync();
// This filters out songs where SongMetadataId is null
```

**✅ CORRECT - Handling null navigation property with fallback:**
```csharp
var ownedSongs = await context.OwnedSongs
    .Include(os => os.SongMetadata)
    .Where(os => os.UserId == userId)
    .ToListAsync();

var filteredSongs = ownedSongs
    .Where(os => 
    {
        // If we have metadata, use it
        if (os.SongMetadata != null)
        {
            return !os.SongMetadata.IsAlbumCover;
        }
        
        // Fallback: check filename extension
        var fileName = os.SongFileName.ToLowerInvariant();
        return fileName.EndsWith(".mp3");
    })
    .ToList();
```

### Key Points

1. **Load all data first**, then filter in memory when navigation properties might be null
2. **Always provide a fallback** when navigation property is null
3. **Use filename patterns** as fallback for file type detection
4. **Document the fallback logic** so it's clear why it exists

## Playlists and Subscription Logic

### Playlist Access Rules

Users can create and manage playlists under these conditions:
- **With Active Subscription**: Can add ANY song from the catalog to playlists
- **Without Subscription**: Can only add songs they own (purchased) to playlists
- **Requirement to Create Playlists**: Must have either an active subscription OR own at least one song

### Song Ownership Types

The application distinguishes between two types of song ownership using the `PayPalOrderId` field in the `OwnedSong` table:

1. **Purchased Songs** (`PayPalOrderId` is set):
   - User paid for the song via PayPal
   - Permanent ownership - remains available even after subscription ends
   - Can be added to playlists at any time
   - Example: `PayPalOrderId = "ORDER-XYZ123"`

2. **Subscription-Based Songs** (`PayPalOrderId` is null):
   - "Virtual" ownership granted during active subscription
   - Temporary access - removed when subscription lapses
   - Created automatically when subscriber adds non-owned song to playlist
   - Example: `PayPalOrderId = null`

### Playlist Cleanup Service

The `PlaylistCleanupService` runs as a background job to clean up subscription-based access:

**What it does:**
1. Finds users with lapsed subscriptions (CANCELLED or EXPIRED status, 48-hour grace period)
2. Identifies subscription-based songs (where `PayPalOrderId` is null)
3. Removes these songs from user's playlists (`UserPlaylists` table)
4. **Deletes the `OwnedSong` records** where `PayPalOrderId` is null (subscription-only access)
5. Preserves purchased songs (with PayPal order ID) in playlists

**Grace Period:** 48 hours after subscription end date to account for job execution delays

**Example:**
```csharp
// Find subscription-based songs to clean up
var nonOwnedSongs = userPlaylistSongs
    .Where(up => string.IsNullOrEmpty(up.OwnedSong.PayPalOrderId))
    .ToList();

// Remove from playlists
context.UserPlaylists.RemoveRange(nonOwnedSongs);

// Delete OwnedSong records (subscription-only)
var ownedSongsToDelete = await context.OwnedSongs
    .Where(os => ownedSongIdsToDelete.Contains(os.Id) && 
                os.UserId == userId &&
                string.IsNullOrEmpty(os.PayPalOrderId))
    .ToListAsync();
context.OwnedSongs.RemoveRange(ownedSongsToDelete);
```

### Implementation Notes

- `PlaylistService.GetAvailableSongsForPlaylistAsync` checks subscription status
- For subscribers: returns all non-album-cover songs from catalog
- Creates `OwnedSong` records on-demand with `PayPalOrderId = null`
- These records enable playlist functionality without schema changes
- Cleanup is automatic via `PlaylistCleanupService` background job

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

## References
