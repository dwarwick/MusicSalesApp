using FFMpegCore;
using Fido2NetLib;
using Fido2NetLib.Serialization;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Components; // for NavigationManager when creating HttpClient
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Azure.Storage.Blobs;
using MusicSalesApp;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components;
using MusicSalesApp.Data;
using MusicSalesApp.Middleware;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor;
using Serilog;
using Serilog.Events;
using System.Text;

var logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
Directory.CreateDirectory(logDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDirectory, "app-log-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true)
    .CreateBootstrapLogger();

try
{
    Log.Information("Application starting...");

    var builder = WebApplication.CreateBuilder(args);

    // Apply Serilog file logging configuration
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(logDirectory, "app-log-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            shared: true));

    // Add services to the container.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents(options =>
        {
            options.DetailedErrors = true;
            
            // Configure SignalR circuit options to keep connections alive
            // Disconnect timeout: Time to wait before disconnecting an inactive circuit
            options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
            
            // Increase JSInterop default timeout
            options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(1);
        });

    builder.Services.AddRazorPages();

    builder.Services.Configure<MobileAppInstallOptions>(builder.Configuration.GetSection("MobileAppInstall"));

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

    // Add DbContextFactory for Blazor Server to avoid concurrent DbContext access issues
    builder.Services.AddDbContextFactory<AppDbContext>(options =>
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")), ServiceLifetime.Scoped);

    builder.Services.AddIdentity<ApplicationUser, IdentityRole<int>>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredLength = 8;
        // Token expiration settings
        options.Tokens.EmailConfirmationTokenProvider = TokenOptions.DefaultEmailProvider;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

    // Configure token lifespan to 10 minutes (for both email confirmation and password reset)
    builder.Services.Configure<DataProtectionTokenProviderOptions>(options =>
    {
        options.TokenLifespan = TimeSpan.FromMinutes(10);
    });

    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(builder.Configuration.GetValue<int>("Auth:ExpireMinutes", 300));
        options.SlidingExpiration = true;
    });

    // Add JWT Bearer authentication for mobile clients (MAUI app)
    var jwtKey = builder.Configuration["Jwt:SecretKey"] ?? throw new InvalidOperationException("Jwt:SecretKey must be configured");
    var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "MusicSalesApp";
    var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "MusicSalesApp.Maui";
    var authenticationBuilder = builder.Services.AddAuthentication();
    authenticationBuilder
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            // Map JWT standard claim names (e.g. "role", "sub") to their
            // Microsoft ClaimTypes equivalents so that [Authorize(Roles = "...")]
            // and User.FindFirstValue(ClaimTypes.NameIdentifier) work correctly.
            options.MapInboundClaims = true;
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrWhiteSpace(accessToken)
                        && path.StartsWithSegments("/adminmessagehub"))
                    {
                        context.Token = accessToken;
                    }

                    return Task.CompletedTask;
                }
            };
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtIssuer,
                ValidAudience = jwtAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
            };
        });

    var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
    var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
    if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
    {
        authenticationBuilder.AddGoogle(ExternalLoginProviders.Google, options =>
        {
            options.ClientId = googleClientId;
            options.ClientSecret = googleClientSecret;
            options.SignInScheme = IdentityConstants.ExternalScheme;
            options.SaveTokens = true;
            options.CallbackPath = "/signin-google-mobile";
        });
    }

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            // Insert Fido2's source-generated context FIRST so its custom Base64Url
            // converters are used for AuthenticatorAttestationRawResponse / AssertionRawResponse.
            // .Add() places it after the default reflection resolver which handles all
            // types and never falls through, making the Fido2 context unreachable.
            options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FidoModelSerializerContext.Default);
        });

    // Log model-validation errors that the [ApiController] auto-400 would hide
    builder.Services.Configure<ApiBehaviorOptions>(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ModelValidation");
            var errors = context.ModelState
                .Where(e => e.Value?.Errors.Count > 0)
                .ToDictionary(
                    e => e.Key,
                    e => e.Value!.Errors.Select(err =>
                        err.ErrorMessage + (err.Exception != null ? $" | {err.Exception.Message}" : "")).ToArray());
            logger.LogWarning("Model validation failed for {Path}: {@Errors}",
                context.HttpContext.Request.Path, errors);
            return new BadRequestObjectResult(new ValidationProblemDetails(context.ModelState));
        };
    });

    // Add SignalR for real-time stream count updates
    const int SignalRMaxMessageSizeKB = 32;
    builder.Services.AddSignalR(options =>
    {
        // Configure SignalR to keep connections alive
        // Keep-alive interval - send pings every 15 seconds to keep connection alive
        options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        
        // Client timeout - server will consider client disconnected if no messages received in this time
        // Should be at least 2x the keep-alive interval
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        
        // Handshake timeout - time to wait for handshake to complete
        options.HandshakeTimeout = TimeSpan.FromSeconds(15);
        
        // Max message size
        options.MaximumReceiveMessageSize = SignalRMaxMessageSizeKB * 1024;
        
        // Enable detailed errors in development
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    });

    // Provide HttpClient with base address and cookies configured.
    // For Blazor Server, we need to forward the authentication cookies from the HttpContext.
    builder.Services.AddScoped(sp =>
    {
        var nav = sp.GetRequiredService<NavigationManager>();
        var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
        
        var handler = new HttpClientHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(nav.BaseUri) };
        
        // Forward the authentication cookie from the current request to API calls
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            var cookies = httpContext.Request.Headers["Cookie"].ToString();
            if (!string.IsNullOrEmpty(cookies))
            {
                httpClient.DefaultRequestHeaders.Add("Cookie", cookies);
            }
        }
        
        return httpClient;
    });

    // Register factory for external HTTP calls (PayPal)
    builder.Services.AddHttpClient();

    builder.Services.AddHttpContextAccessor();

    // Register Antiforgery services for DI
    builder.Services.AddAntiforgery();

    // Configure Data Protection to persist keys to Azure Blob Storage
    // This ensures all instances/domains share the same encryption keys for antiforgery tokens and cookies
    var azureConnectionString = builder.Configuration["Azure:StorageAccountConnectionString"];
    var dataProtectionContainerName = builder.Configuration["Azure:DataProtectionContainerName"] ?? "dataprotection-keys";
    
    if (!string.IsNullOrEmpty(azureConnectionString))
    {
        try
        {
            var blobServiceClient = new BlobServiceClient(azureConnectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(dataProtectionContainerName);
            containerClient.CreateIfNotExists();
            
            builder.Services.AddDataProtection()
                .SetApplicationName("MusicSalesApp") // Ensures all deployments use the same application name
                .PersistKeysToAzureBlobStorage(azureConnectionString, dataProtectionContainerName, "keys.xml");
            
            Log.Information("Data Protection configured to use Azure Blob Storage container: {Container}", dataProtectionContainerName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to configure Data Protection with Azure Blob Storage. Using default key storage.");
        }
    }
    else
    {
        Log.Warning("Azure storage connection string not configured. Data Protection will use default key storage.");
    }

    builder.Services.AddAuthorizationCore(options =>
    {
        // Default policy accepts both cookie and JWT bearer authentication
        options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
            IdentityConstants.ApplicationScheme,
            JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build();

        var type = typeof(Permissions);
        var permissionNames = type.GetFields().Select(permission => permission.Name);
        foreach (var name in permissionNames)
        {
            options.AddPolicy(
                name,
                policyBuilder => policyBuilder
                    .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, JwtBearerDefaults.AuthenticationScheme)
                    .RequireAssertion(
                        context => context.User.HasClaim(claim => claim.Type == CustomClaimTypes.Permission && claim.Value == name)));
        }
    });

    builder.Services.AddSingleton(TimeProvider.System);

    builder.Services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();
    builder.Services.AddScoped<ServerAuthenticationStateProvider>();
    builder.Services.AddScoped<IAuthenticationService, AuthenticationService>(); // RoleManager injected automatically
    builder.Services.AddSingleton<IMobileExternalAuthTokenService, MobileExternalAuthTokenService>();
    builder.Services.AddSingleton<IWebGoogleAuthTokenService, WebGoogleAuthTokenService>();
    builder.Services.AddScoped<IMusicService, MusicService>();
    builder.Services.AddScoped<IMusicUploadService, MusicUploadService>();
    builder.Services.AddScoped<IEmailService, EmailService>();
    builder.Services.AddScoped<IPurchaseEmailService, PurchaseEmailService>();
    builder.Services.AddScoped<ISubscriptionConfirmationEmailService, SubscriptionConfirmationEmailService>();
    builder.Services.AddScoped<IAccountEmailService, AccountEmailService>();
    builder.Services.AddScoped<IAccountDeletionService, AccountDeletionService>();
    builder.Services.AddScoped<IUnverifiedUserCleanupService, UnverifiedUserCleanupService>();
    builder.Services.AddScoped<INewSongNotificationService, NewSongNotificationService>();
    builder.Services.AddScoped<ISongMetadataService, SongMetadataService>();
    builder.Services.AddScoped<ISongDisplayOrderService, SongDisplayOrderService>();
    builder.Services.AddScoped<IFeaturedSongRotationService, FeaturedSongRotationService>();
    builder.Services.AddScoped<ISongAdminService, SongAdminService>();
    builder.Services.AddScoped<ISongStatusService, SongStatusService>();
    builder.Services.AddScoped<IGenreService, GenreService>();
    builder.Services.AddScoped<IThemeService, ThemeService>();
    builder.Services.AddScoped<IPlaylistService, PlaylistService>();
    builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
    builder.Services.AddScoped<IPayPalSubscriptionApiService, PayPalSubscriptionApiService>();
    builder.Services.AddScoped<IPayPalSubscriptionManagementService, PayPalSubscriptionManagementService>();
    builder.Services.AddSingleton<IGooglePlayVerificationService, GooglePlayVerificationService>();
    builder.Services.AddSingleton<IAppleAppStoreVerificationService, AppleAppStoreVerificationService>();
    builder.Services.AddScoped<IAppSettingsService, AppSettingsService>();
    builder.Services.AddScoped<IMaintenanceResetService, MaintenanceResetService>();
    builder.Services.AddScoped<IPlaylistCleanupService, PlaylistCleanupService>();
    builder.Services.AddScoped<ITrackLengthRepairService, TrackLengthRepairService>();
    builder.Services.AddScoped<IBackgroundJobService, BackgroundJobService>();
    builder.Services.AddScoped<IPasskeyService, PasskeyService>();
    builder.Services.AddScoped<ISongLikeService, SongLikeService>();
    builder.Services.AddScoped<IOpenGraphService, OpenGraphService>();
    builder.Services.AddScoped<IStreamCountService, StreamCountService>();
    builder.Services.AddScoped<IStreamCountHubClient, StreamCountHubClient>();
    builder.Services.AddScoped<ILikeCountHubClient, LikeCountHubClient>();
    builder.Services.AddScoped<IWebhookStatusHubClient, WebhookStatusHubClient>();
    builder.Services.AddScoped<IMaintenanceHubClient, MaintenanceHubClient>();
    builder.Services.AddScoped<IAdminMessageHubClient, AdminMessageHubClient>();
    builder.Services.AddScoped<IRecommendationService, RecommendationService>();
    builder.Services.AddScoped<IFileMatchingService, FileMatchingService>();
    builder.Services.AddScoped<ISitemapService, SitemapService>();
    builder.Services.AddScoped<ICreatorService, CreatorService>();
    builder.Services.AddScoped<IDashboardService, DashboardService>();
    builder.Services.AddScoped<ICreatorEmailService, CreatorEmailService>();
    builder.Services.AddScoped<IAdminMessageService, AdminMessageService>();
    builder.Services.AddScoped<IAdminNotificationService, AdminNotificationService>();
    builder.Services.AddScoped<IStreamPayoutService, StreamPayoutService>();
    builder.Services.AddScoped<ICreatorPersonaService, CreatorPersonaService>();
    builder.Services.AddScoped<ITipService, TipService>();
    builder.Services.AddScoped<IReportedSongService, ReportedSongService>();
    builder.Services.AddScoped<IContactRequestEmailService, ContactRequestEmailService>();
    builder.Services.AddScoped<IContactRequestRateLimitService, ContactRequestRateLimitService>();
    builder.Services.AddScoped<IContactRequestAdminService, ContactRequestAdminService>();
    builder.Services.AddScoped<IMobileSongMapper, MobileSongMapper>();
    
    // Register TaxBanditsService with HttpClient
    builder.Services.AddHttpClient<ITaxBanditsService, TaxBanditsService>();

    // Configure Fido2 for passkey support
    builder.Services.AddSingleton<IFido2>(sp =>
    {
        var config = new Fido2Configuration
        {
            ServerDomain = builder.Configuration["Fido2:ServerDomain"] ?? "localhost",
            ServerName = "Music Sales App",
            Origins = builder.Configuration.GetSection("Fido2:Origins").Get<HashSet<string>>() 
                      ?? new HashSet<string> { "https://localhost:5001", "http://localhost:5000" },
            TimestampDriftTolerance = builder.Configuration.GetValue<int>("Fido2:TimestampDriftTolerance", 300000)
        };
        return new Fido2(config);
    });

    // Add Hangfire services with SQL Server storage
    try
    {
        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection"), new SqlServerStorageOptions
            {
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                QueuePollInterval = TimeSpan.FromSeconds(15),
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true
            }));
        
        // Add the Hangfire background job processing server as a service
        builder.Services.AddHangfireServer();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Hangfire database unavailable; skipping background job setup");
    }

    builder.Services.Configure<AzureStorageOptions>(builder.Configuration.GetSection("Azure"));
    builder.Services.AddSingleton<IAzureStorageService, AzureStorageService>();

    builder.Services.AddCascadingAuthenticationState();

    builder.Services.AddSyncfusionBlazor();

    var app = builder.Build();

    // Apply pending migrations automatically at startup (skip during design-time tool execution)
    if (!EF.IsDesignTime)
    {
        using (var scope = app.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var migrationLogger = services.GetRequiredService<ILogger<Program>>();
            try
            {
                migrationLogger.LogInformation("Starting database migration...");
                var db = services.GetRequiredService<AppDbContext>();
                
                // Test database connection first
                if (!db.Database.CanConnect())
                {
                    migrationLogger.LogError("Cannot connect to database. Check your connection string.");
                    throw new InvalidOperationException("Database connection failed. Please verify your connection string in appsettings.json or environment variables.");
                }
                
                db.Database.Migrate();
                migrationLogger.LogInformation("Database migration completed successfully.");
            }
            catch (Exception ex)
            {
                migrationLogger.LogError(ex, "CRITICAL ERROR: An error occurred while applying database migrations. Application cannot start.");
                migrationLogger.LogError("Connection String (masked): {ConnectionString}", 
                    builder.Configuration.GetConnectionString("DefaultConnection")?.Substring(0, Math.Min(50, builder.Configuration.GetConnectionString("DefaultConnection")?.Length ?? 0)) + "...");
                throw; // rethrow to fail fast if migrations cannot be applied
            }
        }
    }

    // Configure the HTTP request pipeline.
    // Forward headers from reverse proxies (Cloudflare, IIS) so Request.Scheme
    // reflects the original client request. This ensures OG meta tag URLs
    // (og:image, og:url) use https:// instead of http://.
    // KnownIPNetworks and KnownProxies are cleared so Cloudflare (non-loopback)
    // forwarded headers are honoured in production.
    var forwardedHeadersOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    };
    forwardedHeadersOptions.KnownIPNetworks.Clear();
    forwardedHeadersOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedHeadersOptions);

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }
    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();

    // Prevent CDN/browser caching of HTML responses so the import map (which contains
    // fingerprinted JS URLs) is always fresh after deployments. Fingerprinted static
    // assets served by MapStaticAssets already have long-lived cache headers.
    app.Use(async (context, next) =>
    {
        // Register a callback that fires just before response headers are sent,
        // so we can safely set Cache-Control before the response starts streaming.
        context.Response.OnStarting(() =>
        {
            var contentType = context.Response.ContentType;
            if (contentType != null && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            }
            return Task.CompletedTask;
        });

        await next();
    });

    // required for /.well-known/* to be served from wwwroot
    // Handle /share/{id} before Blazor to avoid SSR lifecycle issues
    app.UseMiddleware<MusicSalesApp.Middleware.ShareRedirectMiddleware>();
    app.UseStaticFiles();

    app.UseAuthentication();
    app.UseAuthorization();

    // Handle antiforgery token decryption errors gracefully (e.g., after key rotation)
    // This must be before UseAntiforgery() to catch and handle the exceptions
    app.UseAntiforgeryTokenErrorHandling();
    app.UseAntiforgery();

    // Configure Hangfire Dashboard with custom authorization
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new HangfireAuthorizationFilter() },
        DashboardTitle = "Music Sales App - Background Jobs",
        DisplayStorageConnectionString = false
    });

    app.MapStaticAssets();
    app.MapControllers();
    app.MapRazorPages(); // Add Razor Pages routing
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    // Map SignalR hubs for real-time updates
    app.MapHub<MusicSalesApp.Hubs.StreamCountHub>("/streamcounthub");
    app.MapHub<MusicSalesApp.Hubs.LikeCountHub>("/likecounthub");
    app.MapHub<MusicSalesApp.Hubs.WebhookStatusHub>("/webhookstatushub");
    app.MapHub<MusicSalesApp.Hubs.MaintenanceHub>("/maintenancehub");
    app.MapHub<MusicSalesApp.Hubs.AdminMessageHub>("/adminmessagehub");

    app.MapGet("/antiforgery/token", (HttpContext context, IAntiforgery antiforgery) =>
    {
        var tokens = antiforgery.GetAndStoreTokens(context);

        // Use the framework's own field name instead of hard-coding
        return Results.Json(new
        {
            token = tokens.RequestToken,
            fieldName = tokens.FormFieldName
        });
    });

    // This is the folder where appsettings.json lives (and where you said ffmpeg.exe is)
    var ffRoot = app.Environment.ContentRootPath;
    var ffmpegLogger = app.Services.GetRequiredService<ILogger<Program>>();

    // Optional: quick diagnostic log to confirm paths on the server
    var ffmpegPath = Path.Combine(ffRoot, "ffmpeg.exe");
    ffmpegLogger.LogInformation("[FFMPEG] ContentRootPath: {ContentRootPath}", ffRoot);
    ffmpegLogger.LogInformation("[FFMPEG] Expecting ffmpeg at: {FFmpegPath}", ffmpegPath);
    ffmpegLogger.LogInformation("[FFMPEG] Exists? {Exists}", File.Exists(ffmpegPath));

    GlobalFFOptions.Configure(options =>
    {
        options.BinaryFolder = ffRoot;                                // look next to appsettings.json
        options.TemporaryFilesFolder = Path.Combine(ffRoot, "fftemp"); // any writable folder
    });

    // Ensure temp directory exists
    Directory.CreateDirectory(Path.Combine(ffRoot, "fftemp"));

    // Initialize recurring Hangfire jobs
    var hangfireLogger = app.Services.GetRequiredService<ILogger<Program>>();
    try
    {
        hangfireLogger.LogInformation("Initializing Hangfire recurring jobs...");
        
        using (var scope = app.Services.CreateScope())
        {
            var backgroundJobService = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            backgroundJobService.InitializeRecurringJobs();
        }
        
        hangfireLogger.LogInformation("Hangfire recurring jobs initialized successfully.");
    }
    catch (Exception ex)
    {
        hangfireLogger.LogWarning(ex, "Failed to initialize Hangfire recurring jobs. Hangfire may not be configured.");
    }

    app.Run();
}
catch (Microsoft.Extensions.Hosting.HostAbortedException)
{
    // This exception is thrown by EF Core design-time tools (e.g., dotnet ef database update)
    // It's expected and indicates the host was intentionally aborted after building the model
    Log.Information("Host was aborted by EF Core design-time tools.");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
