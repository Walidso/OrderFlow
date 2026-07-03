namespace OrderService.Infrastructure.Auth;

/// <summary>
/// Strongly-typed view of the "Jwt" section in appsettings.json
/// (the Options pattern). Beats sprinkling config["Jwt:Key"] strings around.
/// </summary>
public sealed class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Key { get; init; } = string.Empty;
    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public int ExpiryMinutes { get; init; } = 60;
}
