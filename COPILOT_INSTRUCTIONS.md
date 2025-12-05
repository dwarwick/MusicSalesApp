# Copilot Instructions

## Workspace Context
The current workspace includes the following specific characteristics:
- Projects targeting: '.NET 10'
Consider these characteristics when generating or modifying code, but only if they are directly relevant to the task.

## Repository Context
Active Git repository:
- Path: C:\Users\bgmsd\source\repos\MusicSalesApp (branch: copilot/add-authorization-service)
- Remote: https://github.com/dwarwick/MusicSalesApp

## Razor Component Conventions
- Always create code-behind files for Razor components and pages.
- Code-behind file naming: `[ComponentName].razor.cs` (e.g., `Home.razor` and `Home.razor.cs`).
- Code-behind class naming: `[ComponentName]Model` (e.g., class `HomeModel` for `Home.razor`).
- The Razor component must inherit from its code-behind class using `@inherits [ComponentName]Model`.
- Code-behind classes must inherit from `BlazorBase`.
- Never inject services in the component or code-behind; use services from `BlazorBase`.
- For dialogs, use examples from `Pages/Admin/Dialogs` and `Pages/Common`.

### Example
```razor
@* Home.razor *@
@page "/"
@inherits HomeModel

<h1>Hello, world!</h1>
```

```csharp
// Home.razor.cs
using MusicSalesApp.Components.Base;

namespace MusicSalesApp.Components.Pages;

public partial class HomeModel : BlazorBase
{
    // Component logic here
}
```

## Guidance
Follow the above conventions strictly when adding or modifying Razor components. Ensure new components include their corresponding code-behind class with the proper inheritance chain and avoid direct service injection.

### Code Quality Standards
- **Nullable Reference Types**: Disabled in all projects; do not enable or use nullable annotations
- **Warnings**: Treat as errors and fix before committing
- Check for new warnings after each build
- All tests must pass before creating a PR

## Testing

### Test Frameworks
- **NUnit**: Server-side unit tests and Playwright E2E tests
- **bUnit**: Blazor component tests
- **Playwright**: End-to-end browser tests

### Testing Requirements
**IMPORTANT**: When making code changes, always create or update appropriate tests:

#### When to Create bUnit Tests
- New Blazor components or pages
- Updates to existing component behavior
- Demo functionality changes
- UI interaction logic
- Component state management

#### When to Create Unit Tests
- New API controllers or endpoints
- Service layer logic
- Business logic in helpers or utilities
- Data access layer changes
- AutoMapper configurations

## Index Tags and File Classification

### Overview
The application uses Azure Blob Storage index tags to classify and manage music files. Understanding the file classification is critical for proper validation and tag management.

### File Types and Classifications

#### 1. Album Cover JPEG (IsAlbumCover = true)
**Identification:**
- JPEG/JPG file
- Has `IsAlbumCover: true` index tag
- Has `AlbumName` index tag
- NO associated MP3 file in the song entry

**Required Index Tags:**
- `IsAlbumCover: true`
- `AlbumName: [album name]`
- `AlbumPrice: [price]`

**Validation Rules:**
- Album cover image must exist
- Album price must be set
- NO track number required
- NO genre or song price required

#### 2. Album Track MP3 (MP3 with AlbumName)
**Identification:**
- MP3 file
- Has `AlbumName` index tag
- Part of an album (shares album name with album cover)

**Required Index Tags:**
- `AlbumName: [album name]`
- `TrackNumber: [1-N]` (must be unique within album, >= 1, <= total tracks in album)
- `TrackLength: [seconds]` (auto-extracted during upload)
- `SongPrice: [price]`
- `Genre: [genre]`

**Validation Rules:**
- Track number is REQUIRED
- Track number must be >= 1
- Track number must be <= total tracks in album
- Track number must be UNIQUE within the album
- Song price is REQUIRED
- Genre is REQUIRED
- Track length should be present (read-only, extracted during upload)

#### 3. Standalone Song MP3 (MP3 without AlbumName)
**Identification:**
- MP3 file
- Does NOT have `AlbumName` index tag
- Has associated JPEG cover image with `IsAlbumCover: false`

**Required Index Tags:**
- `TrackLength: [seconds]` (auto-extracted during upload)
- `SongPrice: [price]`
- `Genre: [genre]`

**JPEG Cover Required Index Tags:**
- `IsAlbumCover: false`

**Validation Rules:**
- Song cover image (JPEG) must exist
- Song price is REQUIRED
- Genre is REQUIRED
- NO track number required
- Track length should be present (read-only, extracted during upload)

### Index Tag Reference

All index tag names are defined in `MusicSalesApp.Common.Helpers.IndexTagNames`:

```csharp
- AlbumName: Album name for tracks and album covers
- IsAlbumCover: "true" for album cover JPEGs, "false" for song cover JPEGs
- AlbumPrice: Price for the entire album (set on album cover)
- SongPrice: Price for individual tracks
- Genre: Music genre (set on MP3 files)
- TrackNumber: Track sequence number (1-based, only for album tracks)
- TrackLength: Duration in seconds (auto-extracted, set on all MP3s)
```

### Validation Implementation

When implementing validation in AdminSongManagement:

```csharp
// Determine file type
var hasMP3 = !string.IsNullOrEmpty(song.Mp3FileName);
var isAlbumCoverEntry = song.IsAlbum && !hasMP3;
var isAlbumTrack = hasMP3 && !string.IsNullOrEmpty(song.AlbumName);
var isStandaloneSong = hasMP3 && string.IsNullOrEmpty(song.AlbumName);

// Apply appropriate validation based on type
if (isAlbumCoverEntry) {
    // Validate: album cover image, album price
}
else if (isAlbumTrack) {
    // Validate: track number (required, unique, bounds), song price, genre
}
else if (isStandaloneSong) {
    // Validate: song cover image, song price, genre
}
```

### Tag Update Logic

When saving changes, apply tags based on file type:

```csharp
if (isAlbumCover) {
    // Update: AlbumPrice
}
else if (isMP3) {
    // Update: Genre, SongPrice for all MP3s
    // Additionally update: TrackNumber (only if has AlbumName)
}
```

### Track Length Extraction

- Track length is automatically extracted during upload using FFMpeg
- Extracted for ALL MP3 files (both album tracks and standalone songs)
- Stored as `TrackLength` index tag in seconds (formatted as "F2")
- Read-only in UI - cannot be edited manually
- Falls back to FFProbe if available, but primarily uses FFMpeg with null output

## Build and Deployment

### Build Commands
bash
# Restore packages
dotnet restore

# Build solution
dotnet build

# Run the application
dotnet run --project JwtIdentity


### Database
- SQL Server LocalDB database managed by EF Core migrations
- Connection string is in appsettings.Development.json: `Server=(localdb)\\mssqllocaldb;Database=MusicAppDb;Trusted_Connection=True;MultipleActiveResultSets=true`
- Database created automatically on first run with seed data