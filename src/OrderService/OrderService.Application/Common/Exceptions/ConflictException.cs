namespace OrderService.Application.Common.Exceptions;

/// <summary>Thrown when an operation conflicts with existing state
/// (e.g. registering an email that already exists). Mapped to HTTP 409.</summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message) : base(message) { }
}
