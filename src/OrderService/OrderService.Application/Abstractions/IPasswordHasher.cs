namespace OrderService.Application.Abstractions;

/// <summary>Hashes and verifies passwords. Implementation: PBKDF2 (Infrastructure).</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string storedHash);
}
