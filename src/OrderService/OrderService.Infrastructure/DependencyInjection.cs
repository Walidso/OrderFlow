using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Application.Abstractions;
using OrderService.Infrastructure.Auth;
using OrderService.Infrastructure.Messaging;
using OrderService.Infrastructure.Messaging.Consumers;
using OrderService.Infrastructure.Outbox;
using OrderService.Infrastructure.Persistence;

namespace OrderService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // ------------------- Database -------------------
        services.AddDbContext<OrderDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("OrderDb")));

        // When Application asks for IApplicationDbContext, hand it the SAME
        // scoped OrderDbContext instance (not a second one) — one context
        // per request means one unit of work per request.
        services.AddScoped<IApplicationDbContext>(sp =>
            sp.GetRequiredService<OrderDbContext>());

        // ------------------- Auth -------------------
        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
        // Singletons: both are stateless, so one instance for the app's
        // lifetime is correct and cheapest.

        // ------------------- Messaging -------------------
        services.AddScoped<IEventPublisher, MassTransitEventPublisher>();

        // ------------------- Outbox -------------------
        // Enqueue (Application) writes rows in the same transaction as the
        // business change; Dispatch (background service) relays them to the
        // broker afterwards. Together they make "save + publish" atomic.
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.AddScoped<IOutboxWriter, EfOutboxWriter>();
        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();
        services.AddHostedService<OutboxDispatcherBackgroundService>();

        services.AddMassTransit(x =>
        {
            // These consumers close the loop: Inventory answers, we update
            // the order's status.
            x.AddConsumer<StockReservedConsumer>();
            x.AddConsumer<StockRejectedConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(configuration["RabbitMq:Host"] ?? "localhost", "/", h =>
                {
                    h.Username(configuration["RabbitMq:Username"] ?? "guest");
                    h.Password(configuration["RabbitMq:Password"] ?? "guest");
                });

                // Retry BEFORE giving up. If all attempts fail, MassTransit
                // moves the message to "<queue-name>_error" — the RabbitMQ
                // equivalent of the Azure Service Bus dead-letter queue you
                // already know (details + comparison in the Inventory
                // Service's OrderCreatedConsumer).
                cfg.UseMessageRetry(r => r.Intervals(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(15)));

                // Auto-create one receive queue per registered consumer with
                // sensible kebab-case names (e.g. "stock-reserved").
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
