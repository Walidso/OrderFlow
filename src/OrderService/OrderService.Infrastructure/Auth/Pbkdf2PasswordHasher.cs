using System.Security.Cryptography;
using OrderService.Application.Abstractions;

namespace OrderService.Infrastructure.Auth;

/// <summary>
/// Password hashing with PBKDF2 (built into .NET — no extra packages).
///
/// WHY not SHA256(password)? Plain hashes are fast, and fast is BAD for
/// passwords: an attacker with a leaked table can try billions of guesses
/// per second. PBKDF2 runs the hash 100,000 times ("key stretching") to
/// make each guess deliberately expensive.
///
/// WHY a salt? A random value mixed into every hash so two users with the
/// same password get different hashes, and precomputed "rainbow tables"
/// become useless.
///
/// Stored format: "{iterations}.{saltBase64}.{hashBase64}"
/// Storing the iteration count with the hash lets us raise it later without
/// breaking existing users (old hashes still verify with their own count).
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);

        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);

        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string storedHash)
    {
        var parts = storedHash.Split('.');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], out var iterations)) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // FixedTimeEquals compares in constant time, so an attacker can't
        // measure "how far" the comparison got (a timing side-channel).
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
