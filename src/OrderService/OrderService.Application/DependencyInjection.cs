using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Application.Behaviors;

namespace OrderService.Application;

/// <summary>
/// Each layer registers its own services via an extension method, so
/// Program.cs stays a readable table of contents:
///     builder.Services.AddApplication();
///     builder.Services.AddInfrastructure(config);
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Scan THIS assembly for all IRequestHandler implementations.
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));

        // Scan for all AbstractValidator<T> classes.
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        // Wire the validation behavior into the MediatR pipeline for every
        // request type (open generic <,> = "for all TRequest/TResponse pairs").
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        return services;
    }
}
