using Microsoft.AspNetCore.Identity;
using MusicSalesApp.Models;

namespace MusicSalesApp.Helpers;

public static class UserTimeZonePersistenceHelper
{
    public static async Task PersistIfProvidedAsync(
        UserManager<ApplicationUser> userManager,
        ApplicationUser user,
        string timeZoneId,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || string.Equals(user.TimeZoneId, timeZoneId, StringComparison.Ordinal))
        {
            return;
        }

        user.TimeZoneId = timeZoneId;

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(error => error.Description));
            logger.LogWarning("Failed to persist timezone {TimeZoneId} for user {UserId}: {Errors}", timeZoneId, user.Id, errors);
        }
    }
}