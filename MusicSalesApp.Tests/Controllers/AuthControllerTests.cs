using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using MusicSalesApp.Controllers;

namespace MusicSalesApp.Tests.Controllers;

[TestFixture]
public class AuthControllerTests
{
    private Mock<IConfiguration> _mockConfiguration;
    private AuthController _controller;

    [SetUp]
    public void SetUp()
    {
        _mockConfiguration = new Mock<IConfiguration>();
        _mockConfiguration.Setup(c => c.GetSection("Auth:ExpireMinutes").Value).Returns("300");
        
        _controller = new AuthController(_mockConfiguration.Object);
    }

    [Test]
    public void Login_WithEmptyUsername_ReturnsBadRequest()
    {
        // Arrange
        var request = new LoginRequest { Username = "", Password = "password" };

        // Act
        var result = _controller.Login(request).Result;

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public void Login_WithEmptyPassword_ReturnsBadRequest()
    {
        // Arrange
        var request = new LoginRequest { Username = "admin", Password = "" };

        // Act
        var result = _controller.Login(request).Result;

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public void Login_WithNullUsername_ReturnsBadRequest()
    {
        // Arrange
        var request = new LoginRequest { Username = null, Password = "password" };

        // Act
        var result = _controller.Login(request).Result;

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public void Login_WithNullPassword_ReturnsBadRequest()
    {
        // Arrange
        var request = new LoginRequest { Username = "admin", Password = null };

        // Act
        var result = _controller.Login(request).Result;

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public void LoginRequest_DefaultConstructor_InitializesProperties()
    {
        // Act
        var request = new LoginRequest();

        // Assert
        Assert.That(request.Username, Is.EqualTo(string.Empty));
        Assert.That(request.Password, Is.EqualTo(string.Empty));
    }
}
