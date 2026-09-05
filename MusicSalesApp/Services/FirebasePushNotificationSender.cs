#nullable enable
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

/// <summary>
/// Android delivery, via the Firebase Cloud Messaging HTTP v1 API.
/// </summary>
/// <remarks>
/// <para>
/// Credentials come from a Google service account, resolved the same three ways
/// <see cref="GooglePlayVerificationService"/> resolves its own - a file path, inline JSON, or
/// application default. The OAuth access token is minted and cached by
/// <c>Google.Apis.Auth</c>, which arrives transitively with the Play Billing package, so nothing
/// here hand-rolls RS256 or token expiry.
/// </para>
/// <para>
/// <b>v1 sends one message per request.</b> The legacy multicast endpoint is gone and the batch
/// endpoint is deprecated, so N devices is N requests - bounded here by
/// <see cref="MaxConcurrency"/>. That is fine at this app's volumes and is the reason the
/// dispatcher works through a queue rather than trying to fan out a whole release at once.
/// </para>
/// </remarks>
public sealed class FirebasePushNotificationSender : IPushNotificationSender
{
    private const string MessagingScope = "https://www.googleapis.com/auth/firebase.messaging";
    private const int MaxConcurrency = 8;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FirebasePushNotificationSender> _logger;
    private readonly GoogleCredential? _credential;
    private readonly string _projectId;

    public string Platform => PushPlatforms.Android;

    public bool IsConfigured => _credential is not null && !string.IsNullOrWhiteSpace(_projectId);

