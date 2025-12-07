using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace MusicSalesApp.Models;

public class ApplicationUser : IdentityUser<int>
{
    // Track when the last verification email was sent
    public DateTime? LastVerificationEmailSent { get; set; }

    // User's preferred theme (Light or Dark)
    [MaxLength(10)]
    public string Theme { get; set; } = "Light";
}
