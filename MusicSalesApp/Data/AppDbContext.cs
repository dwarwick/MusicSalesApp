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

    public DbSet<SongMetadata> SongMetadata { get; set; }
    public DbSet<Playlist> Playlists { get; set; }
    public DbSet<UserPlaylist> UserPlaylists { get; set; }
    public DbSet<Subscription> Subscriptions { get; set; }
    public DbSet<Passkey> Passkeys { get; set; }
    public DbSet<SongLike> SongLikes { get; set; }
    public DbSet<RecommendedPlaylist> RecommendedPlaylists { get; set; }
    public DbSet<AppSettings> AppSettings { get; set; }
    public DbSet<Creator> Creators { get; set; }
    public DbSet<StreamPayout> StreamPayouts { get; set; }
    public DbSet<W9Request> W9Requests { get; set; }
    public DbSet<SongStatusHistory> SongStatusHistories { get; set; }
    public DbSet<SongStream> SongStreams { get; set; }
    public DbSet<Genre> Genres { get; set; }
    public DbSet<UserHistory> UserHistories { get; set; }
    public DbSet<Tip> Tips { get; set; }
    public DbSet<BlockedTipAttempt> BlockedTipAttempts { get; set; }
    public DbSet<ChargebackLog> ChargebackLogs { get; set; }
    public DbSet<CreatorPersona> CreatorPersonas { get; set; }
    public DbSet<MobileVerificationCode> MobileVerificationCodes { get; set; }
    public DbSet<ReportedSong> ReportedSongs { get; set; }
    public DbSet<AdminMessage> AdminMessages { get; set; }
    public DbSet<AdminMessageRole> AdminMessageRoles { get; set; }
    public DbSet<AdminMessageRecipient> AdminMessageRecipients { get; set; }
    public DbSet<ContactRequestSubmission> ContactRequestSubmissions { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Seed roles
        var adminRoleId = 1;
        var userRoleId = 2;
        var creatorRoleId = 4;

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
            },
            new IdentityRole<int>
            {
                Id = creatorRoleId,
                Name = Common.Helpers.Roles.Creator,
                NormalizedName = Common.Helpers.Roles.Creator.ToUpper(),
                ConcurrencyStamp = "c3d4e5f6-a7b8-6c7d-0e1f-2a3b4c5d6e7f"
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
            .Where(v => 
            !string.Equals(v, Permissions.NonValidatedUser, StringComparison.OrdinalIgnoreCase) && 
            !string.Equals(v, Permissions.ManageOwnSongs, StringComparison.OrdinalIgnoreCase))
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

        // Creator role gets ValidatedUser, UploadFiles, and ManageOwnSongs permissions
        roleClaims.Add(new IdentityRoleClaim<int>
        {
            Id = nextId++,
            RoleId = creatorRoleId,
            ClaimType = CustomClaimTypes.Permission,
            ClaimValue = Permissions.ValidatedUser
        });
        roleClaims.Add(new IdentityRoleClaim<int>
        {
            Id = nextId++,
            RoleId = creatorRoleId,
            ClaimType = CustomClaimTypes.Permission,
            ClaimValue = Permissions.UploadFiles
        });
        roleClaims.Add(new IdentityRoleClaim<int>
        {
            Id = nextId++,
            RoleId = creatorRoleId,
            ClaimType = CustomClaimTypes.Permission,
            ClaimValue = Permissions.ManageOwnSongs
        });

        builder.Entity<IdentityRoleClaim<int>>().HasData(roleClaims);

        // Configure UserPlaylist relationships to avoid multiple cascade paths
        // When a User is deleted, their Playlists cascade delete, which then cascade delete UserPlaylists
        // So we can use NoAction for the direct User FK to avoid circular cascade paths
        builder.Entity<UserPlaylist>()
            .HasOne(up => up.User)
            .WithMany()
            .HasForeignKey(up => up.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        // When a Playlist is deleted, remove all songs from it
        builder.Entity<UserPlaylist>()
            .HasOne(up => up.Playlist)
            .WithMany(p => p.UserPlaylists)
            .HasForeignKey(up => up.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);

        // When SongMetadata is deleted, remove it from all playlists
        builder.Entity<UserPlaylist>()
            .HasOne(up => up.SongMetadata)
            .WithMany()
            .HasForeignKey(up => up.SongMetadataId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure Passkey entity
        builder.Entity<Passkey>()
            .HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<AdminMessage>()
            .HasOne(am => am.CreatedByUser)
            .WithMany()
            .HasForeignKey(am => am.CreatedByUserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Entity<AdminMessage>()
            .HasOne(am => am.CanceledByUser)
            .WithMany()
            .HasForeignKey(am => am.CanceledByUserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Entity<AdminMessage>()
            .HasIndex(am => am.CreatedAtUtc);

        builder.Entity<AdminMessage>()
            .HasIndex(am => am.CanceledAtUtc);

        builder.Entity<AdminMessageRole>()
            .HasOne(amr => amr.AdminMessage)
            .WithMany(am => am.Roles)
            .HasForeignKey(amr => amr.AdminMessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<AdminMessageRole>()
            .HasIndex(amr => new { amr.AdminMessageId, amr.RoleName })
            .IsUnique();

        builder.Entity<AdminMessageRecipient>()
            .HasOne(amr => amr.AdminMessage)
            .WithMany(am => am.Recipients)
            .HasForeignKey(amr => amr.AdminMessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<AdminMessageRecipient>()
            .HasOne(amr => amr.User)
            .WithMany()
            .HasForeignKey(amr => amr.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<AdminMessageRecipient>()
            .HasIndex(amr => new { amr.AdminMessageId, amr.UserId })
            .IsUnique();

        builder.Entity<AdminMessageRecipient>()
            .HasIndex(amr => amr.UserId);

        builder.Entity<AdminMessageRecipient>()
            .HasIndex(amr => amr.EmailSentAtUtc);

        builder.Entity<AdminMessageRecipient>()
            .HasIndex(amr => amr.AcknowledgedAtUtc);

        builder.Entity<AdminMessageRecipient>()
            .HasIndex(amr => amr.CanceledAtUtc);

        builder.Entity<Passkey>()
            .HasIndex(p => p.CredentialId)
            .IsUnique();

        // Configure SongLike entity
        builder.Entity<SongLike>()
            .HasOne(sl => sl.User)
            .WithMany()
            .HasForeignKey(sl => sl.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<SongLike>()
            .HasOne(sl => sl.SongMetadata)
            .WithMany()
            .HasForeignKey(sl => sl.SongMetadataId)
            .OnDelete(DeleteBehavior.Cascade);

        // Composite unique index to ensure one like/dislike per user per song
        builder.Entity<SongLike>()
            .HasIndex(sl => new { sl.UserId, sl.SongMetadataId })
            .IsUnique();

        // Index for efficient querying of likes/dislikes by song
        builder.Entity<SongLike>()
            .HasIndex(sl => sl.SongMetadataId);

        // Index for efficient querying of user's likes/dislikes
        builder.Entity<SongLike>()
            .HasIndex(sl => sl.UserId);

        // Configure RecommendedPlaylist entity
        builder.Entity<RecommendedPlaylist>()
            .HasOne(rp => rp.User)
            .WithMany()
            .HasForeignKey(rp => rp.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<RecommendedPlaylist>()
            .HasOne(rp => rp.SongMetadata)
            .WithMany()
            .HasForeignKey(rp => rp.SongMetadataId)
            .OnDelete(DeleteBehavior.Cascade);

        // Index for efficient querying by user
        builder.Entity<RecommendedPlaylist>()
            .HasIndex(rp => rp.UserId);

        // Composite index for efficient querying by user and date
        builder.Entity<RecommendedPlaylist>()
            .HasIndex(rp => new { rp.UserId, rp.GeneratedAt });

        // Configure AppSettings entity
        builder.Entity<AppSettings>()
            .HasIndex(s => s.Key)
            .IsUnique();

        // Seed default subscription price setting
        builder.Entity<AppSettings>().HasData(
            new AppSettings
            {
                Id = 1,
                Key = "SubscriptionPrice",
                Value = "3.99",
                Description = "Monthly subscription price in USD",
                UpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new AppSettings
            {
                Id = 2,
                Key = "StreamQualifyingSeconds",
                Value = "30",
                Description = "Number of continuous seconds of playback that qualifies as a stream",
                UpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        );

        // Configure Creator entity
        builder.Entity<Creator>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique index on UserId - each user can only be one creator
        builder.Entity<Creator>()
            .HasIndex(s => s.UserId)
            .IsUnique();

        // Configure SongMetadata-Creator relationship
        builder.Entity<SongMetadata>()
            .HasOne(sm => sm.Creator)
            .WithMany()
            .HasForeignKey(sm => sm.CreatorId)
            .OnDelete(DeleteBehavior.SetNull);

        // Configure W9Request entity
        builder.Entity<W9Request>()
            .HasOne(w => w.User)
            .WithMany()
            .HasForeignKey(w => w.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Index on UserId for efficient lookups
        builder.Entity<W9Request>()
            .HasIndex(w => w.UserId);

        // Index on SubmissionId for webhook handling
        builder.Entity<W9Request>()
            .HasIndex(w => w.SubmissionId);

        // Configure SongStatusHistory entity
        builder.Entity<SongStatusHistory>()
            .HasOne(ssh => ssh.SongMetadata)
            .WithMany()
            .HasForeignKey(ssh => ssh.SongMetadataId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<SongStatusHistory>()
            .HasOne(ssh => ssh.ChangedByUser)
            .WithMany()
            .HasForeignKey(ssh => ssh.ChangedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Index on SongMetadataId for efficient lookups
        builder.Entity<SongStatusHistory>()
            .HasIndex(ssh => ssh.SongMetadataId);

        // Index on ChangedAt for efficient querying of recent changes
        builder.Entity<SongStatusHistory>()
            .HasIndex(ssh => ssh.ChangedAt);

        // Configure SongStream entity
        builder.Entity<SongStream>()
            .HasOne(ss => ss.SongMetadata)
            .WithMany()
            .HasForeignKey(ss => ss.SongMetadataId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<SongStream>()
            .HasOne(ss => ss.Creator)
            .WithMany()
            .HasForeignKey(ss => ss.CreatorId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Entity<SongStream>()
            .HasOne(ss => ss.StreamerUser)
            .WithMany()
            .HasForeignKey(ss => ss.StreamerUserId)
            .OnDelete(DeleteBehavior.NoAction);

        // Index on SongMetadataId for efficient lookups
        builder.Entity<SongStream>()
            .HasIndex(ss => ss.SongMetadataId);

        // Index on CreatorId for efficient lookups by creator
        builder.Entity<SongStream>()
            .HasIndex(ss => ss.CreatorId);

        // Index on StreamerUserId for efficient lookups
        builder.Entity<SongStream>()
            .HasIndex(ss => ss.StreamerUserId);

        // Index on CreatedDate for efficient querying of recent streams
        builder.Entity<SongStream>()
            .HasIndex(ss => ss.CreatedDate);

        // Configure Genre entity
        builder.Entity<Genre>()
            .HasIndex(g => g.Name)
            .IsUnique();

        // Seed default genres from the existing creator song management page
        builder.Entity<Genre>().HasData(
            new Genre { Id = 1, Name = "Classical", IsActive = true, CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CreatedByEmail = "admin@app.com" },
            new Genre { Id = 2, Name = "Country", IsActive = true, CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CreatedByEmail = "admin@app.com" },
            new Genre { Id = 3, Name = "Electronic", IsActive = true, CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CreatedByEmail = "admin@app.com" },
            new Genre { Id = 4, Name = "Hip-Hop", IsActive = true, CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CreatedByEmail = "admin@app.com" },
            new Genre { Id = 5, Name = "Jazz", IsActive = true, CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CreatedByEmail = "admin@app.com" },
            new Genre { Id = 6, Name = "Pop", IsActive = true, CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CreatedByEmail = "admin@app.com" },
            new Genre { Id = 7, Name = "R&B", IsActive = true, CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CreatedByEmail = "admin@app.com" },
            new Genre { Id = 8, Name = "Rock", IsActive = true, CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CreatedByEmail = "admin@app.com" }
        );

        // Configure UserHistory entity
        builder.Entity<UserHistory>()
            .HasOne(uh => uh.User)
            .WithMany()
            .HasForeignKey(uh => uh.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Index on UserId for efficient lookups
        builder.Entity<UserHistory>()
            .HasIndex(uh => uh.UserId);

        // Index on OccurredAt for efficient querying of recent events
        builder.Entity<UserHistory>()
            .HasIndex(uh => uh.OccurredAt);

        // Index on EventType for filtering
        builder.Entity<UserHistory>()
            .HasIndex(uh => uh.EventType);

        // Configure Tip entity
        builder.Entity<Tip>()
            .HasOne(t => t.TipperUser)
            .WithMany()
            .HasForeignKey(t => t.TipperUserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Entity<Tip>()
            .HasOne(t => t.Creator)
            .WithMany()
            .HasForeignKey(t => t.CreatorId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Entity<Tip>()
            .HasOne(t => t.SongMetadata)
            .WithMany()
            .HasForeignKey(t => t.SongMetadataId)
            .OnDelete(DeleteBehavior.SetNull);

        // Index on CreatorId for efficient payout queries
        builder.Entity<Tip>()
            .HasIndex(t => t.CreatorId);

        // Index on TipperUserId for rate-limit queries
        builder.Entity<Tip>()
            .HasIndex(t => t.TipperUserId);

        // Index on Status for payout processing
        builder.Entity<Tip>()
            .HasIndex(t => t.Status);

        // Index on CreatedAt for hold-period queries
        builder.Entity<Tip>()
            .HasIndex(t => t.CreatedAt);

        // Configure BlockedTipAttempt entity - same NoAction pattern as Tip to avoid
        // multiple cascade paths through TipperUser -> AspNetUsers and Creator -> AspNetUsers
        builder.Entity<BlockedTipAttempt>()
            .HasOne(b => b.TipperUser)
            .WithMany()
            .HasForeignKey(b => b.TipperUserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Entity<BlockedTipAttempt>()
            .HasOne(b => b.Creator)
            .WithMany()
            .HasForeignKey(b => b.CreatorId)
            .OnDelete(DeleteBehavior.NoAction);

        // Configure ChargebackLog entity
        builder.Entity<ChargebackLog>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<ChargebackLog>()
            .HasOne(c => c.Tip)
            .WithMany()
            .HasForeignKey(c => c.TipId)
            .OnDelete(DeleteBehavior.SetNull);

        // Index on TipId for efficient lookups
        builder.Entity<ChargebackLog>()
            .HasIndex(c => c.TipId);

        // Configure CreatorPersona entity
        builder.Entity<CreatorPersona>()
            .HasOne(cp => cp.Creator)
            .WithMany()
            .HasForeignKey(cp => cp.CreatorId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure SongMetadata -> CreatorPersona (optional, no cascade to avoid multi-path)
        builder.Entity<SongMetadata>()
            .HasOne(sm => sm.Persona)
            .WithMany()
            .HasForeignKey(sm => sm.PersonaId)
            .OnDelete(DeleteBehavior.NoAction);

        // Configure ReportedSong entity
        builder.Entity<ReportedSong>()
            .HasOne(rs => rs.SongMetadata)
            .WithMany()
            .HasForeignKey(rs => rs.SongMetadataId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ReportedSong>()
            .HasOne(rs => rs.ReportingUser)
            .WithMany()
            .HasForeignKey(rs => rs.ReportingUserId)
            .OnDelete(DeleteBehavior.NoAction);

        // Unique index: one report per user per song
        builder.Entity<ReportedSong>()
            .HasIndex(rs => new { rs.SongMetadataId, rs.ReportingUserId })
            .IsUnique();

        // Index on ReportingUserId for efficient lookups
        builder.Entity<ReportedSong>()
            .HasIndex(rs => rs.ReportingUserId);

        builder.Entity<ContactRequestSubmission>()
            .HasOne(submission => submission.User)
            .WithMany()
            .HasForeignKey(submission => submission.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ContactRequestSubmission>()
            .HasIndex(submission => new { submission.UserId, submission.SubmittedAtUtc });

        builder.Entity<ContactRequestSubmission>()
            .HasIndex(submission => new { submission.IpAddress, submission.SubmittedAtUtc });
    }
}
