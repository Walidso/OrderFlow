namespace OrderService.Domain.Entities;

/// <summary>
/// A registered user. Deliberately minimal — this project is about backend
/// patterns, not identity management. (In "future improvements" we note that
/// a real system would use ASP.NET Core Identity or an external IdP.)
///
/// SECURITY NOTE: we never store the password itself, only a salted PBKDF2
/// hash (see Pbkdf2PasswordHasher in Infrastructure). If the database leaks,
/// the attacker still has to brute-force every password individually.
/// </summary>
public class User
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    private User() { } // for EF Core

    public static User Create(string email, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.", nameof(email));

        return new User
        {
            Id = Guid.NewGuid(),
            // Normalize casing so "Walid@x.se" and "walid@x.se" are the same
            // account. The unique index on Email then does its job reliably.
            Email = email.Trim().ToLowerInvariant(),
            PasswordHash = passwordHash,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
