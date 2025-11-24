# MusicSalesApp

A Blazor Server application with authentication and authorization.

## Setup

### Database Configuration

The application uses SQL Server LocalDB for development. You need to create an `appsettings.Development.json` file with your connection string.

1. Copy `MusicSalesApp/appsettings.Development.json.sample` to `MusicSalesApp/appsettings.Development.json`
2. Adjust the connection string if needed

**Note:** `appsettings.Development.json` is not checked into source control for security reasons.

### Running the Application

```bash
dotnet restore
dotnet build
dotnet run --project MusicSalesApp
```

The database will be created automatically on first run with seed data.

### Default Users

After running the application for the first time, you can log in with:

- **Admin User:**
  - Email: `admin@app.com`
  - Password: `Password_123`
  - Role: Admin
  - Permissions: ManageUsers, ValidatedUser

- **Regular User:**
  - Email: `user@app.com`
  - Password: `Password_123`
  - Role: User
  - Permissions: ValidatedUser

## Testing

```bash
dotnet test
```

## Permissions

The application uses a custom permissions system:

- `ManageUsers` - Assigned to Admin role
- `ValidatedUser` - Assigned to Admin and User roles
- `NonValidatedUser` - Reserved for future use
