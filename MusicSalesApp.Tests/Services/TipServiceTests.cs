using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
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

        _service = new TipService(
            _mockContextFactory.Object,
            _mockConfiguration.Object,
            _mockLogger.Object);
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

        // Add AccountCreated history for user (30 days ago, so account is old enough)
        _context.UserHistories.Add(new UserHistory
        {
            UserId = userId,
            UserEmail = "tipper@test.com",
            EventType = "AccountCreated",
            Description = "Account created",
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
            EventType = "AccountCreated",
            Description = "Account created",
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

        // Add 5 tips in the last hour
        for (int i = 0; i < 5; i++)
        {
            _context.Tips.Add(new Tip
            {
                TipperUserId = 1,
                CreatorId = 1,
                Amount = 1.00m,
                Status = TipStatus.Pending,
                PayPalOrderId = $"ORDER-{i}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10)
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
    public async Task ValidateTipAsync_MaxTipsToSameCreator_ReturnsFalse()
    {
        // Arrange
        await SeedUserAndCreator();

        // Add 10 tips to the same creator (spread over time to avoid hourly rate limit)
        for (int i = 0; i < 10; i++)
        {
            _context.Tips.Add(new Tip
            {
                TipperUserId = 1,
                CreatorId = 1,
                Amount = 1.00m,
                Status = TipStatus.Paid,
                PayPalOrderId = $"ORDER-{i}",
                CreatedAt = DateTime.UtcNow.AddDays(-(i + 1))
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
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 5.00m, Status = TipStatus.Pending, PayPalOrderId = "O1", CreatedAt = DateTime.UtcNow.AddDays(-8) }, // Old enough
            new Tip { TipperUserId = 1, CreatorId = 1, Amount = 3.00m, Status = TipStatus.Pending, PayPalOrderId = "O2", CreatedAt = DateTime.UtcNow.AddDays(-2) }  // Too new
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
}
