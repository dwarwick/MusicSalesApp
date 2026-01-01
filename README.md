# MusicSalesApp

A Blazor Server application with authentication and authorization.

## Setup

### Database Configuration

The application uses SQL Server LocalDB for development. You need to create an `appsettings.Development.json` file with your connection string.

1. Copy `MusicSalesApp/appsettings.Development.json.sample` to `MusicSalesApp/appsettings.Development.json`
2. Adjust the connection string if needed

**Note:** `appsettings.Development.json` is not checked into source control for security reasons.

### SignalR Connection Management

The application uses SignalR for real-time features and implements automatic connection management to provide a seamless user experience:

#### Keep-Alive Configuration
- **Keep-alive interval**: 15 seconds - Server sends ping to keep connection active
- **Client timeout**: 60 seconds - Connection considered dead if no response
- **Circuit retention**: 3 minutes - Server retains inactive circuit state
- **Handshake timeout**: 15 seconds - Time allowed for initial connection

#### Automatic Reconnection
- **Silent reconnection**: No modal dialogs shown to users
- **Exponential backoff**: Retries at 2s, 5s, and 10s intervals
- **Auto-reload**: Page automatically reloads after 3 failed attempts
- **SEO friendly**: Reconnection UI hidden from search engines

This configuration ensures:
- Connections stay alive during normal usage
- Minimal disruption during temporary network issues
- Automatic recovery without user intervention
- No reconnection text appearing in search engine results

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

## Features

### Music Library and Playback

- Browse music library with albums and individual songs
- 60-second preview for non-subscribers and non-owners
- Full playback for purchased music and active subscribers
- Create and manage playlists

### Shopping and Purchases

- Add songs and albums to cart
- Purchase music through **PayPal Expanded Checkout** with:
  - Multiple payment options (PayPal, Venmo, Pay Later)
  - **3D Secure authentication** for enhanced security
  - Enhanced error handling and user experience
  - Compliance with PayPal merchant requirements
- Owned music accessible indefinitely

For detailed information about the PayPal Expanded Checkout integration, see [PAYPAL_EXPANDED_CHECKOUT.md](PAYPAL_EXPANDED_CHECKOUT.md).

### Monthly Subscription

The application supports a monthly subscription model that provides unlimited streaming access to all music.

#### Subscription Features

- **Monthly Price**: Configurable in `appsettings.json` (default: $3.99/month)
- **Unlimited Access**: Subscribers can stream all music in full without purchasing
- **Flexible Cancellation**: Cancel anytime, access continues until end of paid period
- **PayPal Integration**: Secure subscription management through PayPal

#### Configuration

Add the following to your `appsettings.json`:

```json
"PayPal": {
  "ClientId": "YOUR_PAYPAL_CLIENT_ID",
  "Secret": "YOUR_PAYPAL_SECRET",
  "ApiBaseUrl": "https://api-m.sandbox.paypal.com/",
  "SubscriptionPrice": "3.99",
  "ReturnBaseUrl": "https://localhost:7144"
}
```

- Use sandbox URL for testing: `https://api-m.sandbox.paypal.com/`
- Use production URL for live: `https://api-m.paypal.com/`
- Update `ReturnBaseUrl` to match your application's base URL

#### Using Subscriptions

1. Navigate to "Manage Subscription" from the menu (requires login)
2. Review subscription terms
3. Check "I agree to the subscription terms and conditions"
4. Click "Sign Up for Monthly Subscription"
5. Complete payment through PayPal
6. Enjoy unlimited streaming!

To cancel:
1. Navigate to "Manage Subscription"
2. Click "Cancel Subscription"
3. Confirm cancellation
4. Access continues until the end of your current billing period
