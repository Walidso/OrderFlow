using MediatR;
using Microsoft.EntityFrameworkCore;
using OrderService.Application.Abstractions;
using OrderService.Application.Common.Exceptions;
using OrderService.Domain.Entities;

namespace OrderService.Application.Auth.Commands.Register;

public sealed class RegisterCommandHandler : IRequestHandler<RegisterCommand, AuthResultDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenGenerator _jwt;

    public RegisterCommandHandler(
        IApplicationDbContext db, IPasswordHasher hasher, IJwtTokenGenerator jwt)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
    }

    public async Task<AuthResultDto> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        // AnyAsync stops at the first match — cheaper than Count() > 0.
        // (Same reason .Any() beats .Count() > 0 in plain LINQ.)
        var exists = await _db.Users.AnyAsync(u => u.Email == email, cancellationToken);
        if (exists)
            throw new ConflictException($"A user with email '{email}' already exists.");
        // Race-condition note: two simultaneous registrations could both pass
        // this check — the UNIQUE index on Email (see the migration) is the
        // real guarantee. The check just gives a friendlier error first.

        var user = User.Create(email, _hasher.Hash(request.Password));

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        // Log the user straight in — no separate login round trip needed.
        return new AuthResultDto(_jwt.GenerateToken(user.Id, user.Email));
    }
}
