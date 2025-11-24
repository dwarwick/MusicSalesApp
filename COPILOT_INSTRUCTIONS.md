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