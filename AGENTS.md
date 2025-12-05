# Agent Instructions - MusicSalesApp

## Index Tags and File Management

This document provides comprehensive guidance for AI agents working with the MusicSalesApp codebase, specifically around index tags and file classification.

## Core Concepts

### File Classification System

The application uses Azure Blob Storage index tags to classify music files into three distinct types. Proper classification is essential for validation, display, and management.

### File Type 1: Album Cover JPEG

**Purpose:** Represents the album artwork for a collection of tracks.

**Characteristics:**
- File extension: `.jpeg` or `.jpg`
- Has `IsAlbumCover: true` index tag
- Has `AlbumName` index tag pointing to the album
- Does NOT have an associated MP3 file (it's just the cover art)

**Required Index Tags:**
```
IsAlbumCover: true
AlbumName: [album name]
AlbumPrice: [price as decimal]
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
- Has `AlbumName` index tag (associates it with an album)
- Has sequential `TrackNumber` within the album
- May have associated JPEG cover art in the same folder

**Required Index Tags:**
```
AlbumName: [album name]
TrackNumber: [integer 1-N, unique within album]
TrackLength: [duration in seconds, format "F2"]
SongPrice: [price as decimal]
Genre: [genre string]
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
var albumTracks = allSongs.Where(s => 
    !string.IsNullOrEmpty(s.Mp3FileName) &&
    !string.IsNullOrEmpty(s.AlbumName) &&
    s.AlbumName.Equals(song.AlbumName, StringComparison.OrdinalIgnoreCase) &&
    s.Id != song.Id).ToList();

if (albumTracks.Any(t => t.TrackNumber == song.TrackNumber))
{
    // Validation error: duplicate track number
}
```

### File Type 3: Standalone Song MP3

**Purpose:** A standalone music track that is NOT part of any album.

**Characteristics:**
- File extension: `.mp3`
- Does NOT have `AlbumName` index tag
- Has associated JPEG cover art with `IsAlbumCover: false`
- Independent pricing and metadata

**Required Index Tags (MP3):**
```
TrackLength: [duration in seconds, format "F2"]
SongPrice: [price as decimal]
Genre: [genre string]
```

**Required Index Tags (Associated JPEG):**
```
IsAlbumCover: false
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

## Index Tag Constants

All index tag names are defined in `MusicSalesApp.Common.Helpers.IndexTagNames`:

| Constant | Description | Used For |
|----------|-------------|----------|
| `AlbumName` | Name of the album | Album covers, Album tracks |
| `IsAlbumCover` | "true" or "false" | All JPEG files |
| `AlbumPrice` | Album purchase price | Album covers |
| `SongPrice` | Individual track price | All MP3 files |
| `Genre` | Music genre | All MP3 files |
| `TrackNumber` | Sequential track position (1-based) | Album tracks only |
| `TrackLength` | Duration in seconds | All MP3 files (auto-extracted) |

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

    // Step 3: Update tags based on file type
    foreach (var fileName in filesToUpdate)
    {
        var isMP3 = fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
        var isAlbumCover = /* check IsAlbumCover tag */;

        if (isAlbumCover)
        {
            // Update album price only
        }
        else if (isMP3)
        {
            // Update genre and song price for ALL MP3s
            // Update track number ONLY if has album name
        }
    }
}
```

## Track Length Extraction

### Implementation Details

Track length is automatically extracted during file upload using FFMpeg:

1. **When:** During upload, after MP3 conversion (if needed)
2. **How:** Uses `MusicService.GetAudioDurationAsync()`
3. **Storage:** Saved as `TrackLength` index tag, formatted as `duration.ToString("F2")` (e.g., "245.67")
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

When writing tests for index tag functionality:

1. **Mock GetAudioDurationAsync** to return predictable durations
2. **Test all three file types** separately
3. **Verify tag presence and values** after upload
4. **Test validation rules** for each file type
5. **Test track number uniqueness** within albums
6. **Test track number bounds** (>= 1, <= total tracks)

## References

- `MusicSalesApp.Common.Helpers.IndexTagNames` - Tag name constants
- `MusicSalesApp.Services.MusicUploadService` - Upload and tag setting logic
- `MusicSalesApp.Services.MusicService` - Track length extraction
- `MusicSalesApp.Components.Pages.AdminSongManagement.razor.cs` - Validation logic
- `MusicSalesApp.Components.Pages.AlbumPlayer.razor.cs` - Track sorting by number
- `MusicSalesApp.Components.Pages.MusicLibrary.razor.cs` - Album grouping logic
