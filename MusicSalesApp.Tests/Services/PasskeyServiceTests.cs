using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MusicSalesApp.Data;
using MusicSalesApp.Models;
using MusicSalesApp.Services;

namespace MusicSalesApp.Tests.Services;

[TestFixture]
public class PasskeyServiceTests
{
    private Mock<IFido2> _mockFido2;
    private Mock<UserManager<ApplicationUser>> _mockUserManager;
    private Mock<ILogger<PasskeyService>> _mockLogger;
    private DbContextOptions<AppDbContext> _dbOptions;
    private AppDbContext _context;
    private PasskeyService _service;

    [SetUp]
    public void SetUp()
    {
        _mockFido2 = new Mock<IFido2>();
        _mockLogger = new Mock<ILogger<PasskeyService>>();

        var store = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null, null, null, null, null, null, null, null);

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"PasskeyTestDb_{Guid.NewGuid()}")
            .Options;

        _context = new AppDbContext(_dbOptions);
        _service = new PasskeyService(_mockFido2.Object, _context, _mockUserManager.Object, _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    #region BeginRegistrationAsync Tests

    [Test]
    public async Task BeginRegistrationAsync_UserNotFound_ThrowsInvalidOperationException()
    {
        _mockUserManager.Setup(m => m.FindByIdAsync("1"))
            .ReturnsAsync((ApplicationUser)null);

        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _service.BeginRegistrationAsync(1, "TestKey"));
    }

    [Test]
    public async Task BeginRegistrationAsync_ValidUser_CallsRequestNewCredential()
    {
        var user = new ApplicationUser { Id = 1, UserName = "testuser" };
        _mockUserManager.Setup(m => m.FindByIdAsync("1")).ReturnsAsync(user);

        var expectedOptions = new CredentialCreateOptions
        {
            Challenge = new byte[32],
            Rp = new PublicKeyCredentialRpEntity("example.com", "Example", null),
            User = new Fido2User { DisplayName = "testuser", Name = "testuser", Id = BitConverter.GetBytes(1) },
            PubKeyCredParams = new List<PubKeyCredParam>
            {
                new PubKeyCredParam(COSE.Algorithm.ES256)
            }
        };

        _mockFido2.Setup(f => f.RequestNewCredential(It.IsAny<RequestNewCredentialParams>()))
            .Returns(expectedOptions);

        var result = await _service.BeginRegistrationAsync(1, "TestKey");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Timeout, Is.EqualTo(180000));
        _mockFido2.Verify(f => f.RequestNewCredential(It.Is<RequestNewCredentialParams>(p =>
            p.User.Name == "testuser" &&
            p.AttestationPreference == AttestationConveyancePreference.None &&
            p.AuthenticatorSelection.ResidentKey == ResidentKeyRequirement.Discouraged
        )), Times.Once);
    }

    #endregion

    #region GetUserPasskeysAsync Tests

    [Test]
    public async Task GetUserPasskeysAsync_ReturnsEmpty_WhenNoPasskeys()
    {
        var result = await _service.GetUserPasskeysAsync(999);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetUserPasskeysAsync_ReturnsPasskeys_OrderedByCreatedAtDescending()
    {
        var user = new ApplicationUser { Id = 1, UserName = "testuser", Email = "test@test.com" };
        _context.Users.Add(user);

        var passkey1 = new Passkey
        {
            UserId = 1,
            Name = "Key1",
            CredentialId = new byte[] { 1, 2, 3 },
            PublicKey = new byte[] { 4, 5, 6 },
            AttestationObject = new byte[] { 7, 8, 9 },
            ClientDataJSON = new byte[] { 10, 11, 12 },
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        };
        var passkey2 = new Passkey
        {
            UserId = 1,
            Name = "Key2",
            CredentialId = new byte[] { 13, 14, 15 },
            PublicKey = new byte[] { 16, 17, 18 },
            AttestationObject = new byte[] { 19, 20, 21 },
            ClientDataJSON = new byte[] { 22, 23, 24 },
            CreatedAt = DateTime.UtcNow
        };
        _context.Passkeys.AddRange(passkey1, passkey2);
        await _context.SaveChangesAsync();

        var result = await _service.GetUserPasskeysAsync(1);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].Name, Is.EqualTo("Key2")); // Most recent first
        Assert.That(result[1].Name, Is.EqualTo("Key1"));
    }

    #endregion

    #region DeletePasskeyAsync Tests

    [Test]
    public async Task DeletePasskeyAsync_PasskeyNotFound_ReturnsFalse()
    {
        var result = await _service.DeletePasskeyAsync(1, 999);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task DeletePasskeyAsync_WrongUser_ReturnsFalse()
    {
        var user = new ApplicationUser { Id = 1, UserName = "testuser", Email = "test@test.com" };
        _context.Users.Add(user);
        _context.Passkeys.Add(new Passkey
        {
            UserId = 1,
            Name = "Key1",
            CredentialId = new byte[] { 1, 2, 3 },
            PublicKey = new byte[] { 4, 5, 6 },
            AttestationObject = new byte[] { 7, 8, 9 },
            ClientDataJSON = new byte[] { 10, 11, 12 }
        });
        await _context.SaveChangesAsync();

        var result = await _service.DeletePasskeyAsync(999, 1);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task DeletePasskeyAsync_ValidPasskey_DeletesAndReturnsTrue()
    {
        var user = new ApplicationUser { Id = 1, UserName = "testuser", Email = "test@test.com" };
        _context.Users.Add(user);
        var passkey = new Passkey
        {
            UserId = 1,
            Name = "Key1",
            CredentialId = new byte[] { 1, 2, 3 },
            PublicKey = new byte[] { 4, 5, 6 },
            AttestationObject = new byte[] { 7, 8, 9 },
            ClientDataJSON = new byte[] { 10, 11, 12 }
        };
        _context.Passkeys.Add(passkey);
        await _context.SaveChangesAsync();

        var result = await _service.DeletePasskeyAsync(1, passkey.Id);

        Assert.That(result, Is.True);
        Assert.That(await _context.Passkeys.CountAsync(), Is.EqualTo(0));
    }

    #endregion

    #region RenamePasskeyAsync Tests

    [Test]
    public async Task RenamePasskeyAsync_PasskeyNotFound_ReturnsFalse()
    {
        var result = await _service.RenamePasskeyAsync(1, 999, "NewName");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task RenamePasskeyAsync_WrongUser_ReturnsFalse()
    {
        var user = new ApplicationUser { Id = 1, UserName = "testuser", Email = "test@test.com" };
        _context.Users.Add(user);
        _context.Passkeys.Add(new Passkey
        {
            UserId = 1,
            Name = "Key1",
            CredentialId = new byte[] { 1, 2, 3 },
            PublicKey = new byte[] { 4, 5, 6 },
            AttestationObject = new byte[] { 7, 8, 9 },
            ClientDataJSON = new byte[] { 10, 11, 12 }
        });
        await _context.SaveChangesAsync();

        var result = await _service.RenamePasskeyAsync(999, 1, "NewName");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task RenamePasskeyAsync_ValidPasskey_RenamesAndReturnsTrue()
    {
        var user = new ApplicationUser { Id = 1, UserName = "testuser", Email = "test@test.com" };
        _context.Users.Add(user);
        var passkey = new Passkey
        {
            UserId = 1,
            Name = "OldName",
            CredentialId = new byte[] { 1, 2, 3 },
            PublicKey = new byte[] { 4, 5, 6 },
            AttestationObject = new byte[] { 7, 8, 9 },
            ClientDataJSON = new byte[] { 10, 11, 12 }
        };
        _context.Passkeys.Add(passkey);
        await _context.SaveChangesAsync();

        var result = await _service.RenamePasskeyAsync(1, passkey.Id, "NewName");

        Assert.That(result, Is.True);
        var updated = await _context.Passkeys.FindAsync(passkey.Id);
        Assert.That(updated.Name, Is.EqualTo("NewName"));
    }

    #endregion

    #region BeginLoginAsync Tests

    [Test]
    public async Task BeginLoginAsync_UserNotFound_ReturnsEmptyOptions()
    {
        _mockUserManager.Setup(m => m.FindByNameAsync("unknown")).ReturnsAsync((ApplicationUser)null);
        _mockUserManager.Setup(m => m.FindByEmailAsync("unknown")).ReturnsAsync((ApplicationUser)null);

        var result = await _service.BeginLoginAsync("unknown");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.AllowCredentials, Is.Empty);
    }

    [Test]
    public async Task BeginLoginAsync_UserHasNoPasskeys_ThrowsInvalidOperationException()
    {
        var user = new ApplicationUser { Id = 1, UserName = "testuser", Email = "test@test.com" };
        _mockUserManager.Setup(m => m.FindByNameAsync("testuser")).ReturnsAsync(user);

        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _service.BeginLoginAsync("testuser"));
    }

    [Test]
    public async Task BeginLoginAsync_UserHasPasskeys_CallsGetAssertionOptions()
    {
        var user = new ApplicationUser { Id = 1, UserName = "testuser", Email = "test@test.com" };
        _context.Users.Add(user);
        _context.Passkeys.Add(new Passkey
        {
            UserId = 1,
            Name = "Key1",
            CredentialId = new byte[] { 1, 2, 3 },
            PublicKey = new byte[] { 4, 5, 6 },
            AttestationObject = new byte[] { 7, 8, 9 },
            ClientDataJSON = new byte[] { 10, 11, 12 }
        });
        await _context.SaveChangesAsync();

        _mockUserManager.Setup(m => m.FindByNameAsync("testuser")).ReturnsAsync(user);

        var expectedOptions = new AssertionOptions
        {
            Challenge = new byte[32],
            RpId = "example.com",
            AllowCredentials = new List<PublicKeyCredentialDescriptor>
            {
                new PublicKeyCredentialDescriptor(new byte[] { 1, 2, 3 })
            }
        };

        _mockFido2.Setup(f => f.GetAssertionOptions(It.IsAny<GetAssertionOptionsParams>()))
            .Returns(expectedOptions);

        var result = await _service.BeginLoginAsync("testuser");

        Assert.That(result, Is.Not.Null);
        _mockFido2.Verify(f => f.GetAssertionOptions(It.Is<GetAssertionOptionsParams>(p =>
            p.UserVerification == UserVerificationRequirement.Preferred
        )), Times.Once);
    }

    #endregion
}
