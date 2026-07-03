using FluentValidation;
using MediatR;

namespace OrderService.Application.Behaviors;

/// <summary>
/// A MediatR pipeline behavior = middleware for your commands/queries.
/// Every request passes through here BEFORE reaching its handler, exactly
/// like ASP.NET middleware wraps HTTP requests.
///
/// Python bridge: think of it as a decorator applied to every handler.
///
/// Flow:  Controller -> [ValidationBehavior] -> Handler
/// If any registered validator for the request fails, we throw a
/// FluentValidation.ValidationException, which the global error middleware
/// converts to an HTTP 400 with a per-field error dictionary.
///
/// WHY here and not in controllers? One implementation guards EVERY command
/// forever. Add a new command + validator and validation "just works" —
/// impossible to forget.
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    // DI hands us ALL validators registered for this TRequest (often 0 or 1).
    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
        => _validators = validators;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (_validators.Any())
        {
            var context = new ValidationContext<TRequest>(request);

            var results = await Task.WhenAll(
                _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

            var failures = results
                .SelectMany(r => r.Errors)   // flatten: List[List[error]] -> List[error]
                .Where(f => f is not null)
                .ToList();

            if (failures.Count != 0)
                throw new ValidationException(failures);
        }

        // All good — continue down the pipeline to the actual handler.
        return await next();
    }
}
