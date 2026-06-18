using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MusicSalesApp.Services;

public class WebGoogleAuthTokenService : IWebGoogleAuthTokenService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ITimeLimitedDataProtector _registrationIntentProtector;

    public WebGoogleAuthTokenService(IDataProtectionProvider dataProtectionProvider)
    {
        _registrationIntentProtector = dataProtectionProvider
            .CreateProtector("WebGoogleAuth", "RegistrationIntent")
            .ToTimeLimitedDataProtector();
    }

    public string ProtectRegistrationIntent(WebGoogleRegistrationIntentTokenPayload payload)
    {
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        return _registrationIntentProtector.Protect(json, TimeSpan.FromMinutes(10));
    }

    public bool TryUnprotectRegistrationIntent(string token, out WebGoogleRegistrationIntentTokenPayload payload)
    {
        payload = default!;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var json = _registrationIntentProtector.Unprotect(token);
            var result = JsonSerializer.Deserialize<WebGoogleRegistrationIntentTokenPayload>(json, SerializerOptions);
            if (result == null)
            {
                return false;
            }

            payload = result;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return false;
        }
    }
}
