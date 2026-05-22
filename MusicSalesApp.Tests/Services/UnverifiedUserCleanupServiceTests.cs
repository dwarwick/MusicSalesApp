using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Common.Helpers;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class UnverifiedUserCleanupServiceTests
{
    private Mock<IDbContextFactory<AppDbContext>> _mockContextFactory;
    private Mock<IAccountDeletionService> _mockAccountDeletionService;
    private Mock<ILogger<UnverifiedUserCleanupService>> _mockLogger;
    private UnverifiedUserCleanupService _service;
    private AppDbContext _context;
    private DbContextOptions<AppDbContext> _contextOptions;

    [SetUp]
    public void SetUp()
    {
        _contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"UnverifiedUserCleanupTestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_contextOptions);

        _mockContextFactory = new Mock<IDbContextFactory<AppDbContext>>();
        _mockContextFactory.Setup(factory => factory.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(_contextOptions));

        _mockAccountDeletionService = new Mock<IAccountDeletionService>();
        _mockLogger = new Mock<ILogger<UnverifiedUserCleanupService>>();

        _service = new UnverifiedUserCleanupService(
            _mockContextFactory.Object,
            _mockAccountDeletionService.Object,
            _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task DeleteStaleUnverifiedUsersAsync_StaleNonValidatedUser_DeletesAccount()
    {
        var user = await CreateUserAsync(emailConfirmed: false, registeredAt: DateTime.UtcNow.AddDays(-8));
        _mockAccountDeletionService
            .Setup(service => service.DeleteAccountAsync(It.Is<ApplicationUser>(candidate => candidate.Id == user.Id)))
            .ReturnsAsync(IdentityResult.Success);

        var result = await _service.DeleteStaleUnverifiedUsersAsync();

        Assert.That(result, Is.EqualTo(1));
        _mockAccountDeletionService.Verify(
            service => service.DeleteAccountAsync(It.Is<ApplicationUser>(candidate => candidate.Id == user.Id)),
            Times.Once);
    }

    [Test]
    public async Task DeleteStaleUnverifiedUsersAsync_RecentlyRegisteredUser_DoesNotDeleteAccount()
    {
        await CreateUserAsync(emailConfirmed: false, registeredAt: DateTime.UtcNow.AddDays(-6));

        var result = await _service.DeleteStaleUnverifiedUsersAsync();

        Assert.That(result, Is.EqualTo(0));
        _mockAccountDeletionService.Verify(
            service => service.DeleteAccountAsync(It.IsAny<ApplicationUser>()),
            Times.Never);
    }

    [Test]
    public async Task DeleteStaleUnverifiedUsersAsync_UserWithoutRegistrationHistory_DoesNotDeleteAccount()
    {
        await CreateUserAsync(emailConfirmed: false, registeredAt: null);

        var result = await _service.DeleteStaleUnverifiedUsersAsync();

        Assert.That(result, Is.EqualTo(0));
        _mockAccountDeletionService.Verify(
            service => service.DeleteAccountAsync(It.IsAny<ApplicationUser>()),
            Times.Never);
    }

    private async Task<ApplicationUser> CreateUserAsync(bool emailConfirmed, DateTime? registeredAt)
    {
        var role = new IdentityRole<int>
        {
            Id = 1,
            Name = Roles.NonValidatedUser,
            NormalizedName = Roles.NonValidatedUser.ToUpperInvariant()
        };
        _context.Roles.Add(role);

        var user = new ApplicationUser
        {
            Id = 100 + _context.Users.Count(),
            UserName = $"user{_context.Users.Count()}@test.com",
            Email = $"user{_context.Users.Count()}@test.com",
            EmailConfirmed = emailConfirmed
        };
        _context.Users.Add(user);
        _context.UserRoles.Add(new IdentityUserRole<int> { UserId = user.Id, RoleId = role.Id });

        if (registeredAt.HasValue)
        {
            _context.UserHistories.Add(new UserHistory
            {
                UserId = user.Id,
                UserEmail = user.Email,
                EventType = UserHistoryEventTypes.Registration,
                Description = "Registered user",
                OccurredAt = registeredAt.Value
            });
        }

        await _context.SaveChangesAsync();
        return user;
    }
}