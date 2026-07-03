using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderService.Api.Contracts;
using OrderService.Application.Auth;
using OrderService.Application.Auth.Commands.Login;
using OrderService.Application.Auth.Commands.Register;

namespace OrderService.Api.Controllers;

/// <summary>
/// Controllers in this project are deliberately THIN: translate HTTP to a
/// command, send it through MediatR, translate the result back to HTTP.
/// All the actual logic lives in Application handlers — which is exactly
/// what makes those handlers unit-testable without HTTP.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
[AllowAnonymous] // by definition you're not logged in yet on these endpoints
public sealed class AuthController : ControllerBase
{
    private readonly ISender _sender;

    public AuthController(ISender sender) => _sender = sender;

    /// <summary>Create an account. Returns a JWT so the user is logged in immediately.</summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthResultDto>> Register(
        RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new RegisterCommand(request.Email, request.Password), cancellationToken);
        return Ok(result);
    }

    /// <summary>Exchange email + password for a JWT.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResultDto>> Login(
        LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new LoginCommand(request.Email, request.Password), cancellationToken);
        return Ok(result);
    }
}
