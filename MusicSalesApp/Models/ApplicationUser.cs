using Microsoft.AspNetCore.Identity;

namespace MusicSalesApp.Models;

public class ApplicationUser : IdentityUser<int>
{
    // Track when the last verification email was sent
    public DateTime? LastVerificationEmailSent { get; set; }
}
