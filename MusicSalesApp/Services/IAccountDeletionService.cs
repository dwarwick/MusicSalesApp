using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Models;

namespace MusicSalesApp.Services;

public interface IAccountDeletionService
{
    /// <summary>
    /// Deletes the user account after cleaning up all FK-constrained records
    /// that would block the deletion. Returns the IdentityResult from UserManager.DeleteAsync.
    /// </summary>
    Task<IdentityResult> DeleteAccountAsync(ApplicationUser user);
}
