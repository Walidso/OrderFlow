using NSubstitute;
using OrderService.Application.Abstractions;
using OrderService.Application.Auth.Commands.Login;
using OrderService.Application.Common.Exceptions;
using OrderService.Domain.Entities;
using Xunit;

namespace OrderService.UnitTests;

public class LoginCommandHandlerTests
{
    [Fact]
    public async Task Handle_CorrectCredentials_ReturnsToken()
    {
        await using var db = TestDbContextFactory.Create();
        var hasher = Substitute.For<IPasswordHasher>();
        var jwt = Substitute.For<IJwtTokenGenerator>();

        db.Users.Add(User.Create("walid@example.se", "stored-hash"));
        await db.SaveChangesAsync();

        // Only the exact right combination verifies successfully:
        hasher.Verify("Secret123!", "stored-hash").Returns(true);
        jwt.GenerateToken(Arg.Any<Guid>(), "walid@example.se").Returns("fake.jwt.token");

        var handler = new LoginCommandHandler(db, hasher, jwt);
        var result = await handler.Handle(
            new LoginCommand("walid@example.se", "Secret123!"), CancellationToken.None);

        Assert.Equal("fake.jwt.token", result.Token);
    }

    [Fact]
    public async Task Handle_WrongPassword_ThrowsInvalidCredentials()
    {
        await using var db = TestDbContextFactory.Create();
        var hasher = Substitute.For<IPasswordHasher>();

        db.Users.Add(User.Create("walid@example.se", "stored-hash"));
        await db.SaveChangesAsync();

        hasher.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // wrong password

        var handler = new LoginCommandHandler(db, hasher, Substitute.For<IJwtTokenGenerator>());

        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            handler.Handle(new LoginCommand("walid@example.se", "wrong"), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_UnknownEmail_ThrowsInvalidCredentials()
    {
        await using var db = TestDbContextFactory.Create(); // empty database
        var handler = new LoginCommandHandler(
            db, Substitute.For<IPasswordHasher>(), Substitute.For<IJwtTokenGenerator>());

        // Note: SAME exception type as wrong-password — deliberately, so the
        // API response can't be used to discover which emails exist.
        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            handler.Handle(new LoginCommand("ghost@example.se", "whatever"), CancellationToken.None));
    }
}
