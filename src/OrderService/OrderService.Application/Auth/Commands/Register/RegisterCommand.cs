using MediatR;

namespace OrderService.Application.Auth.Commands.Register;

public record RegisterCommand(string Email, string Password) : IRequest<AuthResultDto>;
