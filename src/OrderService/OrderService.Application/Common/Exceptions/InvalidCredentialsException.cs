namespace OrderService.Application.Common.Exceptions;

/// <summary>
/// Thrown on failed login. Mapped to HTTP 401 by the middleware.
/// SECURITY: the message is deliberately vague ("Invalid email or password")
/// so an attacker can't tell WHICH part was wrong — that would let them
/// enumerate registered emails.
/// </summary>
public sealed class InvalidCredentialsException : Exception
{
    public InvalidCredentialsException() : base("Invalid email or password.") { }
}
