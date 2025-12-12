# Copilot Instructions

## Workspace Context
The current workspace includes the following specific characteristics:
- Projects targeting: '.NET 10'
Consider these characteristics when generating or modifying code, but only if they are directly relevant to the task.

## Repository Context
Active Git repository:
- Path: C:\Users\bgmsd\source\repos\MusicSalesApp (branch: copilot/add-authorization-service)
- Remote: https://github.com/dwarwick/MusicSalesApp

## UI Framework
- **Always use Syncfusion Blazor components** for all UI elements
- Theme: Light theme using `bootstrap5.css` from Syncfusion.Blazor.Themes
- Replace standard HTML controls with Syncfusion equivalents:
  - Use `SfButton` instead of `<button>` or Bootstrap buttons
  - Use `SfTextBox` instead of `<input type="text">`
  - Use `SfDialog` instead of Bootstrap modals
  - Use `SfGrid` for data tables
  - Use `SfCard` for card layouts
  - Use `SfToast` or `SfMessage` for alerts/notifications
  - Use `SfAppBar` and `SfSidebar` for navigation

## Responsive CSS Breakpoints
- Do NOT place `@media` rules inside component-scoped `.razor.css` files.
- Put breakpoint-specific CSS into the global files under `wwwroot`:
  - `xl_app.css` (base/desktop-wide defaults)
  - `lg_app.css` (`@media (max-width: 1200px)`)
  - `md_app.css` (`@media (max-width: 992px)`)
  - `sm_app.css` (`@media (max-width: 768px)`)
  - `xs_app.css` (`@media (max-width: 576px)`)
- Component `.razor.css` files should only contain base styles; move any responsive rules into the appropriate global file.

## Razor Component Conventions
- Always create code-behind files for Razor components and pages.
- Code-behind file naming: `[ComponentName].razor.cs` (e.g., `Home.razor` and `Home.razor.cs`).
- Code-behind class naming: `[ComponentName]Model` (e.g., class `HomeModel` for `Home.razor`).
- The Razor component must inherit from its code-behind class using `@inherits [ComponentName]Model`.
- **Code-behind classes must inherit from `BlazorBase`.**
- **Never inject services in the component or code-behind; use services from `BlazorBase`.**
- All services are available through properties inherited from `BlazorBase` (e.g., `NavigationManager`, `CartService`, `AuthenticationService`)
- For dialogs, use Syncfusion `SfDialog` component instead of Bootstrap modals.

### Blazor Server Component Lifecycle
**IMPORTANT:** To avoid DbContext threading issues in Blazor Server:
- **Use `OnAfterRenderAsync(bool firstRender)` for data loading**, not `OnInitializedAsync`
- **Guard with `firstRender` check** and a `_hasLoadedData` flag
- **Call `StateHasChanged()` after loading** to update the UI
- **Use `InvokeAsync()` when updating UI from async context**

`OnInitializedAsync()` can be called multiple times during circuit reconnections, causing "A second operation was started on this context instance" errors.

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
    protected bool _loading = true;
    private bool _hasLoadedData = false;
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasLoadedData)
        {
            _hasLoadedData = true;
            try
            {
                // Load data here
                await LoadData();
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
- **Note**: Tests must inherit from `BUnitTestBase` which provides all necessary service mocks
- **Note**: When testing Syncfusion components, use appropriate selectors and assertions for Syncfusion-rendered HTML

#### When to Create Unit Tests
- New API controllers or endpoints
- Service layer logic
- Business logic in helpers or utilities
- Data access layer changes
- AutoMapper configurations

#### Test Pattern for Components
```csharp
[TestFixture]
public class MyComponentTests : BUnitTestBase
{
    [SetUp]
    public override void BaseSetup()
    {
        base.BaseSetup();
        // Additional setup if needed
    }

    [Test]
    public void MyComponent_RendersCorrectly()
    {
        // Arrange & Act
        var cut = TestContext.Render<MyComponent>();
        
        // Assert - Syncfusion components may render with specific CSS classes
        Assert.That(cut.Find(".e-btn"), Is.Not.Null); // SfButton renders with e-btn class
    }
}
```

## Metadata Storage and File Classification

### Overview
The application uses SQL Server database (SongMetadata table) to store all music metadata. Azure Blob Storage is used ONLY for file storage, NOT for metadata.

**IMPORTANT:** DO NOT use Azure Blob Storage index tags for metadata. All metadata operations must use `SongMetadataService`.

### File Types and Classifications

#### 1. Album Cover JPEG (IsAlbumCover = true)
**Identification:**
- JPEG/JPG file
- Database field `IsAlbumCover: true`
- Has `AlbumName` in database
- NO associated MP3 file in the song entry

**Required Database Fields:**
- `IsAlbumCover: true`
- `AlbumName: [album name]`
- `AlbumPrice: [price]`
- `ImageBlobPath: [path to image file]`

**Validation Rules:**
- Album cover image must exist
- Album price must be set
- NO track number required
- NO genre or song price required

#### 2. Album Track MP3 (MP3 with AlbumName)
**Identification:**
- MP3 file
- Has `AlbumName` in database
- Part of an album (shares album name with album cover)

**Required Database Fields:**
- `AlbumName: [album name]`
- `TrackNumber: [1-N]` (must be unique within album, >= 1, <= total tracks in album)
- `TrackLength: [seconds]` (auto-extracted during upload)
- `SongPrice: [price]`
- `Genre: [genre]`
- `Mp3BlobPath: [path to MP3 file]`

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
- Does NOT have `AlbumName` in database
- Has associated JPEG cover image with `IsAlbumCover: false`

**Required Database Fields:**
- `TrackLength: [seconds]` (auto-extracted during upload)
- `SongPrice: [price]`
- `Genre: [genre]`
- `Mp3BlobPath: [path to MP3 file]`

**JPEG Cover Required Database Fields:**
- `IsAlbumCover: false`
- `ImageBlobPath: [path to image file]`

**Validation Rules:**
- Song cover image (JPEG) must exist
- Song price is REQUIRED
- Genre is REQUIRED
- NO track number required
- Track length should be present (read-only, extracted during upload)

### Database Metadata Fields

All metadata fields are stored in the `SongMetadata` SQL table:

```csharp
- AlbumName: Album name for tracks and album covers
- IsAlbumCover: Boolean flag for album cover images vs song cover images
- AlbumPrice: Price for the entire album (set on album cover)
- SongPrice: Price for individual tracks
- Genre: Music genre (set on MP3 files)
- TrackNumber: Track sequence number (1-based, only for album tracks)
- TrackLength: Duration in seconds (auto-extracted, set on all MP3s)
- Mp3BlobPath: Path to MP3 file in blob storage
- ImageBlobPath: Path to image file in blob storage
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

### Metadata Save Logic

When saving changes, save to SQL database (NOT blob index tags):

```csharp
await _songMetadataService.UpsertAsync(new SongMetadata
{
    AlbumName = song.AlbumName,
    IsAlbumCover = isAlbumCover,
    AlbumPrice = song.AlbumPrice,
    SongPrice = song.SongPrice,
    Genre = song.Genre,
    TrackNumber = song.TrackNumber,
    TrackLength = song.TrackLength,
    Mp3BlobPath = song.Mp3FileName,
    ImageBlobPath = song.JpegFileName
});
```

### Track Length Extraction

- Track length is automatically extracted during upload using FFMpeg
- Extracted for ALL MP3 files (both album tracks and standalone songs)
- Stored in SQL `SongMetadata.TrackLength` field as double (e.g., 245.67)
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