using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OrderService.Application.Abstractions;

namespace OrderService.Infrastructure.Auth;

/// <summary>
/// Builds the JWT (JSON Web Token) returned by register/login.
///
/// A JWT is three base64url parts: header.payload.signature
///  - header: which algorithm signs it (HS256 here)
///  - payload: the "claims" — statements about the user (id, email, expiry)
///  - signature: HMAC-SHA256 over header+payload using our secret key
///
/// CRUCIAL MENTAL MODEL: the payload is only ENCODED, not encrypted —
/// anyone can read it (paste one into jwt.io). The signature is what makes
/// it trustworthy: change one character of the payload and the signature no
/// longer matches, so the server rejects it. Never put secrets in claims.
/// </summary>
public sealed class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly JwtSettings _settings;

    public JwtTokenGenerator(IOptions<JwtSettings> options) => _settings = options.Value;

    public string GenerateToken(Guid userId, string email)
    {
        var claims = new[]
        {
            // "sub" (subject) = who this token is about. ASP.NET's JWT
            // middleware maps it to ClaimTypes.NameIdentifier by default,
            // which is what the controllers read back.
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
            // "jti" = unique token id; useful for token revocation lists.
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,       // who created the token
            audience: _settings.Audience,   // who it is intended for
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_settings.ExpiryMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
