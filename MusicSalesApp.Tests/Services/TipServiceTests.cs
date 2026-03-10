using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class TipServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<TipService>> _mockLogger;
    private Mock<IEmailService> _mockEmailService;
    private Mock<IAdminNotificationService> _mockAdminNotificationService;
    private TipService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TipTestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);

        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<TipService>>();
        _mockEmailService = new Mock<IEmailService>();
        _mockAdminNotificationService = new Mock<IAdminNotificationService>();

        _service = new TipService(
            _mockContextFactory.Object,
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockEmailService.Object,
            _mockAdminNotificationService.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private async Task<(ApplicationUser user, Creator creator)> SeedUserAndCreator(int userId = 1, int creatorUserId = 2)
    {
        var user = new ApplicationUser { Id = userId, UserName = "tipper@test.com", Email = "tipper@test.com", NormalizedEmail = "TIPPER@TEST.COM", NormalizedUserName = "TIPPER@TEST.COM" };
        var creatorUser = new ApplicationUser { Id = creatorUserId, UserName = "creator@test.com", Email = "creator@test.com", NormalizedEmail = "CREATOR@TEST.COM", NormalizedUserName = "CREATOR@TEST.COM" };

        _context.Users.AddRange(user, creatorUser);
        var creator = new Creator { Id = 1, UserId = creatorUserId, IsActive = true, DisplayName = "Test Creator", PayPalEmail = "creator@test.com" };
        _context.Creators.Add(creator);

        // Add Registration history for user (30 days ago, so account is old enough)
        _context.UserHistories.Add(new UserHistory
        {
            UserId = userId,
            UserEmail = "tipper@test.com",
            EventType = UserHistoryEventTypes.Registration,
            Description = "User registered",
            OccurredAt = DateTime.UtcNow.AddDays(-30)
        });

        await _context.SaveChangesAsync();
        return (user, creator);
    }

    [Test]
    public async Task ValidateTipAsync_ValidTip_ReturnsCanTip()
    {
        // Arrange
        await SeedUserAndCreator();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 5.00m, null, null);

        // Assert
        Assert.That(canTip, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ValidateTipAsync_BelowMinimum_ReturnsFalse()
    {
        // Arrange
        await SeedUserAndCreator();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 0.50m, null, null);

        // Assert
        Assert.That(canTip, Is.False);
        Assert.That(error, Does.Contain("Minimum tip amount"));
    }

    [Test]
    public async Task ValidateTipAsync_AboveMaximum_ReturnsFalse()
    {
        // Arrange
        await SeedUserAndCreator();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 51.00m, null, null);

        // Assert
        Assert.That(canTip, Is.False);
        Assert.That(error, Does.Contain("Maximum tip amount"));
    }

    [Test]
    public async Task ValidateTipAsync_SelfTipping_ReturnsFalse()
    {
        // Arrange - user 2 is the creator
        await SeedUserAndCreator();

        // Act - user 2 tries to tip themselves (they are the creator)
        var (canTip, error) = await _service.ValidateTipAsync(2, 1, 5.00m, null, null);

        // Assert
        Assert.That(canTip, Is.False);
        Assert.That(error, Does.Contain("cannot tip yourself"));
    }

    [Test]
    public async Task ValidateTipAsync_NewAccount_ReturnsFalse()
    {
        // Arrange
        var user = new ApplicationUser { Id = 10, UserName = "newuser@test.com", Email = "newuser@test.com", NormalizedEmail = "NEWUSER@TEST.COM", NormalizedUserName = "NEWUSER@TEST.COM" };
        _context.Users.Add(user);

        var creatorUser = new ApplicationUser { Id = 20, UserName = "creator@test.com", Email = "creator@test.com", NormalizedEmail = "CREATOR@TEST.COM", NormalizedUserName = "CREATOR@TEST.COM" };
        _context.Users.Add(creatorUser);

        var creator = new Creator { Id = 5, UserId = 20, IsActive = true, DisplayName = "Creator" };
        _context.Creators.Add(creator);

        // Account created 2 days ago (too new)
        _context.UserHistories.Add(new UserHistory
        {
            UserId = 10,
            UserEmail = "newuser@test.com",
            EventType = UserHistoryEventTypes.Registration,
            Description = "User registered",
            OccurredAt = DateTime.UtcNow.AddDays(-2)
        });

        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(10, 5, 5.00m, null, null);

        // Assert
        Assert.That(canTip, Is.False);
        Assert.That(error, Does.Contain("days old"));
    }

    [Test]
    public async Task ValidateTipAsync_RateLimitExceeded_ReturnsFalse()
    {
        // Arrange
        await SeedUserAndCreator();

        // Add 5 captured tips in the last hour
        for (int i = 0; i < 5; i++)
        {
            _context.Tips.Add(new Tip
            {
                TipperUserId = 1,
                CreatorId = 1,
                Amount = 1.00m,
                Status = TipStatus.Pending,
                PayPalOrderId = $"ORDER-{i}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                CapturedAt = DateTime.UtcNow.AddMinutes(-10)
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 1.00m, null, null);

        // Assert
        Assert.That(canTip, Is.False);
        Assert.That(error, Does.Contain("tips per hour"));
    }

    [Test]
    public async Task ValidateTipAsync_UncapturedTipsDoNotCountTowardsRateLimit_ReturnsCanTip()
    {
        // Arrange
        await SeedUserAndCreator();

        // Add 5 uncaptured (abandoned) tips in the last hour - these should NOT count
        for (int i = 0; i < 5; i++)
        {
            _context.Tips.Add(new Tip
            {
                TipperUserId = 1,
                CreatorId = 1,
                Amount = 1.00m,
                Status = TipStatus.Pending,
                PayPalOrderId = $"UNCAPTURED-ORDER-{i}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                CapturedAt = null // Not captured - user abandoned PayPal checkout
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 1.00m, null, null);

        // Assert - should succeed because uncaptured tips don't count
        Assert.That(canTip, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ValidateTipAsync_UncapturedTipsDoNotCountTowardsLifetimeLimit_ReturnsCanTip()
    {
        // Arrange
        await SeedUserAndCreator();

        // Add 10 uncaptured tips to the same creator - these should NOT count
        for (int i = 0; i < 10; i++)
        {
            _context.Tips.Add(new Tip
            {
                TipperUserId = 1,
                CreatorId = 1,
                Amount = 1.00m,
                Status = TipStatus.Pending,
                PayPalOrderId = $"UNCAPTURED-LIFETIME-{i}",
                CreatedAt = DateTime.UtcNow.AddDays(-(i + 1)),
                CapturedAt = null
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 1.00m, null, null);

        // Assert - should succeed because uncaptured tips don't count
        Assert.That(canTip, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ValidateTipAsync_MaxTipsToSameCreator_ReturnsFalse()
    {
        // Arrange
        await SeedUserAndCreator();

        // Add 10 captured tips to the same creator (spread over time to avoid hourly rate limit)
        for (int i = 0; i < 10; i++)
        {
            _context.Tips.Add(new Tip
            {
                TipperUserId = 1,
                CreatorId = 1,
                Amount = 1.00m,
                Status = TipStatus.Paid,
                PayPalOrderId = $"ORDER-{i}",
                CreatedAt = DateTime.UtcNow.AddDays(-(i + 1)),
                CapturedAt = DateTime.UtcNow.AddDays(-(i + 1))
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 1.00m, null, null);

        // Assert
        Assert.That(canTip, Is.False);
        Assert.That(error, Does.Contain("maximum number of tips"));
    }

    [Test]
    public async Task GetTipsForCreatorAsync_ReturnsTips()
    {
        // Arrange
        await SeedUserAndCreator();

        _context.Tips.AddRange(
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 5.00m, Status = TipStatus.Pending, PayPalOrderId = "O1", CreatedAt = DateTime.UtcNow },
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 10.00m, Status = TipStatus.Paid, PayPalOrderId = "O2", CreatedAt = DateTime.UtcNow.AddDays(-10) }
        );
        await _context.SaveChangesAsync();

        // Act
        var tips = await _service.GetTipsForCreatorAsync(1);

        // Assert
        Assert.That(tips, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetClearedTipsForPayoutAsync_OnlyReturnsCleared()
    {
        // Arrange
        await SeedUserAndCreator();

        _context.Tips.AddRange(
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 5.00m, Status = TipStatus.Pending, PayPalOrderId = "O1", CreatedAt = DateTime.UtcNow },
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 10.00m, Status = TipStatus.Cleared, PayPalOrderId = "O2", CreatedAt = DateTime.UtcNow.AddDays(-10) },
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 3.00m, Status = TipStatus.Paid, PayPalOrderId = "O3", CreatedAt = DateTime.UtcNow.AddDays(-20) }
        );
        await _context.SaveChangesAsync();

        // Act
        var tips = await _service.GetClearedTipsForPayoutAsync(1);

        // Assert
        Assert.That(tips, Has.Count.EqualTo(1));
        Assert.That(tips[0].Amount, Is.EqualTo(10.00m));
    }

    [Test]
    public async Task ProcessPendingToClearedAsync_ClearsOldTips()
    {
        // Arrange
        await SeedUserAndCreator();

        _context.Tips.AddRange(
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 5.00m, Status = TipStatus.Pending, PayPalOrderId = "O1", CreatedAt = DateTime.UtcNow.AddDays(-8), CapturedAt = DateTime.UtcNow.AddDays(-8) }, // Old enough and captured
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 3.00m, Status = TipStatus.Pending, PayPalOrderId = "O2", CreatedAt = DateTime.UtcNow.AddDays(-2), CapturedAt = DateTime.UtcNow.AddDays(-2) }  // Too new
        );
        await _context.SaveChangesAsync();

        // Act
        var clearedCount = await _service.ProcessPendingToClearedAsync();

        // Assert
        Assert.That(clearedCount, Is.EqualTo(1));

        // Verify the status was actually updated
        using var verifyContext = new AppDbContext(_contextOptions);
        var tips = await verifyContext.Tips.ToListAsync();
        Assert.That(tips.Count(t => t.Status == TipStatus.Cleared), Is.EqualTo(1));
        Assert.That(tips.Count(t => t.Status == TipStatus.Pending), Is.EqualTo(1));
    }

    [Test]
    public async Task ProcessPendingToClearedAsync_SkipsUncapturedTips()
    {
        // Arrange
        await SeedUserAndCreator();

        _context.Tips.AddRange(
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 5.00m, Status = TipStatus.Pending, PayPalOrderId = "O1", CreatedAt = DateTime.UtcNow.AddDays(-8), CapturedAt = DateTime.UtcNow.AddDays(-8) }, // Captured - should clear
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 3.00m, Status = TipStatus.Pending, PayPalOrderId = "O2", CreatedAt = DateTime.UtcNow.AddDays(-8), CapturedAt = null }  // Uncaptured - should NOT clear
        );
        await _context.SaveChangesAsync();

        // Act
        var clearedCount = await _service.ProcessPendingToClearedAsync();

        // Assert - only the captured tip should be cleared
        Assert.That(clearedCount, Is.EqualTo(1));

        using var verifyContext = new AppDbContext(_contextOptions);
        var tips = await verifyContext.Tips.ToListAsync();
        Assert.That(tips.Count(t => t.Status == TipStatus.Cleared), Is.EqualTo(1));
        // Uncaptured tip older than 24h should be removed
        Assert.That(tips.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task ProcessPendingToClearedAsync_RemovesStaleTips()
    {
        // Arrange
        await SeedUserAndCreator();

        _context.Tips.Add(
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 5.00m, Status = TipStatus.Pending, PayPalOrderId = "O1", CreatedAt = DateTime.UtcNow.AddDays(-2), CapturedAt = null } // Uncaptured, older than 24h
        );
        await _context.SaveChangesAsync();

        // Act
        var clearedCount = await _service.ProcessPendingToClearedAsync();

        // Assert - no tips cleared, but stale uncaptured tip should be removed
        Assert.That(clearedCount, Is.EqualTo(0));

        using var verifyContext = new AppDbContext(_contextOptions);
        var tips = await verifyContext.Tips.ToListAsync();
        Assert.That(tips.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task ProcessPendingToClearedAsync_KeepsRecentUncapturedTips()
    {
        // Arrange - uncaptured tip less than 24 hours old should be kept (user may still be in PayPal checkout)
        await SeedUserAndCreator();

        _context.Tips.Add(
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 5.00m, Status = TipStatus.Pending, PayPalOrderId = "O1", CreatedAt = DateTime.UtcNow.AddMinutes(-30), CapturedAt = null }
        );
        await _context.SaveChangesAsync();

        // Act
        var clearedCount = await _service.ProcessPendingToClearedAsync();

        // Assert - tip should not be cleared (not captured) and not removed (still fresh)
        Assert.That(clearedCount, Is.EqualTo(0));

        using var verifyContext = new AppDbContext(_contextOptions);
        var tips = await verifyContext.Tips.ToListAsync();
        Assert.That(tips.Count, Is.EqualTo(1));
        Assert.That(tips[0].Status, Is.EqualTo(TipStatus.Pending));
    }

    [Test]
    public async Task ProcessPendingToClearedAsync_DoesNotClearRecentlyCapturedTips()
    {
        // Arrange - captured tip within 7-day hold period should stay Pending
        await SeedUserAndCreator();

        _context.Tips.Add(
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 5.00m, Status = TipStatus.Pending, PayPalOrderId = "O1", CreatedAt = DateTime.UtcNow.AddDays(-3), CapturedAt = DateTime.UtcNow.AddDays(-3) }
        );
        await _context.SaveChangesAsync();

        // Act
        var clearedCount = await _service.ProcessPendingToClearedAsync();

        // Assert - tip should remain Pending (under 7-day hold)
        Assert.That(clearedCount, Is.EqualTo(0));

        using var verifyContext = new AppDbContext(_contextOptions);
        var tips = await verifyContext.Tips.ToListAsync();
        Assert.That(tips.Count, Is.EqualTo(1));
        Assert.That(tips[0].Status, Is.EqualTo(TipStatus.Pending));
    }

    [Test]
    public async Task ProcessPendingToClearedAsync_MixedScenario_HandlesAllCorrectly()
    {
        // Arrange - mix of captured/uncaptured, old/new tips
        await SeedUserAndCreator();

        _context.Tips.AddRange(
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 1.00m, Status = TipStatus.Pending, PayPalOrderId = "O1", CreatedAt = DateTime.UtcNow.AddDays(-10), CapturedAt = DateTime.UtcNow.AddDays(-10) }, // Old + captured => clear
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 2.00m, Status = TipStatus.Pending, PayPalOrderId = "O2", CreatedAt = DateTime.UtcNow.AddDays(-10), CapturedAt = null },                       // Old + uncaptured => remove
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 3.00m, Status = TipStatus.Pending, PayPalOrderId = "O3", CreatedAt = DateTime.UtcNow.AddDays(-3), CapturedAt = DateTime.UtcNow.AddDays(-3) },  // Recent + captured => keep pending
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 4.00m, Status = TipStatus.Pending, PayPalOrderId = "O4", CreatedAt = DateTime.UtcNow.AddMinutes(-5), CapturedAt = null },                      // Fresh + uncaptured => keep pending
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 5.00m, Status = TipStatus.Cleared, PayPalOrderId = "O5", CreatedAt = DateTime.UtcNow.AddDays(-15) }                                            // Already cleared => ignore
        );
        await _context.SaveChangesAsync();

        // Act
        var clearedCount = await _service.ProcessPendingToClearedAsync();

        // Assert
        Assert.That(clearedCount, Is.EqualTo(1)); // Only O1 should be cleared

        using var verifyContext = new AppDbContext(_contextOptions);
        var tips = await verifyContext.Tips.OrderBy(t => t.Amount).ToListAsync();
        Assert.That(tips.Count, Is.EqualTo(4)); // O2 was removed (stale uncaptured)

        var clearedTip = tips.First(t => t.PayPalOrderId == "O1");
        Assert.That(clearedTip.Status, Is.EqualTo(TipStatus.Cleared));

        var recentCapturedTip = tips.First(t => t.PayPalOrderId == "O3");
        Assert.That(recentCapturedTip.Status, Is.EqualTo(TipStatus.Pending)); // Still in hold period

        var freshUncapturedTip = tips.First(t => t.PayPalOrderId == "O4");
        Assert.That(freshUncapturedTip.Status, Is.EqualTo(TipStatus.Pending)); // Too fresh to remove

        var alreadyCleared = tips.First(t => t.PayPalOrderId == "O5");
        Assert.That(alreadyCleared.Status, Is.EqualTo(TipStatus.Cleared)); // Unchanged
    }

    [Test]
    public async Task MarkTipsAsPaidAsync_UpdatesStatusAndDate()
    {
        // Arrange
        await SeedUserAndCreator();

        var tip1 = new Tip { TipperUserId = 1, CreatorId = 1, Amount = 5.00m, Status = TipStatus.Cleared, PayPalOrderId = "O1", CreatedAt = DateTime.UtcNow.AddDays(-10) };
        var tip2 = new Tip { TipperUserId = 1, CreatorId = 1, Amount = 10.00m, Status = TipStatus.Cleared, PayPalOrderId = "O2", CreatedAt = DateTime.UtcNow.AddDays(-9) };
        _context.Tips.AddRange(tip1, tip2);
        await _context.SaveChangesAsync();

        var tipIds = new List<int> { tip1.Id, tip2.Id };

        // Act
        await _service.MarkTipsAsPaidAsync(tipIds, "PAYOUT-TXN-123");

        // Assert
        using var verifyContext = new AppDbContext(_contextOptions);
        var tips = await verifyContext.Tips.ToListAsync();
        Assert.That(tips.All(t => t.Status == TipStatus.Paid), Is.True);
        Assert.That(tips.All(t => t.PaidAt.HasValue), Is.True);
        Assert.That(tips.All(t => t.PayPalPayoutTransactionId == "PAYOUT-TXN-123"), Is.True);
    }

    [Test]
    public async Task MarkTipsAsPaidAsync_OnlyMarksClearedTips()
    {
        // Arrange
        await SeedUserAndCreator();

        var clearedTip = new Tip { TipperUserId = 1, CreatorId = 1, Amount = 5.00m, Status = TipStatus.Cleared, PayPalOrderId = "O1", CreatedAt = DateTime.UtcNow.AddDays(-10) };
        var pendingTip = new Tip { TipperUserId = 1, CreatorId = 1, Amount = 3.00m, Status = TipStatus.Pending, PayPalOrderId = "O2", CreatedAt = DateTime.UtcNow };
        _context.Tips.AddRange(clearedTip, pendingTip);
        await _context.SaveChangesAsync();

        // Act
        await _service.MarkTipsAsPaidAsync(new List<int> { clearedTip.Id, pendingTip.Id }, "PAYOUT-TXN-456");

        // Assert
        using var verifyContext = new AppDbContext(_contextOptions);
        var tips = await verifyContext.Tips.ToListAsync();
        var paid = tips.First(t => t.Id == clearedTip.Id);
        var stillPending = tips.First(t => t.Id == pendingTip.Id);

        Assert.That(paid.Status, Is.EqualTo(TipStatus.Paid));
        Assert.That(stillPending.Status, Is.EqualTo(TipStatus.Pending)); // Should NOT have been marked as paid
    }

    [Test]
    public async Task ValidateTipAsync_CreatorNotFound_ReturnsFalse()
    {
        // Arrange
        var user = new ApplicationUser { Id = 1, UserName = "tipper@test.com", Email = "tipper@test.com", NormalizedEmail = "TIPPER@TEST.COM", NormalizedUserName = "TIPPER@TEST.COM" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 999, 5.00m, null, null);

        // Assert
        Assert.That(canTip, Is.False);
        Assert.That(error, Does.Contain("Creator not found"));
    }

    [Test]
    public async Task ValidateTipAsync_UserNotFound_ReturnsFalse()
    {
        // Act
        var (canTip, error) = await _service.ValidateTipAsync(999, 1, 5.00m, null, null);

        // Assert
        Assert.That(canTip, Is.False);
        Assert.That(error, Does.Contain("User not found"));
    }

    [Test]
    public async Task ValidateTipAsync_ExactMinimumAmount_ReturnsCanTip()
    {
        // Arrange
        await SeedUserAndCreator();

        // Act
        var (canTip, _) = await _service.ValidateTipAsync(1, 1, 1.00m, null, null);

        // Assert
        Assert.That(canTip, Is.True);
    }

    [Test]
    public async Task ValidateTipAsync_ExactMaximumAmount_ReturnsCanTip()
    {
        // Arrange
        await SeedUserAndCreator();

        // Act
        var (canTip, _) = await _service.ValidateTipAsync(1, 1, 50.00m, null, null);

        // Assert
        Assert.That(canTip, Is.True);
    }

    // ==================== Fingerprint Fraud Detection Tests ====================

    [Test]
    public async Task ValidateTipAsync_FingerprintFraud_DifferentAccountsSameFingerprint_ReturnsFalse()
    {
        // Arrange
        await SeedUserAndCreator();

        // Add a 3rd user who tipped recently from the same fingerprint
        var otherUser = new ApplicationUser { Id = 3, UserName = "other@test.com", Email = "other@test.com", NormalizedEmail = "OTHER@TEST.COM", NormalizedUserName = "OTHER@TEST.COM" };
        _context.Users.Add(otherUser);

        // 2 captured tips from different users with the same fingerprint in the last hour (meets threshold of 2)
        _context.Tips.Add(new Tip
        {
            TipperUserId = 3,
            CreatorId = 1,
            Amount = 5.00m,
            Status = TipStatus.Pending,
            PayPalOrderId = "FP-ORDER-1",
            MachineFingerprint = "same-fingerprint-abc",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            CapturedAt = DateTime.UtcNow.AddMinutes(-10)
        });

        var otherUser2 = new ApplicationUser { Id = 4, UserName = "other2@test.com", Email = "other2@test.com", NormalizedEmail = "OTHER2@TEST.COM", NormalizedUserName = "OTHER2@TEST.COM" };
        _context.Users.Add(otherUser2);
        _context.Tips.Add(new Tip
        {
            TipperUserId = 4,
            CreatorId = 1,
            Amount = 5.00m,
            Status = TipStatus.Pending,
            PayPalOrderId = "FP-ORDER-2",
            MachineFingerprint = "same-fingerprint-abc",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            CapturedAt = DateTime.UtcNow.AddMinutes(-5)
        });

        await _context.SaveChangesAsync();

        // Act - user 1 tries to tip from the same fingerprint
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 5.00m, null, "same-fingerprint-abc");

        // Assert
        Assert.That(canTip, Is.False);
        Assert.That(error, Does.Contain("Unusual activity detected"));
    }

    [Test]
    public async Task ValidateTipAsync_FingerprintFraud_BelowThreshold_ReturnsCanTip()
    {
        // Arrange
        await SeedUserAndCreator();

        // Only 1 captured tip from a different user with the same fingerprint (below threshold of 2)
        var otherUser = new ApplicationUser { Id = 3, UserName = "other@test.com", Email = "other@test.com", NormalizedEmail = "OTHER@TEST.COM", NormalizedUserName = "OTHER@TEST.COM" };
        _context.Users.Add(otherUser);
        _context.Tips.Add(new Tip
        {
            TipperUserId = 3,
            CreatorId = 1,
            Amount = 5.00m,
            Status = TipStatus.Pending,
            PayPalOrderId = "FP-ORDER-1",
            MachineFingerprint = "same-fingerprint-abc",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            CapturedAt = DateTime.UtcNow.AddMinutes(-10)
        });
        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 5.00m, null, "same-fingerprint-abc");

        // Assert
        Assert.That(canTip, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ValidateTipAsync_FingerprintFraud_OwnTipsDoNotCount_ReturnsCanTip()
    {
        // Arrange
        await SeedUserAndCreator();

        // User 1's own previous tips with the same fingerprint should NOT trigger the fraud check
        for (int i = 0; i < 3; i++)
        {
            _context.Tips.Add(new Tip
            {
                TipperUserId = 1,
                CreatorId = 1,
                Amount = 1.00m,
                Status = TipStatus.Pending,
                PayPalOrderId = $"OWN-FP-{i}",
                MachineFingerprint = "my-fingerprint",
                CreatedAt = DateTime.UtcNow.AddMinutes(-(i + 1))
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 5.00m, null, "my-fingerprint");

        // Assert
        Assert.That(canTip, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ValidateTipAsync_FingerprintFraud_OldTipsOutsideWindow_ReturnsCanTip()
    {
        // Arrange
        await SeedUserAndCreator();

        // Captured tips from different users with the same fingerprint, but older than 1 hour
        var otherUser = new ApplicationUser { Id = 3, UserName = "other@test.com", Email = "other@test.com", NormalizedEmail = "OTHER@TEST.COM", NormalizedUserName = "OTHER@TEST.COM" };
        _context.Users.Add(otherUser);

        for (int i = 0; i < 3; i++)
        {
            _context.Tips.Add(new Tip
            {
                TipperUserId = 3,
                CreatorId = 1,
                Amount = 5.00m,
                Status = TipStatus.Pending,
                PayPalOrderId = $"OLD-FP-{i}",
                MachineFingerprint = "same-fingerprint-abc",
                CreatedAt = DateTime.UtcNow.AddHours(-2), // Outside 1-hour window
                CapturedAt = DateTime.UtcNow.AddHours(-2)
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 5.00m, null, "same-fingerprint-abc");

        // Assert
        Assert.That(canTip, Is.True);
        Assert.That(error, Is.Null);
    }

    // ==================== IP Address Fraud Detection Tests ====================

    [Test]
    public async Task ValidateTipAsync_IpFraud_DifferentAccountsSameIp_ReturnsFalse()
    {
        // Arrange
        await SeedUserAndCreator();

        // Add 5 captured tips from different users with the same IP in the last hour (meets threshold of 5)
        for (int i = 0; i < 5; i++)
        {
            var otherUser = new ApplicationUser
            {
                Id = 100 + i,
                UserName = $"ipuser{i}@test.com",
                Email = $"ipuser{i}@test.com",
                NormalizedEmail = $"IPUSER{i}@TEST.COM",
                NormalizedUserName = $"IPUSER{i}@TEST.COM"
            };
            _context.Users.Add(otherUser);
            _context.Tips.Add(new Tip
            {
                TipperUserId = 100 + i,
                CreatorId = 1,
                Amount = 2.00m,
                Status = TipStatus.Pending,
                PayPalOrderId = $"IP-ORDER-{i}",
                IpAddress = "192.168.1.100",
                CreatedAt = DateTime.UtcNow.AddMinutes(-(i + 1)),
                CapturedAt = DateTime.UtcNow.AddMinutes(-(i + 1))
            });
        }
        await _context.SaveChangesAsync();

        // Act - user 1 tries to tip from the same IP
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 5.00m, "192.168.1.100", null);

        // Assert
        Assert.That(canTip, Is.False);
        Assert.That(error, Does.Contain("Unusual activity detected"));
    }

    [Test]
    public async Task ValidateTipAsync_IpFraud_BelowThreshold_ReturnsCanTip()
    {
        // Arrange
        await SeedUserAndCreator();

        // Only 4 captured tips from different users with the same IP (below threshold of 5)
        for (int i = 0; i < 4; i++)
        {
            var otherUser = new ApplicationUser
            {
                Id = 100 + i,
                UserName = $"ipuser{i}@test.com",
                Email = $"ipuser{i}@test.com",
                NormalizedEmail = $"IPUSER{i}@TEST.COM",
                NormalizedUserName = $"IPUSER{i}@TEST.COM"
            };
            _context.Users.Add(otherUser);
            _context.Tips.Add(new Tip
            {
                TipperUserId = 100 + i,
                CreatorId = 1,
                Amount = 2.00m,
                Status = TipStatus.Pending,
                PayPalOrderId = $"IP-ORDER-{i}",
                IpAddress = "192.168.1.100",
                CreatedAt = DateTime.UtcNow.AddMinutes(-(i + 1)),
                CapturedAt = DateTime.UtcNow.AddMinutes(-(i + 1))
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 5.00m, "192.168.1.100", null);

        // Assert
        Assert.That(canTip, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ValidateTipAsync_IpFraud_OwnTipsDoNotCount_ReturnsCanTip()
    {
        // Arrange
        await SeedUserAndCreator();

        // User 1's own tips from the same IP should NOT trigger the fraud check
        for (int i = 0; i < 4; i++)
        {
            _context.Tips.Add(new Tip
            {
                TipperUserId = 1,
                CreatorId = 1,
                Amount = 1.00m,
                Status = TipStatus.Pending,
                PayPalOrderId = $"OWN-IP-{i}",
                IpAddress = "10.0.0.1",
                CreatedAt = DateTime.UtcNow.AddMinutes(-(i + 1))
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 5.00m, "10.0.0.1", null);

        // Assert
        Assert.That(canTip, Is.True);
        Assert.That(error, Is.Null);
    }

    // ==================== Reciprocal Tipping (Collusion) Detection Tests ====================

    [Test]
    public async Task ValidateTipAsync_ReciprocalTipping_CollusionDetected_ReturnsFalse()
    {
        // Arrange - two users who are both creators, tipping each other
        // User 1 (tipper) is also a creator (Creator 2)
        // User 2 is Creator 1
        await SeedUserAndCreator(); // User 1 tips Creator 1 (owned by User 2)

        // Make User 1 also a creator
        var tipperCreator = new Creator { Id = 2, UserId = 1, IsActive = true, DisplayName = "Tipper Creator", PayPalEmail = "tipper@test.com" };
        _context.Creators.Add(tipperCreator);

        // User 2 (the creator being tipped) has tipped back to User 1's creator profile 3 times in the last 30 days
        for (int i = 0; i < 3; i++)
        {
            _context.Tips.Add(new Tip
            {
                TipperUserId = 2, // Creator 1's owner tips back
                CreatorId = 2,    // To User 1's creator profile
                Amount = 5.00m,
                Status = TipStatus.Cleared,
                PayPalOrderId = $"RECIP-ORDER-{i}",
                CreatedAt = DateTime.UtcNow.AddDays(-(i + 1)),
                CapturedAt = DateTime.UtcNow.AddDays(-(i + 1))
            });
        }
        await _context.SaveChangesAsync();

        // Act - User 1 tries to tip Creator 1 (owned by User 2, who has been tipping back)
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 5.00m, null, null);

        // Assert
        Assert.That(canTip, Is.False);
        Assert.That(error, Does.Contain("Reciprocal tipping limit reached"));
    }

    [Test]
    public async Task ValidateTipAsync_ReciprocalTipping_BelowThreshold_ReturnsCanTip()
    {
        // Arrange - two users who are both creators, but below the reciprocal threshold
        await SeedUserAndCreator();

        // Make User 1 also a creator
        var tipperCreator = new Creator { Id = 2, UserId = 1, IsActive = true, DisplayName = "Tipper Creator", PayPalEmail = "tipper@test.com" };
        _context.Creators.Add(tipperCreator);

        // Only 2 captured reciprocal tips (below threshold of 3)
        for (int i = 0; i < 2; i++)
        {
            _context.Tips.Add(new Tip
            {
                TipperUserId = 2,
                CreatorId = 2,
                Amount = 5.00m,
                Status = TipStatus.Cleared,
                PayPalOrderId = $"RECIP-ORDER-{i}",
                CreatedAt = DateTime.UtcNow.AddDays(-(i + 1)),
                CapturedAt = DateTime.UtcNow.AddDays(-(i + 1))
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 5.00m, null, null);

        // Assert
        Assert.That(canTip, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ValidateTipAsync_ReciprocalTipping_OutsideWindow_ReturnsCanTip()
    {
        // Arrange - reciprocal tips exist but are older than 30 days
        await SeedUserAndCreator();

        var tipperCreator = new Creator { Id = 2, UserId = 1, IsActive = true, DisplayName = "Tipper Creator", PayPalEmail = "tipper@test.com" };
        _context.Creators.Add(tipperCreator);

        // 5 captured reciprocal tips, but all older than 30 days
        for (int i = 0; i < 5; i++)
        {
            _context.Tips.Add(new Tip
            {
                TipperUserId = 2,
                CreatorId = 2,
                Amount = 5.00m,
                Status = TipStatus.Paid,
                PayPalOrderId = $"OLD-RECIP-{i}",
                CreatedAt = DateTime.UtcNow.AddDays(-(31 + i)), // Outside 30-day window
                CapturedAt = DateTime.UtcNow.AddDays(-(31 + i))
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 5.00m, null, null);

        // Assert
        Assert.That(canTip, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task ValidateTipAsync_ReciprocalTipping_TipperNotCreator_SkipsCheck()
    {
        // Arrange - tipper is NOT a creator, so reciprocal tipping check should be skipped
        await SeedUserAndCreator(); // User 1 is just a regular user, not a creator

        // Act
        var (canTip, error) = await _service.ValidateTipAsync(1, 1, 5.00m, null, null);

        // Assert
        Assert.That(canTip, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task CaptureTipAsync_NonExistentOrder_ReturnsFalse()
    {
        // Arrange
        await SeedUserAndCreator();

        // Act - try to capture an order that doesn't exist
        var (success, error, amount) = await _service.CaptureTipAsync("NON-EXISTENT-ORDER");

        // Assert
        Assert.That(success, Is.False);
        Assert.That(error, Does.Contain("not found"));
        Assert.That(amount, Is.EqualTo(0));
    }

    [Test]
    public async Task CaptureTipAsync_AlreadyCapturedTip_ReturnsFalse()
    {
        // Arrange
        await SeedUserAndCreator();

        // Add a tip that's already cleared (captured)
        _context.Tips.Add(new Tip
        {
            TipperUserId = 1,
            CreatorId = 1,
            Amount = 5.00m,
            Status = TipStatus.Cleared,
            PayPalOrderId = "ALREADY-CAPTURED",
            CreatedAt = DateTime.UtcNow.AddDays(-10)
        });
        await _context.SaveChangesAsync();

        // Act
        var (success, error, amount) = await _service.CaptureTipAsync("ALREADY-CAPTURED");

        // Assert
        Assert.That(success, Is.False);
        Assert.That(error, Does.Contain("not found"));
        Assert.That(amount, Is.EqualTo(0));
    }

    // ==================== Tip Model CapturedAt Tests ====================

    [Test]
    public void Tip_CapturedAt_DefaultsToNull()
    {
        // Act
        var tip = new Tip();

        // Assert
        Assert.That(tip.CapturedAt, Is.Null);
    }

    [Test]
    public void Tip_CapturedAt_CanBeSet()
    {
        // Arrange
        var capturedTime = DateTime.UtcNow;

        // Act
        var tip = new Tip { CapturedAt = capturedTime };

        // Assert
        Assert.That(tip.CapturedAt, Is.EqualTo(capturedTime));
    }

    [Test]
    public async Task Tip_CapturedAt_PersistsInDatabase()
    {
        // Arrange
        await SeedUserAndCreator();
        var capturedTime = DateTime.UtcNow;

        _context.Tips.Add(new Tip
        {
            TipperUserId = 1,
            CreatorId = 1,
            Amount = 5.00m,
            Status = TipStatus.Pending,
            PayPalOrderId = "PERSIST-TEST",
            CapturedAt = capturedTime,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Act - retrieve from a fresh context
        using var verifyContext = new AppDbContext(_contextOptions);
        var tip = await verifyContext.Tips.FirstAsync(t => t.PayPalOrderId == "PERSIST-TEST");

        // Assert
        Assert.That(tip.CapturedAt, Is.Not.Null);
        Assert.That(tip.CapturedAt!.Value, Is.EqualTo(capturedTime).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task Tip_CapturedAt_NullPersistsInDatabase()
    {
        // Arrange - tip without capture (abandoned checkout)
        await SeedUserAndCreator();

        _context.Tips.Add(new Tip
        {
            TipperUserId = 1,
            CreatorId = 1,
            Amount = 5.00m,
            Status = TipStatus.Pending,
            PayPalOrderId = "NULL-CAPTURE-TEST",
            CapturedAt = null,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Act
        using var verifyContext = new AppDbContext(_contextOptions);
        var tip = await verifyContext.Tips.FirstAsync(t => t.PayPalOrderId == "NULL-CAPTURE-TEST");

        // Assert
        Assert.That(tip.CapturedAt, Is.Null);
    }
}
