namespace OrderService.Application.Common.Exceptions;

/// <summary>
/// Thrown by handlers when a requested resource doesn't exist.
/// The global error-handling middleware translates this into an HTTP 404
/// ProblemDetails response. Handlers stay HTTP-agnostic: they speak in
/// domain terms ("not found"), the Api layer speaks HTTP (404).
/// </summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}
