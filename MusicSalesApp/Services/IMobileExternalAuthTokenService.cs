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
    string DisplayName);