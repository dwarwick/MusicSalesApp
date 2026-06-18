namespace MusicSalesApp.Services;

public interface IWebGoogleAuthTokenService
{
    string ProtectRegistrationIntent(WebGoogleRegistrationIntentTokenPayload payload);
    bool TryUnprotectRegistrationIntent(string token, out WebGoogleRegistrationIntentTokenPayload payload);
}

public sealed record WebGoogleRegistrationIntentTokenPayload(
    bool AcceptTermsOfUse,
    bool AcceptPrivacyPolicy,
    bool AcceptRefundPolicy,
    bool ReceiveNewSongEmails,
    string ReturnUrl);