    public FirebasePushNotificationSender(
        IConfiguration configuration,
        IHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        ILogger<FirebasePushNotificationSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // The Firebase project id, which is NOT the Android package name and not the Play
        // Console's developer id. Getting this wrong produces a 404 on every send.
        _projectId = configuration["Push:Firebase:ProjectId"] ?? string.Empty;

        var keyPath = ResolvePath(configuration["Push:Firebase:ServiceAccountKeyPath"], environment.ContentRootPath);
        var inlineJson = configuration["Push:Firebase:ServiceAccountKeyJson"];

        try
        {
            // CS0618: FromStream/FromJson are deprecated because they will also accept an
            // external-account (workload identity) credential, which can name an arbitrary token
            // URL. That is not this - the input is a service-account key we place on the server
            // ourselves. Suppressed exactly as GooglePlayVerificationService does for the same
            // call on the same kind of file.
#pragma warning disable CS0618
            if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
            {
                using var stream = new FileStream(keyPath, FileMode.Open, FileAccess.Read);
                _credential = GoogleCredential.FromStream(stream).CreateScoped(MessagingScope);
            }
            else if (!string.IsNullOrWhiteSpace(inlineJson))
            {
                _credential = GoogleCredential.FromJson(inlineJson).CreateScoped(MessagingScope);
            }
#pragma warning restore CS0618
        }
        catch (Exception ex)
        {
            // A warning, not a throw. Push being unconfigured must not stop the site starting -
            // the same posture the Apple revocation key takes.
            _logger.LogWarning(ex, "Firebase push credentials could not be loaded; Android push is disabled.");
        }

        if (!IsConfigured)
        {
            _logger.LogInformation(
                "Android push is not configured (Push:Firebase:ProjectId and a service account key are required).");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PushDeliveryResult>> SendAsync(
        PushMessage message,
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken = default)
    {
        if (tokens is null || tokens.Count == 0)
        {
            return [];
        }

        if (!IsConfigured)
        {
            // Transport failure rather than a permanent one: the notification stays unstamped, so
            // configuring credentials later delivers the backlog instead of silently losing it.
            return tokens
                .Select(token => new PushDeliveryResult(
                    token, PushDeliveryOutcome.TransportFailure, "Firebase push is not configured."))
                .ToList();
        }

        string accessToken;

        try
        {
            accessToken = await ((ITokenAccess)_credential!).GetAccessTokenForRequestAsync(
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            // One failure here fails the whole batch, and correctly so - without a token not one
            // request can be made, and every one of them is retryable.
            _logger.LogWarning(ex, "Could not obtain a Firebase access token; deferring {Count} pushes.", tokens.Count);

            return tokens
                .Select(token => new PushDeliveryResult(
                    token, PushDeliveryOutcome.TransportFailure, "No Firebase access token."))
                .ToList();
        }

        var endpoint = $"https://fcm.googleapis.com/v1/projects/{_projectId}/messages:send";
        var results = new List<PushDeliveryResult>(tokens.Count);
        var gate = new SemaphoreSlim(MaxConcurrency);

        var sends = tokens.Select(async token =>
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                return await SendOneAsync(endpoint, accessToken, message, token, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });

        results.AddRange(await Task.WhenAll(sends));
        return results;
    }

    private async Task<PushDeliveryResult> SendOneAsync(
        string endpoint,
        string accessToken,
        PushMessage message,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(BuildPayload(message, token), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return new PushDeliveryResult(token, PushDeliveryOutcome.Delivered);
            }

            return ClassifyFailure(token, response.StatusCode, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firebase push request threw.");
            return new PushDeliveryResult(token, PushDeliveryOutcome.TransportFailure, ex.Message);
        }
    }

    /// <summary>
    /// Turns an FCM error into one of the three outcomes.
    /// </summary>
    /// <remarks>
    /// The status code alone is not enough: a 400 can mean a malformed payload (our bug, keep the
    /// token) or an invalid registration token (their device, drop the token), and only the
    /// <c>errorCode</c> in the body separates them. Treating every 400 as a dead token would
    /// unregister every device the first time a payload bug shipped.
    /// </remarks>
    private PushDeliveryResult ClassifyFailure(string token, HttpStatusCode status, string body)
    {
        var errorCode = TryReadErrorCode(body);

        // UNREGISTERED: the app was uninstalled or the token was replaced.
        // 404 on the message resource means the same thing for a token send.
        if (string.Equals(errorCode, "UNREGISTERED", StringComparison.OrdinalIgnoreCase)
            || status == HttpStatusCode.NotFound)
        {
            return new PushDeliveryResult(token, PushDeliveryOutcome.TokenRejected, errorCode ?? "UNREGISTERED");
        }

        // INVALID_ARGUMENT covers a bad token AND a bad payload. The body names the offending
        // field, so only blame the token when the token is what it named.
        if (string.Equals(errorCode, "INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase))
        {
            return body.Contains("\"field\": \"message.token\"", StringComparison.OrdinalIgnoreCase)
                   || body.Contains("registration token", StringComparison.OrdinalIgnoreCase)
                ? new PushDeliveryResult(token, PushDeliveryOutcome.TokenRejected, "INVALID_ARGUMENT (token)")
                : new PushDeliveryResult(token, PushDeliveryOutcome.PermanentFailure, $"INVALID_ARGUMENT: {Truncate(body)}");
        }

        // Rate limiting, Firebase outages, and auth problems are all worth another go later.
        if (status == HttpStatusCode.TooManyRequests
            || status == HttpStatusCode.Unauthorized
            || status == HttpStatusCode.Forbidden
            || (int)status >= 500)
        {
            _logger.LogWarning("Firebase push deferred ({Status}): {Body}", status, Truncate(body));
            return new PushDeliveryResult(token, PushDeliveryOutcome.TransportFailure, status.ToString());
        }

        _logger.LogWarning("Firebase push refused ({Status}): {Body}", status, Truncate(body));
        return new PushDeliveryResult(token, PushDeliveryOutcome.PermanentFailure, Truncate(body));
    }

    private static string? TryReadErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("error", out var error))
            {
                return null;
            }

            // The FCM-specific code lives in error.details[], not in error.status - the latter is
            // the generic Google API status and does not distinguish an unregistered token.
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String)
                    {
                        return code.GetString();
                    }
                }
            }

            return error.TryGetProperty("status", out var statusText) ? statusText.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The v1 message body.
    /// </summary>
    /// <remarks>
    /// Carries both a <c>notification</c> block and a <c>data</c> block on purpose. The
    /// notification block is what lets Android display the alert while the app is backgrounded
    /// without the app running any code; the data block is what the app reads on tap to decide
    /// where to navigate. A data-only message would show nothing unless the app happened to be alive.
    /// </remarks>
    private static string BuildPayload(PushMessage message, string token)
    {
        var payload = new Dictionary<string, object>
        {
            ["message"] = new Dictionary<string, object>
            {
                ["token"] = token,
                ["notification"] = new Dictionary<string, object>
                {
                    ["title"] = message.Title,
                    ["body"] = message.Body,
                },
                ["data"] = message.Data is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(message.Data),
                ["android"] = new Dictionary<string, object>
                {
                    ["priority"] = "high",
                    ["notification"] = new Dictionary<string, object>
                    {
                        // Must match the channel the app creates, or Android 8+ drops the
                        // notification silently.
                        ["channel_id"] = PushNotificationChannels.ArtistUpdates,
                    },
                },
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string Truncate(string value) =>
        string.IsNullOrEmpty(value) || value.Length <= 400 ? value : value[..400];

    private static string ResolvePath(string? configuredPath, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        // The configured value uses Windows separators to match the sibling Apple/Play keys on the
        // IIS host; normalising makes the same value resolve when run on macOS or Linux.
        var normalized = configuredPath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        return Path.IsPathRooted(normalized) ? normalized : Path.Combine(contentRootPath, normalized);
    }
}
