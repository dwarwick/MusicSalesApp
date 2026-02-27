using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Data;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public class PasskeyService : IPasskeyService
{
    private readonly IFido2 _fido2;
    private readonly AppDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<PasskeyService> _logger;

    public PasskeyService(
        IFido2 fido2,
        AppDbContext context,
        UserManager<ApplicationUser> userManager,
        ILogger<PasskeyService> logger)
    {
        _fido2 = fido2;
        _context = context;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<CredentialCreateOptions> BeginRegistrationAsync(int userId, string passkeyName)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        // Get existing credentials for this user
        var existingKeys = await _context.Passkeys
            .Where(p => p.UserId == userId)
            .Select(p => new PublicKeyCredentialDescriptor(p.CredentialId))
            .ToListAsync();

        // Create user entity for FIDO2
        var fido2User = new Fido2User
        {
            DisplayName = user.UserName,
            Name = user.UserName,
            Id = BitConverter.GetBytes(userId) // Convert userId to byte array
        };

        // Options for authenticator selection
        // Note: Not setting AuthenticatorAttachment allows both platform (Windows Hello, Touch ID)
        // and cross-platform (security keys, phone passkeys, cloud password managers) authenticators
        var authenticatorSelection = new AuthenticatorSelection
        {
            // ResidentKey = Discouraged allows both discoverable and non-discoverable credentials
            // This enables cloud password managers like Google Password Manager while still supporting
            // traditional authenticators
            ResidentKey = ResidentKeyRequirement.Discouraged,
            UserVerification = UserVerificationRequirement.Preferred
            // AuthenticatorAttachment is intentionally not set to allow all authenticator types
        };

        var exts = new AuthenticationExtensionsClientInputs
        {
            Extensions = true,
            UserVerificationMethod = true
        };

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fido2User,
            ExcludeCredentials = existingKeys,
            AuthenticatorSelection = authenticatorSelection,
            AttestationPreference = AttestationConveyancePreference.None,
            Extensions = exts
        });

        // Set a longer timeout (3 minutes) to accommodate cloud password managers
        // which may need extra time to sync/communicate with their servers
        options.Timeout = 180000; // 180 seconds in milliseconds

        return options;
    }

    public async Task<bool> CompleteRegistrationAsync(int userId, string passkeyName, AuthenticatorAttestationRawResponse attestationResponse, CredentialCreateOptions originalOptions)
    {
        try
        {
            _logger.LogInformation("CompleteRegistrationAsync started for user {UserId}, passkeyName: {PasskeyName}", userId, passkeyName);

            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null)
            {
                _logger.LogWarning("CompleteRegistrationAsync: User {UserId} not found", userId);
                return false;
            }

            _logger.LogInformation("AttestationResponse — Id: {Id}, RawId len: {RawIdLen}, Response null: {RespNull}",
                attestationResponse?.Id, attestationResponse?.RawId?.Length, attestationResponse?.Response == null);

            // Use the original options that were created during BeginRegistrationAsync
            // This is critical - the challenge must match!
            
            // Verify and make the credential
            _logger.LogInformation("Calling MakeNewCredentialAsync...");
            var success = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = originalOptions,
                IsCredentialIdUniqueToUserCallback = async (args, cancellationToken) =>
                {
                    // Check if credential ID already exists
                    var exists = await _context.Passkeys
                        .AnyAsync(p => p.CredentialId == args.CredentialId, cancellationToken);
                    return !exists;
                }
            });

            _logger.LogInformation("MakeNewCredentialAsync succeeded. Id len: {IdLen}, PublicKey len: {PkLen}, SignCount: {SignCount}",
                success.Id?.Length, success.PublicKey?.Length, success.SignCount);

            // Store the passkey
            var passkey = new Passkey
            {
                UserId = userId,
                Name = passkeyName,
                CredentialId = success.Id,
                PublicKey = success.PublicKey,
                AttestationObject = success.AttestationObject,
                ClientDataJSON = success.AttestationClientDataJson,
                SignCount = (int)success.SignCount,
                AAGUID = Guid.Empty.ToString(),
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow
            };

            _context.Passkeys.Add(passkey);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Passkey saved successfully for user {UserId}, passkeyId: {PasskeyId}", userId, passkey.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing passkey registration for user {UserId}. Exception type: {ExType}", userId, ex.GetType().FullName);
            return false;
        }
    }

    public async Task<AssertionOptions> BeginLoginAsync(string username)
    {
        var user = await _userManager.FindByNameAsync(username) 
                   ?? await _userManager.FindByEmailAsync(username);
        
        if (user == null)
        {
            // Return empty options to prevent user enumeration
            return new AssertionOptions
            {
                Challenge = new byte[32],
                RpId = string.Empty,
                AllowCredentials = new List<PublicKeyCredentialDescriptor>()
            };
        }

        // Get all passkeys for this user
        var passkeys = await _context.Passkeys
            .Where(p => p.UserId == user.Id)
            .ToListAsync();

        if (!passkeys.Any())
        {
            throw new InvalidOperationException("No passkeys found for this user");
        }

        var existingCredentials = passkeys
            .Select(p => new PublicKeyCredentialDescriptor(p.CredentialId))
            .ToList();

        var exts = new AuthenticationExtensionsClientInputs
        {
            UserVerificationMethod = true
        };

        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = existingCredentials,
            UserVerification = UserVerificationRequirement.Preferred,
            Extensions = exts
        });

        return options;
    }

    public async Task<ApplicationUser> CompleteLoginAsync(AuthenticatorAssertionRawResponse assertionResponse, AssertionOptions originalOptions)
    {
        try
        {
            // Find the passkey by credential ID (RawId is byte[], matching DB column type)
            var rawId = assertionResponse.RawId;
            var passkey = await _context.Passkeys
                .Include(p => p.User)
                .FirstOrDefaultAsync(p => p.CredentialId == rawId);

            if (passkey == null)
            {
                throw new InvalidOperationException("Passkey not found");
            }

            // Use the original options that were created during BeginLoginAsync
            // This is critical - the challenge must match!

            // Verify the assertion
            var res = await _fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = originalOptions,
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = (uint)passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = async (args, cancellationToken) =>
                {
                    var storedPasskey = await _context.Passkeys
                        .FirstOrDefaultAsync(p => p.CredentialId == args.CredentialId, cancellationToken);
                    return storedPasskey?.UserId == passkey.UserId;
                }
            });

            // Update sign count and last used
            passkey.SignCount = (int)res.SignCount;
            passkey.LastUsedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return passkey.User;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing passkey login");
            throw;
        }
    }

    public async Task<List<Passkey>> GetUserPasskeysAsync(int userId)
    {
        return await _context.Passkeys
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
    }

    public async Task<bool> DeletePasskeyAsync(int userId, int passkeyId)
    {
        var passkey = await _context.Passkeys
            .FirstOrDefaultAsync(p => p.Id == passkeyId && p.UserId == userId);

        if (passkey == null)
        {
            return false;
        }

        _context.Passkeys.Remove(passkey);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RenamePasskeyAsync(int userId, int passkeyId, string newName)
    {
        var passkey = await _context.Passkeys
            .FirstOrDefaultAsync(p => p.Id == passkeyId && p.UserId == userId);

        if (passkey == null)
        {
            return false;
        }

        passkey.Name = newName;
        await _context.SaveChangesAsync();
        return true;
    }
}
