using Microsoft.EntityFrameworkCore;
using NSubstitute;
using OrderService.Application.Abstractions;
using OrderService.Application.Auth.Commands.Register;
using OrderService.Application.Common.Exceptions;
using OrderService.Domain.Entities;
using Xunit;

namespace OrderService.UnitTests;

public class RegisterCommandHandlerTests
{
    // Shared helpers keep each test's Arrange section short and readable.
    private static (RegisterCommandHandler handler,
                    Infrastructure.Persistence.OrderDbContext db,
                    IPasswordHasher hasher,
                    IJwtTokenGenerator jwt) CreateSut()
    {
        var db = TestDbContextFactory.Create();
        var hasher = Substitute.For<IPasswordHasher>();
        var jwt = Substitute.For<IJwtTokenGenerator>();

        // Program the mocks: "when Hash is called with anything, return this".
        hasher.Hash(Arg.Any<string>()).Returns("hashed-password");
        jwt.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>()).Returns("fake.jwt.token");

        return (new RegisterCommandHandler(db, hasher, jwt), db, hasher, jwt);
    }

    [Fact]
    public async Task Handle_NewEmail_CreatesUserAndReturnsToken()
    {
        var (handler, db, _, _) = CreateSut();
        await using var _db = db;

        var result = await handler.Handle(
            new RegisterCommand("walid@example.se", "Secret123!"), CancellationToken.None);

        Assert.Equal("fake.jwt.token", result.Token);
        var user = await db.Users.SingleAsync();
        Assert.Equal("walid@example.se", user.Email);
        // We verify the STORED value is the hash, never the raw password —
        // the single most important assertion in this file.
        Assert.Equal("hashed-password", user.PasswordHash);
    }

    [Fact]
    public async Task Handle_UppercaseEmail_IsNormalizedToLowercase()
    {
        var (handler, db, _, _) = CreateSut();
        await using var _db = db;

        await handler.Handle(
            new RegisterCommand("WALID@Example.SE", "Secret123!"), CancellationToken.None);

        var user = await db.Users.SingleAsync();
        Assert.Equal("walid@example.se", user.Email); // "same email, different case" = same account
    }

    [Fact]
    public async Task Handle_DuplicateEmail_ThrowsConflictException()
    {
        var (handler, db, _, _) = CreateSut();
        await using var _db = db;

        db.Users.Add(User.Create("walid@example.se", "already-hashed"));
        await db.SaveChangesAsync();

        // Assert.ThrowsAsync = "this call MUST throw exactly this type".
        // The middleware later turns ConflictException into HTTP 409 —
        // but that mapping belongs to the integration tests, not here.
        await Assert.ThrowsAsync<ConflictException>(() =>
            handler.Handle(new RegisterCommand("walid@example.se", "Secret123!"),
                CancellationToken.None));
    }
}
