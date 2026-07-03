namespace OrderService.Application.Auth;

/// <summary>What the client gets back after register/login: a signed JWT.</summary>
public record AuthResultDto(string Token);
