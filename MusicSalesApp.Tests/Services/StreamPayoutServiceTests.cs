#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Identity;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class StreamPayoutServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<IEmailService> _mockEmailService;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<ILogger<StreamPayoutService>> _mockLogger;
    private Mock<ITaxBanditsService> _mockTaxBanditsService;
    private Mock<ITipService> _mockTipService;
    private Mock<IAppSettingsService> _mockAppSettingsService;
    private StreamPayoutService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"StreamPayoutTestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);

        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<StreamPayoutService>>();
        _mockEmailService = new Mock<IEmailService>();
        _mockEmailService.Setup(x => x.GetLogoUrl()).Returns("https://streamtunes.test/logo.png");
        _mockEmailService.Setup(x => x.GetAppBaseUrl()).Returns("https://streamtunes.test");
        _mockEmailService
            .Setup(x => x.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _mockTaxBanditsService = new Mock<ITaxBanditsService>();
        _mockTipService = new Mock<ITipService>();
        _mockTipService.Setup(x => x.ProcessPendingToClearedAsync()).ReturnsAsync(0);
        _mockTipService.Setup(x => x.GetClearedTipsForPayoutAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<Tip>());
        _mockAppSettingsService = new Mock<IAppSettingsService>();

        _service = new StreamPayoutService(
            _mockContextFactory.Object,
            _mockEmailService.Object,
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockTaxBanditsService.Object,
            _mockTipService.Object,
            _mockAppSettingsService.Object);
    }

    private async Task SeedCreatorRoleAsync(int userId)
    {
        var role = new IdentityRole<int> { Id = 1, Name = Roles.Creator, NormalizedName = Roles.Creator.ToUpperInvariant() };
        _context.Roles.Add(role);
        _context.UserRoles.Add(new IdentityUserRole<int> { UserId = userId, RoleId = role.Id });
        await _context.SaveChangesAsync();
    }

    private async Task<Creator> SeedPayoutEligibleCreatorAsync(
        bool payPalReady = false,
        bool taxReady = false,
        bool includeStreams = true,
        bool assignRole = true)
    {
        var creatorUser = new ApplicationUser
        {
            Id = 100,
            UserName = "blockedcreator@test.com",
            Email = "blockedcreator@test.com",
            NormalizedEmail = "BLOCKEDCREATOR@TEST.COM",
            NormalizedUserName = "BLOCKEDCREATOR@TEST.COM"
        };
        _context.Users.Add(creatorUser);

        var creator = new Creator
        {
            Id = 100,
            UserId = creatorUser.Id,
            IsActive = true,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            DisplayName = "Blocked Creator",
            PayPalEmail = payPalReady ? "paypal@test.com" : null,
            PayPalAccountAffirmed = payPalReady,
            TaxFormStatus = taxReady ? TaxFormStatus.Completed : TaxFormStatus.NotStarted,
            TaxResidencyType = taxReady ? TaxResidencyType.US : TaxResidencyType.Unknown,
            TaxBanditsPayeeRef = taxReady ? creatorUser.Email : null,
            LocationCertification = CreatorLocationCertification.USPerson,
            AcknowledgmentAccepted = true,
            PayoutRequirementsAcknowledged = true,
            StreamPayRate = 0.005m
        };
        _context.Creators.Add(creator);

        if (includeStreams)
        {
            _context.SongMetadata.Add(new SongMetadata
            {
                Id = 100,
                Mp3BlobPath = "creator/song.mp3",
                SongTitle = "Eligible Song",
                CreatorId = creator.Id,
                IsActive = true,
                IsAlbumCover = false,
                NumberOfStreams = 2000,
                StreamsAtLastPayout = 0
            });
        }

        await _context.SaveChangesAsync();
        if (assignRole)
        {
            await SeedCreatorRoleAsync(creatorUser.Id);
        }

        return creator;
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private async Task SeedCreatorWithPayouts(decimal tipAmount = 0m)
    {
        var creatorUser = new ApplicationUser
        {
            Id = 1, UserName = "creator@test.com", Email = "creator@test.com",
            NormalizedEmail = "CREATOR@TEST.COM", NormalizedUserName = "CREATOR@TEST.COM"
        };
        _context.Users.Add(creatorUser);

        var creator = new Creator
        {
            Id = 1, UserId = 1, IsActive = true,
            DisplayName = "Test Creator",
            PayPalEmail = "creator@test.com",
            TaxResidencyType = TaxResidencyType.US,
            TaxBanditsPayeeRef = "creator@test.com"
        };
        _context.Creators.Add(creator);

        var songMetadata = new SongMetadata
        {
            Id = 1, Mp3BlobPath = "test.mp3", SongTitle = "Test Song",
            CreatorId = 1
        };
        _context.SongMetadata.Add(songMetadata);

        await _context.SaveChangesAsync();

        // Add a stream payout record with tip amount
        _context.StreamPayouts.Add(new StreamPayout
        {
            CreatorId = 1,
            SongMetadataId = 1,
            NumberOfStreams = 2000,
            RatePerStream = 0.005m,
            GrossAmount = 10.00m,
            WithholdingRate = 0m,
            WithheldAmount = 0m,
            NetAmount = 10.00m,
            TipAmount = tipAmount,
            PayPalTransactionId = "PAYOUT-TXN-001",
            TaxBanditsStatus = "Pending",
            PaymentDate = DateTime.UtcNow.AddDays(-1)
        });
        await _context.SaveChangesAsync();
    }

    // ==================== RetryPending1099TransactionsAsync Tests ====================

    [Test]
    public async Task ProcessPendingPayoutsAsync_BlocksStreamPayoutAndEmails_WhenPayPalAndTaxIncomplete()
    {
        await SeedPayoutEligibleCreatorAsync(payPalReady: false, taxReady: false);

        var processed = await _service.ProcessPendingPayoutsAsync();

        Assert.That(processed, Is.EqualTo(0));

        await using var verifyContext = new AppDbContext(_contextOptions);
        var song = await verifyContext.SongMetadata.SingleAsync(s => s.Id == 100);
        Assert.That(song.StreamsAtLastPayout, Is.EqualTo(0));
        Assert.That(await verifyContext.StreamPayouts.CountAsync(), Is.EqualTo(0));

        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                "blockedcreator@test.com",
                It.Is<string>(subject => subject.Contains("Payout Action Required ($10.00)")),
                It.Is<string>(body => body.Contains("owned or authorized PayPal payout email")
                    && body.Contains("completed W-9 or W-8 tax form"))),
            Times.Once);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(subject => subject.Contains("Creator Payout Blocked ($10.00 USD)")),
                It.Is<string>(body => body.Contains("Blocked Payout Amount")
                    && body.Contains("Gross Stream Earnings"))),
            Times.Once);
        _mockTaxBanditsService.Verify(
            x => x.ReportForm1099TransactionsBatchAsync(It.IsAny<List<Form1099Transaction>>(), default),
            Times.Never);
    }

    [Test]
    public async Task ProcessPendingPayoutsAsync_BlocksPayout_WhenPayPalEmailMalformed()
    {
        var creator = await SeedPayoutEligibleCreatorAsync(payPalReady: true, taxReady: true);
        creator.PayPalEmail = "@angelaomalley72";
        await _context.SaveChangesAsync();

        Assert.That(creator.IsFullyOnboarded, Is.False);

        var processed = await _service.ProcessPendingPayoutsAsync();

        Assert.That(processed, Is.EqualTo(0));

        await using var verifyContext = new AppDbContext(_contextOptions);
        Assert.That(await verifyContext.StreamPayouts.CountAsync(), Is.EqualTo(0));

        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                "blockedcreator@test.com",
                It.Is<string>(subject => subject.Contains("Payout Action Required ($10.00)")),
                It.Is<string>(body => body.Contains("owned or authorized PayPal payout email"))),
            Times.Once);
    }

    [Test]
    public async Task ProcessPendingPayoutsAsync_BlocksTipOnlyPayoutAndLeavesTipsCleared_WhenRequirementsIncomplete()
    {
        await SeedPayoutEligibleCreatorAsync(payPalReady: false, taxReady: false, includeStreams: false);
        var clearedTips = new List<Tip>
        {
            new()
            {
                Id = 200,
                CreatorId = 100,
                TipperUserId = 999,
                Amount = 8.00m,
                Status = TipStatus.Cleared
            }
        };
        _mockTipService.Setup(x => x.GetClearedTipsForPayoutAsync(100))
            .ReturnsAsync(clearedTips);

        var processed = await _service.ProcessPendingPayoutsAsync();

        Assert.That(processed, Is.EqualTo(0));
        _mockTipService.Verify(x => x.MarkTipsAsPaidAsync(It.IsAny<List<int>>(), It.IsAny<string>()), Times.Never);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                "blockedcreator@test.com",
                It.Is<string>(subject => subject.Contains("Payout Action Required ($8.00)")),
                It.IsAny<string>()),
            Times.Once);
        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                AdminNotificationService.AdminEmail,
                It.Is<string>(subject => subject.Contains("Creator Payout Blocked ($8.00 USD)")),
                It.Is<string>(body => body.Contains("Gross Tip Earnings"))),
            Times.Once);
    }

    [Test]
    public async Task ProcessPendingPayoutsAsync_BlocksPayout_WhenCreatorRoleMissing()
    {
        await SeedPayoutEligibleCreatorAsync(payPalReady: true, taxReady: true, assignRole: false);

        var processed = await _service.ProcessPendingPayoutsAsync();

        Assert.That(processed, Is.EqualTo(0));

        await using var verifyContext = new AppDbContext(_contextOptions);
        Assert.That(await verifyContext.StreamPayouts.CountAsync(), Is.EqualTo(0));

        _mockEmailService.Verify(
            x => x.SendEmailAsync(
                "blockedcreator@test.com",
                It.IsAny<string>(),
                It.Is<string>(body => body.Contains("Creator role"))),
            Times.Once);
    }

    [Test]
    public async Task RetryPending1099_IncludesTipAmountInGrossAmount()
    {
        // Arrange - payout with $10 streams + $5 tips
        await SeedCreatorWithPayouts(tipAmount: 5.00m);

        _mockAppSettingsService.Setup(x => x.IsTaxBanditsMaintenanceActiveAsync())
            .ReturnsAsync(false);

        Form1099Transaction? capturedTransaction = null;
        _mockTaxBanditsService
            .Setup(x => x.ReportForm1099TransactionsBatchAsync(It.IsAny<List<Form1099Transaction>>(), default))
            .Callback<List<Form1099Transaction>, CancellationToken>((txns, _) => capturedTransaction = txns.FirstOrDefault())
            .ReturnsAsync(new Form1099TransactionResponse { Success = true, TransactionId = "TXN-123" });

        // Act
        var updated = await _service.RetryPending1099TransactionsAsync();

        // Assert - GrossAmount should include tip amount (10 + 5 = 15)
        Assert.That(capturedTransaction, Is.Not.Null);
        Assert.That(capturedTransaction!.GrossAmount, Is.EqualTo(15.00m)); // GrossAmount + TipAmount
        Assert.That(updated, Is.GreaterThan(0));
    }

    [Test]
    public async Task RetryPending1099_WithoutTips_UsesOnlyGrossAmount()
    {
        // Arrange - payout with only streams, no tips
        await SeedCreatorWithPayouts(tipAmount: 0m);

        _mockAppSettingsService.Setup(x => x.IsTaxBanditsMaintenanceActiveAsync())
            .ReturnsAsync(false);

        Form1099Transaction? capturedTransaction = null;
        _mockTaxBanditsService
            .Setup(x => x.ReportForm1099TransactionsBatchAsync(It.IsAny<List<Form1099Transaction>>(), default))
            .Callback<List<Form1099Transaction>, CancellationToken>((txns, _) => capturedTransaction = txns.FirstOrDefault())
            .ReturnsAsync(new Form1099TransactionResponse { Success = true, TransactionId = "TXN-456" });

        // Act
        var updated = await _service.RetryPending1099TransactionsAsync();

        // Assert - GrossAmount should be just the stream amount
        Assert.That(capturedTransaction, Is.Not.Null);
        Assert.That(capturedTransaction!.GrossAmount, Is.EqualTo(10.00m));
        Assert.That(updated, Is.GreaterThan(0));
    }

    [Test]
    public async Task RetryPending1099_SkipsDuringMaintenance()
    {
        // Arrange
        await SeedCreatorWithPayouts(tipAmount: 5.00m);

        _mockAppSettingsService.Setup(x => x.IsTaxBanditsMaintenanceActiveAsync())
            .ReturnsAsync(true);

        // Act
        var updated = await _service.RetryPending1099TransactionsAsync();

        // Assert
        Assert.That(updated, Is.EqualTo(0));
        _mockTaxBanditsService.Verify(
            x => x.ReportForm1099TransactionsBatchAsync(It.IsAny<List<Form1099Transaction>>(), default),
            Times.Never);
    }

    [Test]
    public async Task RetryPending1099_NoPendingPayouts_ReturnsZero()
    {
        // Arrange - no payouts in database
        _mockAppSettingsService.Setup(x => x.IsTaxBanditsMaintenanceActiveAsync())
            .ReturnsAsync(false);

        // Act
        var updated = await _service.RetryPending1099TransactionsAsync();

        // Assert
        Assert.That(updated, Is.EqualTo(0));
        _mockTaxBanditsService.Verify(
            x => x.ReportForm1099TransactionsBatchAsync(It.IsAny<List<Form1099Transaction>>(), default),
            Times.Never);
    }

    [Test]
    public async Task RetryPending1099_FailedSubmission_KeepsPendingStatus()
    {
        // Arrange
        await SeedCreatorWithPayouts(tipAmount: 3.00m);

        _mockAppSettingsService.Setup(x => x.IsTaxBanditsMaintenanceActiveAsync())
            .ReturnsAsync(false);

        _mockTaxBanditsService
            .Setup(x => x.ReportForm1099TransactionsBatchAsync(It.IsAny<List<Form1099Transaction>>(), default))
            .ReturnsAsync(new Form1099TransactionResponse { Success = false, ErrorMessage = "Service unavailable" });

        // Act
        var updated = await _service.RetryPending1099TransactionsAsync();

        // Assert - no payouts should be marked as updated
        Assert.That(updated, Is.EqualTo(0));

        // Verify payout still has Pending status
        using var verifyContext = new AppDbContext(_contextOptions);
        var payout = await verifyContext.StreamPayouts.FirstAsync();
        Assert.That(payout.TaxBanditsStatus, Is.EqualTo("Pending"));
    }

    // ==================== GetAllPayoutsAsync Tests ====================

    [Test]
    public async Task GetAllPayoutsAsync_ReturnsAllPayoutsWithNavigationProperties()
    {
        // Arrange
        await SeedCreatorWithPayouts(tipAmount: 7.50m);

        // Act
        var payouts = await _service.GetAllPayoutsAsync();

        // Assert
        Assert.That(payouts, Has.Count.EqualTo(1));
        Assert.That(payouts[0].TipAmount, Is.EqualTo(7.50m));
        Assert.That(payouts[0].GrossAmount, Is.EqualTo(10.00m));
        Assert.That(payouts[0].Creator, Is.Not.Null);
        Assert.That(payouts[0].SongMetadata, Is.Not.Null);
    }

    // ==================== StreamPayout TipAmount Model Tests ====================

    [Test]
    public void StreamPayout_TipAmount_DefaultsToZero()
    {
        // Act
        var payout = new StreamPayout();

        // Assert
        Assert.That(payout.TipAmount, Is.EqualTo(0m));
    }

    [Test]
    public void StreamPayout_TipAmount_CanBeSet()
    {
        // Act
        var payout = new StreamPayout { TipAmount = 25.50m };

        // Assert
        Assert.That(payout.TipAmount, Is.EqualTo(25.50m));
    }
}
