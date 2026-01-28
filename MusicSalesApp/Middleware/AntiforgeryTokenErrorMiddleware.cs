using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace MusicSalesApp.Middleware;

#nullable enable

/// <summary>
/// Middleware that handles antiforgery token decryption failures gracefully.
/// This can happen when:
/// - Data protection keys are rotated during deployment
/// - User has stale cookies from a previous session with different keys
/// - Multi-instance deployments with unsynchronized keys
/// 
/// Instead of showing a 400 Bad Request error, this middleware clears the 
/// problematic antiforgery cookie and redirects the user back to retry with fresh tokens.
/// </summary>
public class AntiforgeryTokenErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AntiforgeryTokenErrorMiddleware> _logger;
    private readonly AntiforgeryOptions _antiforgeryOptions;

    public AntiforgeryTokenErrorMiddleware(
        RequestDelegate next, 
        ILogger<AntiforgeryTokenErrorMiddleware> logger,
        IOptions<AntiforgeryOptions> antiforgeryOptions)
    {
        _next = next;
        _logger = logger;
        _antiforgeryOptions = antiforgeryOptions.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AntiforgeryValidationException ex) when (ex.InnerException is CryptographicException)
        {
            _logger.LogWarning(
                "Antiforgery token decryption failed for {Path}. Clearing cookie and redirecting. User-Agent: {UserAgent}",
                context.Request.Path,
                context.Request.Headers.UserAgent);

            // Clear the antiforgery cookie so a new one will be generated
            ClearAntiforgeryCookie(context);

            // For GET requests, just continue (a new token will be generated)
            // For POST requests, redirect back to the same page as GET to get fresh tokens
            if (HttpMethods.IsPost(context.Request.Method) || 
                HttpMethods.IsPut(context.Request.Method) ||
                HttpMethods.IsDelete(context.Request.Method))
            {
                // Redirect to the same URL as GET request
                // This will cause the page to reload with fresh antiforgery tokens
                var returnUrl = context.Request.Path + context.Request.QueryString;
                context.Response.Redirect(returnUrl);
            }
            else
            {
                // For GET requests, just let it continue with cleared cookie
                // The antiforgery system will generate a new token
                context.Response.StatusCode = StatusCodes.Status200OK;
            }
        }
    }

    private void ClearAntiforgeryCookie(HttpContext context)
    {
        // The default antiforgery cookie name follows the pattern ".AspNetCore.Antiforgery.{id}"
        // We need to find and clear any matching cookies
        var antiforgeryCookiePrefix = ".AspNetCore.Antiforgery.";
        
        // Also check for custom cookie name if configured
        var customCookieName = _antiforgeryOptions.Cookie?.Name;

        foreach (var cookie in context.Request.Cookies)
        {
            if (cookie.Key.StartsWith(antiforgeryCookiePrefix, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(customCookieName) && cookie.Key == customCookieName))
            {
                _logger.LogDebug("Clearing antiforgery cookie: {CookieName}", cookie.Key);
                
                context.Response.Cookies.Delete(cookie.Key, new CookieOptions
                {
                    Path = "/",
                    Secure = context.Request.IsHttps,
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict
                });
            }
        }
    }
}

/// <summary>
/// Extension methods for registering the middleware
/// </summary>
public static class AntiforgeryTokenErrorMiddlewareExtensions
{
    public static IApplicationBuilder UseAntiforgeryTokenErrorHandling(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<AntiforgeryTokenErrorMiddleware>();
    }
}
