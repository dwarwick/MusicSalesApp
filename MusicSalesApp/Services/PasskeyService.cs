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
        var authenticatorSelection = new AuthenticatorSelection
        {
            RequireResidentKey = false,
            UserVerification = UserVerificationRequirement.Preferred
        };

        var exts = new AuthenticationExtensionsClientInputs
        {
            Extensions = true,
            UserVerificationMethod = true
        };

        var options = _fido2.RequestNewCredential(
            fido2User,
            existingKeys,
            authenticatorSelection,
            AttestationConveyancePreference.None,
            exts);

        return options;
    }

    public async Task<bool> CompleteRegistrationAsync(int userId, string passkeyName, AuthenticatorAttestationRawResponse attestationResponse)
    {
        try
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null)
            {
                return false;
            }

            // Get the options that were used to start the registration
            // In a real-world scenario, these would be stored in a cache or session
            // For simplicity, we'll recreate them
            var existingKeys = await _context.Passkeys
                .Where(p => p.UserId == userId)
                .Select(p => new PublicKeyCredentialDescriptor(p.CredentialId))
                .ToListAsync();

            var fido2User = new Fido2User
            {
                DisplayName = user.UserName,
                Name = user.UserName,
                Id = BitConverter.GetBytes(userId)
            };

            var authenticatorSelection = new AuthenticatorSelection
            {
                RequireResidentKey = false,
                UserVerification = UserVerificationRequirement.Preferred
            };

            var exts = new AuthenticationExtensionsClientInputs
            {
                Extensions = true,
                UserVerificationMethod = true
            };

            var options = _fido2.RequestNewCredential(
                fido2User,
                existingKeys,
                authenticatorSelection,
                AttestationConveyancePreference.None,
                exts);

            // Verify and make the credential
            var success = await _fido2.MakeNewCredentialAsync(
                attestationResponse,
                options,
                async (args, cancellationToken) =>
                {
                    // Check if credential ID already exists
                    var credIdString = Convert.ToBase64String(args.CredentialId);
                    var exists = await _context.Passkeys
                        .AnyAsync(p => p.CredentialId == args.CredentialId, cancellationToken);
                    return !exists;
                });

            // Store the passkey
            var passkey = new Passkey
            {
                UserId = userId,
                Name = passkeyName,
                CredentialId = success.Result.CredentialId,
                PublicKey = success.Result.PublicKey,
                AttestationObject = attestationResponse.Response.AttestationObject,
                ClientDataJSON = attestationResponse.Response.ClientDataJson,
                SignCount = (int)success.Result.Counter,
                AAGUID = success.Result.Aaguid.ToString(),
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow
            };

            _context.Passkeys.Add(passkey);
            await _context.SaveChangesAsync();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing passkey registration for user {UserId}", userId);
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

        var options = _fido2.GetAssertionOptions(
            existingCredentials,
            UserVerificationRequirement.Preferred,
            exts);

        return options;
    }

    public async Task<ApplicationUser> CompleteLoginAsync(AuthenticatorAssertionRawResponse assertionResponse)
    {
        try
        {
            // Find the passkey by credential ID
            var passkey = await _context.Passkeys
                .Include(p => p.User)
                .FirstOrDefaultAsync(p => p.CredentialId == assertionResponse.Id);

            if (passkey == null)
            {
                throw new InvalidOperationException("Passkey not found");
            }

            // Get assertion options (in production, retrieve from cache/session)
            var existingCredentials = new List<PublicKeyCredentialDescriptor>
            {
                new PublicKeyCredentialDescriptor(passkey.CredentialId)
            };

            var exts = new AuthenticationExtensionsClientInputs
            {
                UserVerificationMethod = true
            };

            var options = _fido2.GetAssertionOptions(
                existingCredentials,
                UserVerificationRequirement.Preferred,
                exts);

            // Verify the assertion
            var res = await _fido2.MakeAssertionAsync(
                assertionResponse,
                options,
                passkey.PublicKey,
                (uint)passkey.SignCount,
                async (args, cancellationToken) =>
                {
                    var storedPasskey = await _context.Passkeys
                        .FirstOrDefaultAsync(p => p.CredentialId == args.CredentialId, cancellationToken);
                    return storedPasskey?.UserId == passkey.UserId;
                });

            // Update sign count and last used
            passkey.SignCount = (int)res.Counter;
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
