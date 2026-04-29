using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MusicSalesApp.Services;

public class MobileExternalAuthTokenService : IMobileExternalAuthTokenService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ITimeLimitedDataProtector _loginExchangeProtector;
    private readonly ITimeLimitedDataProtector _pendingRegistrationProtector;

    public MobileExternalAuthTokenService(IDataProtectionProvider dataProtectionProvider)
    {
        _loginExchangeProtector = dataProtectionProvider
            .CreateProtector("MobileExternalAuth", "LoginExchange")
            .ToTimeLimitedDataProtector();
        _pendingRegistrationProtector = dataProtectionProvider
            .CreateProtector("MobileExternalAuth", "PendingRegistration")
            .ToTimeLimitedDataProtector();
    }

    public string ProtectLoginExchange(int userId)
    {
        return Protect(
            _loginExchangeProtector,
            new MobileExternalLoginExchangeTokenPayload(userId),
            TimeSpan.FromMinutes(5));
    }

    public bool TryUnprotectLoginExchange(string token, out MobileExternalLoginExchangeTokenPayload payload)
    {
        return TryUnprotect(_loginExchangeProtector, token, out payload);
    }

    public string ProtectPendingRegistration(MobilePendingExternalRegistrationTokenPayload payload)
    {
        return Protect(_pendingRegistrationProtector, payload, TimeSpan.FromMinutes(10));
    }

    public bool TryUnprotectPendingRegistration(string token, out MobilePendingExternalRegistrationTokenPayload payload)
    {
        return TryUnprotect(_pendingRegistrationProtector, token, out payload);
    }

    private static string Protect<TPayload>(ITimeLimitedDataProtector protector, TPayload payload, TimeSpan lifetime)
    {
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        return protector.Protect(json, lifetime);
    }

    private static bool TryUnprotect<TPayload>(ITimeLimitedDataProtector protector, string token, out TPayload payload)
    {
        payload = default!;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var json = protector.Unprotect(token);
            var result = JsonSerializer.Deserialize<TPayload>(json, SerializerOptions);
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