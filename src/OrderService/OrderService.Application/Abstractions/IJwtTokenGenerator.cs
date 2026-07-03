namespace OrderService.Application.Abstractions;

/// <summary>
/// Creates a signed JWT for an authenticated user.
/// Lives in Application as an interface because login/register handlers need
/// it, but the crypto details (keys, signing algorithms) are Infrastructure.
/// </summary>
public interface IJwtTokenGenerator
{
    string GenerateToken(Guid userId, string email);
}
