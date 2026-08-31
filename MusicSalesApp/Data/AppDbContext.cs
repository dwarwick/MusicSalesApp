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
    public DbSet<TopStreamedPlaylistEntry> TopStreamedPlaylistEntries { get; set; }
    public DbSet<AppSettings> AppSettings { get; set; }
    public DbSet<Creator> Creators { get; set; }
    public DbSet<StreamPayout> StreamPayouts { get; set; }
    public DbSet<W9Request> W9Requests { get; set; }
    public DbSet<SongStatusHistory> SongStatusHistories { get; set; }
    public DbSet<MediaIntegrityAuditRun> MediaIntegrityAuditRuns { get; set; }
    public DbSet<MediaIntegrityAuditItem> MediaIntegrityAuditItems { get; set; }
    public DbSet<SongUploadJob> SongUploadJobs { get; set; }
    public DbSet<MediaIntegrityAuditNotification> MediaIntegrityAuditNotifications { get; set; }
    public DbSet<StorageBackupRun> StorageBackupRuns { get; set; }
    public DbSet<StorageBackupContainerProgress> StorageBackupContainerProgresses { get; set; }
    public DbSet<StorageBackupItemFailure> StorageBackupItemFailures { get; set; }
    public DbSet<ImageVariantBackfillRun> ImageVariantBackfillRuns { get; set; }
    public DbSet<HlsPackagingBackfillRun> HlsPackagingBackfillRuns { get; set; }
    public DbSet<HlsPackagingBackfillFailure> HlsPackagingBackfillFailures { get; set; }
    public DbSet<ImageVariantBackfillItemFailure> ImageVariantBackfillItemFailures { get; set; }
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
    public DbSet<PayPalSubscriptionAnomaly> PayPalSubscriptionAnomalies { get; set; }
    public DbSet<DurableFunctionTask> DurableFunctionTasks { get; set; }
    public DbSet<LyricsAlignmentJob> LyricsAlignmentJobs { get; set; }
    public DbSet<SongLyrics> SongLyrics { get; set; }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampSubscriptionActivations();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampSubscriptionActivations();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Records the first time a subscription reaches ACTIVE.
    ///
    /// <para>
    /// Done here rather than at the nine places that assign Status, because the value has to be a
    /// durable fact and the ninth assignment is the one someone forgets. Trial eligibility depends
    /// on it: HasPriorActivatedSubscriptionAsync used to infer "has subscribed before" from the
    /// row's current status, so a cancellation - by webhook, by the drift sweep, by anything -
    /// handed the user another free trial.
    /// </para>
    ///
    /// <para>
    /// Write-once. Never cleared, never overwritten, so a resubscribe on the same row keeps the
    /// original activation.
    /// </para>
    /// </summary>
    private void StampSubscriptionActivations()
    {
        foreach (var entry in ChangeTracker.Entries<Subscription>())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified)
            {
                continue;
            }

            if (entry.Entity.ActivatedAtUtc == null
                && string.Equals(entry.Entity.Status, SubscriptionStatuses.Active, StringComparison.Ordinal))
            {
                entry.Entity.ActivatedAtUtc = DateTime.UtcNow;
            }
        }
    }

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

        // Covers the nightly entitlement drift sweep, which filters on BillingSource and orders by
        // LastProviderCheckAtUtc. Subscriptions otherwise carries only a UserId index, so the sweep
        // would be a full scan plus a sort every night, growing with the subscriber base.
        builder.Entity<Subscription>()
            .HasIndex(subscription => new
            {
                subscription.BillingSource,
                subscription.LastProviderCheckAtUtc,
                subscription.Id
            });

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

        builder.Entity<PayPalSubscriptionAnomaly>()
            .HasOne(anomaly => anomaly.Subscription)
            .WithMany()
            .HasForeignKey(anomaly => anomaly.SubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PayPalSubscriptionAnomaly>()
            .HasIndex(anomaly => anomaly.SubscriptionId)
            .HasFilter("[ResolvedAtUtc] IS NULL")
            .IsUnique();

        builder.Entity<PayPalSubscriptionAnomaly>()
            .HasIndex(anomaly => anomaly.CorrelationId)
            .IsUnique();

        builder.Entity<PayPalSubscriptionAnomaly>()
            .HasIndex(anomaly => anomaly.UserId);

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

        // Configure TopStreamedPlaylistEntry entity
        builder.Entity<TopStreamedPlaylistEntry>()
            .HasOne(entry => entry.SongMetadata)
            .WithMany()
            .HasForeignKey(entry => entry.SongMetadataId)
            .OnDelete(DeleteBehavior.Cascade);

        // Every read is "give me one chart in rank order", so the covering shape is (Window, DisplayOrder).
        builder.Entity<TopStreamedPlaylistEntry>()
            .HasIndex(entry => new { entry.Window, entry.DisplayOrder });

        // Configure AppSettings entity
        builder.Entity<AppSettings>()
            .HasIndex(s => s.Key)
            .IsUnique();

        // Seed application settings
        builder.Entity<AppSettings>().HasData(
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

        // Production uses SQL Server. Keep this provider-specific model constraint out
        // of SQLite test models; the SQL Server migration below still creates it.
        if (Database.IsSqlServer())
        {
            builder.Entity<SongMetadata>()
                .ToTable(table => table.HasCheckConstraint(
                    "CK_SongMetadata_AudioRequiresTitle",
                    "[Mp3BlobPath] IS NULL OR ([SongTitle] IS NOT NULL AND LTRIM(RTRIM([SongTitle])) <> '')"));
        }

        // Every legacy song has a NULL MediaGuid. SQL Server treats NULLs as equal in a unique
        // index and would allow only one such row, so the index has to be filtered to the rows
        // that actually carry a GUID. SQLite already permits repeated NULLs, so it needs no filter.
        var mediaGuidIndex = builder.Entity<SongMetadata>()
            .HasIndex(song => song.MediaGuid)
            .IsUnique();
        if (Database.IsSqlServer())
        {
            mediaGuidIndex.HasFilter("[MediaGuid] IS NOT NULL");
        }

        // Mp3BlobPath is looked up by value on the public media route, which until now meant a scan
        // per request (SongMetadataService.GetByBlobPathAsync ORs across three path columns).
        builder.Entity<SongMetadata>()
            .HasIndex(song => song.Mp3BlobPath);

        // The HLS package folder, for the same reason: the streaming endpoints resolve a request
        // back to its song by this value on every manifest and key request. Unique and filtered for
        // the same reason MediaGuid is - most rows are NULL until the backfill reaches them, and
        // SQL Server would otherwise permit only one of them.
        var hlsStreamIndex = builder.Entity<SongMetadata>()
            .HasIndex(song => song.HlsStreamId)
            .IsUnique();
        if (Database.IsSqlServer())
        {
            hlsStreamIndex.HasFilter("[HlsStreamId] IS NOT NULL");
        }

        builder.Entity<MediaIntegrityAuditRun>()
            .HasMany(run => run.Items)
            .WithOne(item => item.AuditRun)
            .HasForeignKey(item => item.AuditRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<MediaIntegrityAuditItem>()
            .HasOne(item => item.SongMetadata)
            .WithMany()
            .HasForeignKey(item => item.SongMetadataId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<MediaIntegrityAuditItem>()
            .HasIndex(item => new { item.AuditRunId, item.SongMetadataId });

        builder.Entity<MediaIntegrityAuditRun>()
            .HasIndex(run => run.ActiveLockKey)
            .IsUnique()
            .HasFilter("[ActiveLockKey] IS NOT NULL");

        // The media GUID is how every queue message and callback finds its job, and a duplicate
        // would let two callbacks assemble the same song twice. Unique rather than merely indexed.
        builder.Entity<SongUploadJob>()
            .HasIndex(job => job.MediaGuid)
            .IsUnique();

        // The upload page lists a creator's in-flight jobs on every load, always filtered to the
        // unfinished ones.
        builder.Entity<SongUploadJob>()
            .HasIndex(job => new { job.CreatorId, job.Status });

        // The reconciler sweeps by "stopped moving", not by age.
        builder.Entity<SongUploadJob>()
            .HasIndex(job => new { job.Status, job.StepUpdatedAt });

        builder.Entity<SongUploadJob>()
            .HasOne(job => job.Creator)
            .WithMany()
            .HasForeignKey(job => job.CreatorId)
            .OnDelete(DeleteBehavior.Cascade);

        // A completed job points at the song it produced. Deleting the song must not delete the
        // job history, so the link is severed rather than cascaded - the same choice
        // SongMetadata.CreatorId already makes.
        builder.Entity<SongUploadJob>()
            .HasOne(job => job.SongMetadata)
            .WithMany()
            .HasForeignKey(job => job.SongMetadataId)
            .OnDelete(DeleteBehavior.SetNull);

        // Configure DurableFunctionTask entity

        // The instance id is how a callback, a status poll and a terminate request all find the same
        // run. Two rows claiming one instance would mean two callers believing they own it.
        builder.Entity<DurableFunctionTask>()
            .HasIndex(task => task.InstanceId)
            .IsUnique();

        // The reconciler sweeps one durable function's unfinished runs at a time.
        builder.Entity<DurableFunctionTask>()
            .HasIndex(task => new { task.FunctionName, task.Status });

        // Configure LyricsAlignmentJob entity

        // The attempt GUID names its staging folder and correlates every callback. Unique for the
        // same reason SongUploadJob.MediaGuid is - a duplicate would let two callbacks assemble the
        // same result twice.
        builder.Entity<LyricsAlignmentJob>()
            .HasIndex(job => job.JobId)
            .IsUnique();

        // The reconciler sweeps by "stopped moving", not by age.
        builder.Entity<LyricsAlignmentJob>()
            .HasIndex(job => new { job.Status, job.StepUpdatedAt });

        // Submitting refuses while an attempt for the same song is still in flight, which is a
        // lookup on exactly this pair. Without it that check table-scans on every submit.
        builder.Entity<LyricsAlignmentJob>()
            .HasIndex(job => new { job.SongMetadataId, job.Status });

        builder.Entity<LyricsAlignmentJob>()
            .HasIndex(job => new { job.CreatorId, job.Status });

        // An attempt belongs to its song; deleting the song makes its alignment history meaningless,
        // so it goes too. Note there is deliberately no Creator foreign key here - see the comment
        // on LyricsAlignmentJob.CreatorId - which is what keeps this the only cascade path in.
        builder.Entity<LyricsAlignmentJob>()
            .HasOne(job => job.SongMetadata)
            .WithMany()
            .HasForeignKey(job => job.SongMetadataId)
            .OnDelete(DeleteBehavior.Cascade);

        // The orchestration record outlives the attempt that started it: it is the generic audit of
        // what this application asked Azure to run, and is useful precisely when the domain row has
        // been tidied away.
        builder.Entity<LyricsAlignmentJob>()
            .HasOne(job => job.DurableFunctionTask)
            .WithMany()
            .HasForeignKey(job => job.DurableFunctionTaskId)
            .OnDelete(DeleteBehavior.SetNull);

        // Configure SongLyrics entity

        // One lyrics record per song. Unique rather than merely indexed, because the completion
        // service upserts on this key and a duplicate would let two rows disagree about what is
        // published.
        builder.Entity<SongLyrics>()
            .HasIndex(lyrics => lyrics.SongMetadataId)
            .IsUnique();

        // The public media whitelist resolves an incoming blob path to a lyrics row, and that check
        // runs on EVERY request to api/music - including every audio stream and every cover image,
        // none of which are lyrics. Without these it is a table scan per request; with them it is a
        // seek that misses. Two separate indexes rather than one composite, because the lookup is an
        // OR across the two columns and a composite could only serve the leading one.
        builder.Entity<SongLyrics>()
            .HasIndex(lyrics => lyrics.TimingsBlobPath);

        builder.Entity<SongLyrics>()
            .HasIndex(lyrics => lyrics.LrcBlobPath);

        builder.Entity<SongLyrics>()
            .HasOne(lyrics => lyrics.SongMetadata)
            .WithMany()
            .HasForeignKey(lyrics => lyrics.SongMetadataId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<MediaIntegrityAuditNotification>()
            .HasOne(notification => notification.AuditRun)
            .WithMany(run => run.Notifications)
            .HasForeignKey(notification => notification.AuditRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<MediaIntegrityAuditNotification>()
            .HasIndex(notification => new
            {
                notification.AuditRunId,
                notification.NotificationType,
                notification.Recipient
            })
            .IsUnique();

        builder.Entity<StorageBackupRun>()
            .HasMany(run => run.Containers)
            .WithOne(container => container.Run)
            .HasForeignKey(container => container.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<StorageBackupRun>()
            .HasMany(run => run.Failures)
            .WithOne(failure => failure.Run)
            .HasForeignKey(failure => failure.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        // Only one backup or restore may be active at a time; the filtered unique index makes
        // this race-proof rather than relying on a read-then-write check.
        builder.Entity<StorageBackupRun>()
            .HasIndex(run => run.ActiveLockKey)
            .IsUnique()
            .HasFilter("[ActiveLockKey] IS NOT NULL");

        builder.Entity<StorageBackupRun>()
            .HasIndex(run => run.CreatedAt);

        builder.Entity<StorageBackupContainerProgress>()
            .HasIndex(container => new { container.RunId, container.SourceContainerName })
            .IsUnique();

        builder.Entity<ImageVariantBackfillRun>()
            .HasMany(run => run.Failures)
            .WithOne(failure => failure.Run)
            .HasForeignKey(failure => failure.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        // Same singleton-lock pattern as the storage backup: the image backfill is CPU-bound and
        // runs in-process with the web app, so two overlapping runs would be a self-inflicted
        // outage rather than merely wasteful.
        builder.Entity<ImageVariantBackfillRun>()
            .HasIndex(run => run.ActiveLockKey)
            .IsUnique()
            .HasFilter("[ActiveLockKey] IS NOT NULL");

        builder.Entity<ImageVariantBackfillRun>()
            .HasIndex(run => run.CreatedAt);


        builder.Entity<HlsPackagingBackfillRun>()
            .HasMany(run => run.Failures)
            .WithOne(failure => failure.Run)
            .HasForeignKey(failure => failure.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        // The same singleton-lock pattern as the other two long-running jobs. Overlapping packaging
        // runs would not merely waste Function executions - two runs could mint different
        // HlsStreamIds for the same song, and whichever callback landed second would sweep the
        // folder the first had just recorded.
        builder.Entity<HlsPackagingBackfillRun>()
            .HasIndex(run => run.ActiveLockKey)
            .IsUnique()
            .HasFilter("[ActiveLockKey] IS NOT NULL");

        builder.Entity<HlsPackagingBackfillRun>()
            .HasIndex(run => run.CreatedAt);

        builder.Entity<HlsPackagingBackfillFailure>()
            .HasIndex(failure => failure.RunId);

        builder.Entity<ImageVariantBackfillItemFailure>()
            .HasIndex(failure => failure.RunId);

        builder.Entity<StorageBackupItemFailure>()
            .HasIndex(failure => failure.RunId);

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

        // Composite for the top-streamed charts, which filter on a date range and then group by song.
        // The single-column CreatedDate index above finds the rows but still requires a lookup per row
        // to read SongMetadataId; with the song id in the key the whole group-by is served from the
        // index. Matters most for the 365-day chart, which scans the widest range.
        builder.Entity<SongStream>()
            .HasIndex(ss => new { ss.CreatedDate, ss.SongMetadataId });

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
