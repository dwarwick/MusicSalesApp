using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Models;

namespace MusicSalesApp.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<int>, int>
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Seed roles
        var adminRoleId = 1;
        var userRoleId = 2;

        builder.Entity<IdentityRole<int>>().HasData(
            new IdentityRole<int>
            {
                Id = adminRoleId,
                Name = "Admin",
                NormalizedName = "ADMIN",
                ConcurrencyStamp = Guid.NewGuid().ToString()
            },
            new IdentityRole<int>
            {
                Id = userRoleId,
                Name = "User",
                NormalizedName = "USER",
                ConcurrencyStamp = Guid.NewGuid().ToString()
            }
        );

        // Create password hasher
        var hasher = new PasswordHasher<ApplicationUser>();

        // Seed admin user
        var adminUser = new ApplicationUser
        {
            Id = 1,
            UserName = "admin@app.com",
            NormalizedUserName = "ADMIN@APP.COM",
            Email = "admin@app.com",
            NormalizedEmail = "ADMIN@APP.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        adminUser.PasswordHash = hasher.HashPassword(adminUser, "Password_123");

        // Seed regular user
        var regularUser = new ApplicationUser
        {
            Id = 2,
            UserName = "user@app.com",
            NormalizedUserName = "USER@APP.COM",
            Email = "user@app.com",
            NormalizedEmail = "USER@APP.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        regularUser.PasswordHash = hasher.HashPassword(regularUser, "Password_123");

        builder.Entity<ApplicationUser>().HasData(adminUser, regularUser);

        // Seed user roles
        builder.Entity<IdentityUserRole<int>>().HasData(
            new IdentityUserRole<int>
            {
                RoleId = adminRoleId,
                UserId = 1
            },
            new IdentityUserRole<int>
            {
                RoleId = userRoleId,
                UserId = 2
            }
        );
    }
}
