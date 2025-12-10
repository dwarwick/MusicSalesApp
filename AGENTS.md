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

## Responsive CSS Breakpoints
- Do not place `@media` CSS rules inside component-scoped `.razor.css` files.
- Use the global breakpoint files in `wwwroot` for responsive styles:
  - `xl_app.css` (wide/desktop defaults)
  - `lg_app.css` (`@media (max-width: 1200px)`)
  - `md_app.css` (`@media (max-width: 992px)`)
  - `sm_app.css` (`@media (max-width: 768px)`)
  - `xs_app.css` (`@media (max-width: 576px)`)
- Component styles should remain base-only; move responsive adjustments to the appropriate global file.
- Ensure these files are linked in `Components/App.razor` via `<link rel="stylesheet" href="@Assets["<file>"]" />` so they apply app-wide.

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

### File Type 1: Album Cover JPEG

**Purpose:** Represents the album artwork for a collection of tracks.

**Characteristics:**
- File extension: `.jpeg` or `.jpg`
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
- May have associated JPEG cover art in the same folder

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
- Has associated JPEG cover art with `IsAlbumCover: false`
- Independent pricing and metadata

**Required Database Fields (MP3):**
```
TrackLength: [duration in seconds]
SongPrice: [price as decimal]
Genre: [genre string]
Mp3BlobPath: [path to MP3 file]
```

**Required Database Fields (Associated JPEG):**
```
IsAlbumCover: false
ImageBlobPath: [path to image file]
```

**Validation Requirements:**
- ✅ Song cover image (JPEG) must exist
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
