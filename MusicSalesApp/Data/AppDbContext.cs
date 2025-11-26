using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MusicSalesApp.Common.Helpers;
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
                Name = Common.Helpers.Roles.Admin,
                NormalizedName = Common.Helpers.Roles.Admin.ToUpper(),
                ConcurrencyStamp = "a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d"
            },
            new IdentityRole<int>
            {
                Id = userRoleId,
                Name = Common.Helpers.Roles.User,
                NormalizedName = Common.Helpers.Roles.User.ToUpper(),
                ConcurrencyStamp = "b2c3d4e5-f6a7-5b6c-9d0e-1f2a3b4c5d6e"
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
            SecurityStamp = "c3d4e5f6-a7b8-6c7d-0e1f-2a3b4c5d6e7f",
            ConcurrencyStamp = "d4e5f6a7-b8c9-7d8e-1f2a-3b4c5d6e7f8a"
        };
        adminUser.PasswordHash = "AQAAAAIAAYagAAAAEIJK1BY5bzMovp+I46WIyfIQZfjRi3dpeb5PN5FeKO9NskZ9RDffZtfBMzhiR/uWsw=="; //hasher.HashPassword(adminUser, "Password_123");

        // Seed regular user
        var regularUser = new ApplicationUser
        {
            Id = 2,
            UserName = "user@app.com",
            NormalizedUserName = "USER@APP.COM",
            Email = "user@app.com",
            NormalizedEmail = "USER@APP.COM",
            EmailConfirmed = true,
            SecurityStamp = "d4e5f6a7-b8c9-7d8e-1f2a-3b4c5d6e7f8a",
            ConcurrencyStamp = "e5f6a7b8-c9d0-8e9f-2a3b-4c5d6e7f8a9b"
        };
        regularUser.PasswordHash = "AQAAAAIAAYagAAAAELfX5sMWJgJQ48czQFh5cJAw8+ZZxj6EMiY1gN/ib1tlG8zPGBefyjVfv+0r/5ER/g=="; //hasher.HashPassword(regularUser, "Password_123");

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

        // Dynamically seed role permission claims
        var adminPermissions = typeof(Permissions)
            .GetFields()
            .Select(f => f.GetValue(null)?.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Where(v => !string.Equals(v, Permissions.NonValidatedUser, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v)
            .ToList();

        var roleClaims = new List<IdentityRoleClaim<int>>();
        var nextId = 1;
        foreach (var perm in adminPermissions)
        {
            roleClaims.Add(new IdentityRoleClaim<int>
            {
                Id = nextId++,
                RoleId = adminRoleId,
                ClaimType = CustomClaimTypes.Permission,
                ClaimValue = perm
            });
        }

        // User role gets ValidatedUser only
        roleClaims.Add(new IdentityRoleClaim<int>
        {
            Id = nextId++,
            RoleId = userRoleId,
            ClaimType = CustomClaimTypes.Permission,
            ClaimValue = Permissions.ValidatedUser
        });

        builder.Entity<IdentityRoleClaim<int>>().HasData(roleClaims);
    }
}
