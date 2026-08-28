namespace MusicSalesApp.Services;

public interface IMobileExternalAuthTokenService
{
    string ProtectLoginExchange(int userId);
    bool TryUnprotectLoginExchange(string token, out MobileExternalLoginExchangeTokenPayload payload);
    string ProtectPendingRegistration(MobilePendingExternalRegistrationTokenPayload payload);
    bool TryUnprotectPendingRegistration(string token, out MobilePendingExternalRegistrationTokenPayload payload);
}

public sealed record MobileExternalLoginExchangeTokenPayload(int UserId);

public sealed record MobilePendingExternalRegistrationTokenPayload(
    string LoginProvider,
    string ProviderKey,
    string Email,
    string DisplayName,
    // Apple only. Carried through registration because the authorization code it came from is
    // single-use and expires in five minutes - well inside the ten-minute life of this token - so
    // it is exchanged the moment the user first authorizes and the result parked here until there
    // is a user row to attach it to. Null for Google.
    string ExternalRefreshToken = null);