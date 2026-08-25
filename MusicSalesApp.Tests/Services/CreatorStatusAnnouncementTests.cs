using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

/// <summary>
/// The one-shot claim behind the "you are a creator" notice.
///
/// <para>
/// On SQLite rather than the InMemory provider the rest of <see cref="CreatorServiceTests"/> uses,
/// because the claim is a single conditional UPDATE and InMemory does not implement
/// <c>ExecuteUpdate</c> at all. Testing it there would prove nothing about the statement that
/// actually runs.
/// </para>
/// </summary>
[TestFixture]
public class CreatorStatusAnnouncementTests
{
    private SqliteConnection _connection = default!;
    private IDbContextFactory<AppDbContext> _contextFactory = default!;
    private AppDbContext _context = default!;
    private CreatorService _service = default!;

    [SetUp]
    public void Setup()
    {
        // Held open for the fixture's lifetime: an in-memory SQLite database exists only as long
        // as a connection to it does.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _contextFactory = new SqliteContextFactory(options);
        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        var userManager = new Mock<UserManager<ApplicationUser>>(
            new Mock<IUserStore<ApplicationUser>>().Object,
            null!, null!, null!, null!, null!, null!, null!, null!);

        var personaService = new Mock<ICreatorPersonaService>();
        personaService
            .Setup(x => x.DeleteAllPersonasForCreatorAsync(It.IsAny<int>()))
            .ReturnsAsync(0);

        _service = new CreatorService(
            _contextFactory,
            new Mock<IAzureStorageService>().Object,
            userManager.Object,
            new Mock<IConfiguration>().Object,
            new Mock<ILogger<CreatorService>>().Object,
            new Mock<IAppSettingsService>().Object,
            new Mock<IAdminNotificationService>().Object,
            personaService.Object,
            new Mock<ICreatorEmailService>().Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task ActivationClaim_SucceedsOnce_ThenNeverAgain()
    {
        // Whoever wins this spends it on a Google Ads conversion, a funnel event and a permanent
        // user-history row, so a second true is not a cosmetic bug. It used to be driven by
        // ?creator_activated=true, and reloading the page fired all three again.
        var creator = await AddCreatorAsync(activationAnnouncedAt: null);

        var first = await _service.TryClaimActivationAnnouncementAsync(creator.Id);
        var second = await _service.TryClaimActivationAnnouncementAsync(creator.Id);
        var third = await _service.TryClaimActivationAnnouncementAsync(creator.Id);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.False);
            Assert.That(third, Is.False);
        });
    }

    [Test]
    public async Task ActivationClaim_ConcurrentCallers_ProduceExactlyOneWinner()
    {
        // Two tabs, or a double-submit. The null check is inside the UPDATE rather than a read
        // followed by a write, which is what makes this safe.
        var creator = await AddCreatorAsync(activationAnnouncedAt: null);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => _service.TryClaimActivationAnnouncementAsync(creator.Id)));

        Assert.That(results.Count(won => won), Is.EqualTo(1));
    }

    [Test]
    public async Task ActivationClaim_ReturnsFalse_WhenAlreadyAnnounced()
    {
        var creator = await AddCreatorAsync(activationAnnouncedAt: DateTime.UtcNow.AddDays(-3));

        Assert.That(await _service.TryClaimActivationAnnouncementAsync(creator.Id), Is.False);
    }

    [Test]
    public async Task ActivationClaim_ReturnsFalse_ForACreatorThatDoesNotExist()
    {
        Assert.That(await _service.TryClaimActivationAnnouncementAsync(9999), Is.False);
    }

    [Test]
    public async Task DeactivationClaim_IsIndependentOfTheActivationOne()
    {
        // They are separate one-shots. Stopping and later restarting has to be able to announce
        // both, in order, without either consuming the other.
        var creator = await AddCreatorAsync(activationAnnouncedAt: null);

        var activation = await _service.TryClaimActivationAnnouncementAsync(creator.Id);
        var deactivation = await _service.TryClaimDeactivationAnnouncementAsync(creator.Id);

        Assert.Multiple(() =>
        {
            Assert.That(activation, Is.True);
            Assert.That(deactivation, Is.True);
        });
    }

    [Test]
    public async Task ActivateCreatorAsync_RearmsTheClaim_SoAReturningCreatorIsToldAgain()
    {
        var creator = await AddCreatorAsync(activationAnnouncedAt: DateTime.UtcNow.AddDays(-30));

        await _service.ActivateCreatorAsync(creator.Id);

        Assert.That(await _service.TryClaimActivationAnnouncementAsync(creator.Id), Is.True);
    }

    [Test]
    public async Task ActivateCreatorAsync_LeavesOnboardedAtAlone()
    {
        // Re-activating a dormant creator must not restamp when they onboarded.
        // StartOnboardingAsync stamps OnboardedAt via ResetCreatorOnboardingAsync and then calls
        // ActivateCreatorAsync, so writing it here too would overwrite the real date with today
        // every time CompleteOnboardingAsync or the admin activate endpoint revived an account.
        var onboarded = new DateTime(2026, 2, 15, 17, 32, 43, DateTimeKind.Utc);
        var creator = await AddCreatorAsync(activationAnnouncedAt: DateTime.UtcNow.AddDays(-30));

        await using (var seed = await _contextFactory.CreateDbContextAsync())
        {
            var row = await seed.Creators.SingleAsync(c => c.Id == creator.Id);
            row.OnboardedAt = onboarded;
            row.IsActive = false;
            await seed.SaveChangesAsync();
        }

        await _service.ActivateCreatorAsync(creator.Id);

        await using var verify = await _contextFactory.CreateDbContextAsync();
        var saved = await verify.Creators.SingleAsync(c => c.Id == creator.Id);

        Assert.Multiple(() =>
        {
            Assert.That(saved.IsActive, Is.True);
            Assert.That(saved.OnboardedAt, Is.EqualTo(onboarded));
        });
    }

    private async Task<Creator> AddCreatorAsync(DateTime? activationAnnouncedAt)
    {
        var user = new ApplicationUser
        {
            UserName = $"claim{Guid.NewGuid():N}@test.com",
            Email = "claim@test.com",
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var creator = new Creator
        {
            UserId = user.Id,
            IsActive = true,
            OnboardingStatus = CreatorOnboardingStatus.Completed,
            CreatorAgreementAccepted = true,
            ActivationAnnouncedAt = activationAnnouncedAt,
        };
        _context.Creators.Add(creator);
        await _context.SaveChangesAsync();

        return creator;
    }

    private sealed class SqliteContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public SqliteContextFactory(DbContextOptions<AppDbContext> options) => _options = options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppDbContext(_options));
    }
}
