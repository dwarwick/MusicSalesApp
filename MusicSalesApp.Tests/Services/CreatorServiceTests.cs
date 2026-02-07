using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class CreatorServiceTests
{
    private Mock<IAzureStorageService> _mockStorageService;
    private Mock<ILogger<CreatorService>> _mockLogger;
    private Mock<IConfiguration> _mockConfiguration;
    private Mock<IAppSettingsService> _mockAppSettingsService;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private IDbContextFactory<AppDbContext> _contextFactory;
    private AppDbContext _context;
    private CreatorService _service;

    [SetUp]
    public void Setup()
    {
        _mockStorageService = new Mock<IAzureStorageService>();
        _mockLogger = new Mock<ILogger<CreatorService>>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockAppSettingsService = new Mock<IAppSettingsService>();
        _mockAppSettingsService.Setup(x => x.GetStreamPayRateAsync()).ReturnsAsync(0.005m);

        var store = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        _contextFactory = new TestDbContextFactory(options);
        _context = new AppDbContext(options);

        _service = new CreatorService(
            _contextFactory,
            _mockStorageService.Object,
            _mockUserManager.Object,
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockAppSettingsService.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    #region ResetCreatorOnboardingAsync Tests

    [Test]
    public async Task ResetCreatorOnboardingAsync_SetsOnboardingStatusToCompleted()
    {
        // Arrange — creator who stopped selling (Suspended)
        var user = new ApplicationUser { UserName = "test@test.com", Email = "test@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Suspended,
            IsActive = false,
            PayPalEmail = "old@paypal.com",
            PayPalAccountAffirmed = false,
            PaymentsReceivable = false,
            PrimaryEmailConfirmed = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ResetCreatorOnboardingAsync(creator.Id, "new@paypal.com", true);

        // Assert
        Assert.That(result.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
        Assert.That(result.PayPalEmail, Is.EqualTo("new@paypal.com"));
        Assert.That(result.PayPalAccountAffirmed, Is.True);
        Assert.That(result.PaymentsReceivable, Is.True);
        Assert.That(result.PrimaryEmailConfirmed, Is.True);
        Assert.That(result.OnboardedAt, Is.Not.Null);

        // Verify persistence
        await using var verifyContext = await _contextFactory.CreateDbContextAsync();
        var saved = await verifyContext.Creators.FindAsync(creator.Id);
        Assert.That(saved!.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed),
            "OnboardingStatus should be Completed in the database after reset");
    }

    [Test]
    public async Task ResetCreatorOnboardingAsync_FromConsentRevoked_SetsCompleted()
    {
        // Arrange — creator whose consent was revoked
        var user = new ApplicationUser { UserName = "revoked@test.com", Email = "revoked@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.ConsentRevoked,
            IsActive = false,
            PayPalAccountAffirmed = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ResetCreatorOnboardingAsync(creator.Id, "new@paypal.com", true);

        // Assert
        Assert.That(result.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
    }

    [Test]
    public async Task ResetCreatorOnboardingAsync_ThrowsForInvalidCreatorId()
    {
        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ResetCreatorOnboardingAsync(9999, "test@paypal.com", true));
    }

    #endregion

    #region ActivateCreatorAsync Tests

    [Test]
    public async Task ActivateCreatorAsync_SetsIsActiveAndOnboardingCompleted()
    {
        // Arrange
        var user = new ApplicationUser { UserName = "activate@test.com", Email = "activate@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            IsActive = false
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ActivateCreatorAsync(creator.Id);

        // Assert
        Assert.That(result.IsActive, Is.True);
        Assert.That(result.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
    }

    #endregion

    #region StopBeingCreatorAsync → Re-signup Full Flow Test

    [Test]
    public async Task FullFlow_StopBeingCreator_ThenReSignup_CreatorCanBeActivated()
    {
        // Arrange — active creator
        var user = new ApplicationUser { UserName = "flow@test.com", Email = "flow@test.com" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            TaxFormStatus = TaxFormStatus.Completed,
            IsActive = true,
            PayPalEmail = "original@paypal.com",
            PayPalAccountAffirmed = true,
            PaymentsReceivable = true,
            PrimaryEmailConfirmed = true
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        // Mock user manager for StopBeingCreatorAsync (it removes Creator role)
        _mockUserManager.Setup(x => x.FindByIdAsync(user.Id.ToString()))
            .ReturnsAsync(user);
        _mockUserManager.Setup(x => x.IsInRoleAsync(user, "Creator"))
            .ReturnsAsync(true);
        _mockUserManager.Setup(x => x.RemoveFromRoleAsync(user, "Creator"))
            .ReturnsAsync(IdentityResult.Success);

        // Step 1: Stop being a creator
        await _service.StopBeingCreatorAsync(user.Id);

        // Verify suspended state
        await using (var ctx1 = await _contextFactory.CreateDbContextAsync())
        {
            var suspended = await ctx1.Creators.FindAsync(creator.Id);
            Assert.That(suspended!.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Suspended));
            Assert.That(suspended.IsActive, Is.False);
            // TaxFormStatus should be preserved
            Assert.That(suspended.TaxFormStatus, Is.EqualTo(TaxFormStatus.Completed));
        }

        // Step 2: Re-signup (ResetCreatorOnboarding)
        await _service.ResetCreatorOnboardingAsync(creator.Id, "new@paypal.com", true);

        // Verify reset state
        await using (var ctx2 = await _contextFactory.CreateDbContextAsync())
        {
            var reset = await ctx2.Creators.FindAsync(creator.Id);
            Assert.That(reset!.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed),
                "OnboardingStatus should be Completed after re-signup");
            Assert.That(reset.PayPalEmail, Is.EqualTo("new@paypal.com"));
            Assert.That(reset.PayPalAccountAffirmed, Is.True);
            // IsActive should still be false — needs ActivateCreatorAsync
            Assert.That(reset.IsActive, Is.False);
        }

        // Step 3: Activate (simulates webhook completing tax form check)
        await _service.ActivateCreatorAsync(creator.Id);

        // Verify final active state
        await using (var ctx3 = await _contextFactory.CreateDbContextAsync())
        {
            var active = await ctx3.Creators.FindAsync(creator.Id);
            Assert.That(active!.IsActive, Is.True);
            Assert.That(active.OnboardingStatus, Is.EqualTo(CreatorOnboardingStatus.Completed));
        }
    }

    #endregion

    private class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext()
        {
            return new AppDbContext(_options);
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AppDbContext(_options));
        }
    }
}
