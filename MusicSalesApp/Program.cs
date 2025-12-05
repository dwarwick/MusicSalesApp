using FFMpegCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components; // for NavigationManager when creating HttpClient
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Components;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;
using Syncfusion.Blazor;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRazorPages();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

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

// Configure email confirmation token lifespan to 10 minutes
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

builder.Services.AddControllers();

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

builder.Services.AddAuthorizationCore(options =>
{
    var type = typeof(Permissions);
    var permissionNames = type.GetFields().Select(permission => permission.Name);
    foreach (var name in permissionNames)
    {
        options.AddPolicy(
            name,
            policyBuilder => policyBuilder.RequireAssertion(
                context => context.User.HasClaim(claim => claim.Type == CustomClaimTypes.Permission && claim.Value == name)));
    }
});

builder.Services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();
builder.Services.AddScoped<ServerAuthenticationStateProvider>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>(); // RoleManager injected automatically
builder.Services.AddScoped<IMusicService, MusicService>();
builder.Services.AddScoped<IMusicUploadService, MusicUploadService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<ICartService, CartService>();

builder.Services.Configure<AzureStorageOptions>(builder.Configuration.GetSection("Azure"));
builder.Services.AddSingleton<IAzureStorageService, AzureStorageService>();

builder.Services.AddCascadingAuthenticationState();

GlobalFFOptions.Configure(options =>
{
    options.BinaryFolder = Path.Combine(AppContext.BaseDirectory);
});

builder.Services.AddSyncfusionBlazor();

var app = builder.Build();

// Apply pending migrations automatically at startup
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var db = services.GetRequiredService<AppDbContext>();
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while applying database migrations.");
        throw; // rethrow to fail fast if migrations cannot be applied
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorPages(); // Add Razor Pages routing
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/antiforgery/token", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);

    // Use the framework�s own field name instead of hard-coding
    return Results.Json(new
    {
        token = tokens.RequestToken,
        fieldName = tokens.FormFieldName
    });
});


app.Run();
