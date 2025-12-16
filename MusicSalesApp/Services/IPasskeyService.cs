using Fido2NetLib;
using Fido2NetLib.Objects;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public interface IPasskeyService
{
    Task<CredentialCreateOptions> BeginRegistrationAsync(int userId, string passkeyName);
    Task<bool> CompleteRegistrationAsync(int userId, string passkeyName, AuthenticatorAttestationRawResponse attestationResponse, CredentialCreateOptions originalOptions);
    
    Task<AssertionOptions> BeginLoginAsync(string username);
    Task<ApplicationUser> CompleteLoginAsync(AuthenticatorAssertionRawResponse assertionResponse, AssertionOptions originalOptions);
    
    Task<List<Passkey>> GetUserPasskeysAsync(int userId);
    Task<bool> DeletePasskeyAsync(int userId, int passkeyId);
    Task<bool> RenamePasskeyAsync(int userId, int passkeyId, string newName);
}
