using Microsoft.AspNetCore.Identity;

namespace MusicSalesApp.Extensions;

public static class UserLookupExtensions
{
    /// <summary>
    /// Finds a user by email or username.
    /// Tries email first, then username if email lookup returns null.
    /// </summary>
    /// <typeparam name="TUser">The user type</typeparam>
    /// <param name="userManager">The UserManager instance</param>
    /// <param name="emailOrUsername">Email or username to search for</param>
    /// <returns>The user if found, null otherwise</returns>
    public static async Task<TUser> FindByEmailOrUsernameAsync<TUser>(
        this UserManager<TUser> userManager, 
        string emailOrUsername) where TUser : class
    {
        if (string.IsNullOrEmpty(emailOrUsername))
        {
            return null;
        }

        // Try to find user by email first, then by username
        return await userManager.FindByEmailAsync(emailOrUsername) 
               ?? await userManager.FindByNameAsync(emailOrUsername);
    }
}
