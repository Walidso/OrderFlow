using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using OrderService.Application.Common.Exceptions;

namespace OrderService.Api.Middleware;

/// <summary>
/// THE single place where exceptions become HTTP responses.
///
/// It sits FIRST in the pipeline (see Program.cs) so its try/catch wraps
/// everything downstream: routing, auth, controllers, MediatR handlers.
///
/// Every error body follows RFC 7807 "Problem Details" — the standard JSON
/// error format ASP.NET Core uses natively. Consistency matters: clients
/// write ONE error parser instead of guessing per endpoint.
///
/// Mapping table (Application exception -> HTTP status):
///   ValidationException          -> 400 + per-field error dictionary
///   InvalidCredentialsException  -> 401
///   NotFoundException            -> 404
///   ConflictException            -> 409
///   anything else                -> 500 with a GENERIC message
///
/// WHY generic on 500? Stack traces reveal internals (table names, file
/// paths, library versions) — a gift to attackers. We log the full details
/// server-side and give the client a correlation-friendly, boring message.
/// </summary>
public sealed class GlobalExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlingMiddleware> _logger;

    public GlobalExceptionHandlingMiddleware(
        RequestDelegate next, ILogger<GlobalExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context); // hand off to the rest of the pipeline
        }
        catch (ValidationException ex)
        {
            // Group FluentValidation failures by property:
            // { "Items[0].Quantity": ["Quantity must be at least 1."] }
            var errors = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

            var problem = new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more validation errors occurred.",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
            };
            await WriteAsync(context, problem);
        }
        catch (InvalidCredentialsException ex)
        {
            await WriteAsync(context, Problem(401, "Unauthorized", ex.Message,
                "https://tools.ietf.org/html/rfc7235#section-3.1"));
        }
        catch (NotFoundException ex)
        {
            await WriteAsync(context, Problem(404, "Resource not found", ex.Message,
                "https://tools.ietf.org/html/rfc7231#section-6.5.4"));
        }
        catch (ConflictException ex)
        {
            await WriteAsync(context, Problem(409, "Conflict", ex.Message,
                "https://tools.ietf.org/html/rfc7231#section-6.5.8"));
        }
        catch (Exception ex)
        {
            // Full details go to the log (with stack trace)...
            _logger.LogError(ex, "Unhandled exception for {Method} {Path}",
                context.Request.Method, context.Request.Path);

            // ...but the client only sees this:
            await WriteAsync(context, Problem(500, "An unexpected error occurred.",
                "The server encountered an error. Please try again later.",
                "https://tools.ietf.org/html/rfc7231#section-6.6.1"));
        }
    }

    private static ProblemDetails Problem(int status, string title, string detail, string type)
        => new() { Status = status, Title = title, Detail = detail, Type = type };

    private static async Task WriteAsync(HttpContext context, ProblemDetails problem)
    {
        context.Response.StatusCode = problem.Status ?? 500;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, problem.GetType(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
