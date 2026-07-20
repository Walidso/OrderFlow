using InventoryService.Worker.Consumers;
using InventoryService.Worker.Idempotency;
using InventoryService.Worker.Stock;
using MassTransit;

// ============================================================================
// The Inventory Service is a lightweight event-driven worker.
// It hosts no controllers — just a MassTransit consumer + two tiny endpoints
// (/health for orchestration, /stock so you can peek at inventory in demos).
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// Singleton: ONE stock store for the whole process — it IS our "database".
builder.Services.AddSingleton<IStockStore, InMemoryStockStore>();

// Singleton for the same reason: the dedupe guard must be shared across
// every message this process ever consumes, not scoped per-message.
builder.Services.AddSingleton<IProcessedOrderStore, InMemoryProcessedOrderStore>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderCreatedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });

        // Explicit endpoint (instead of ConfigureEndpoints) so the queue
        // name — and therefore the error queue's name — is obvious:
        //   queue:        inventory-order-created
        //   error queue:  inventory-order-created_error   (the "DLQ")
        cfg.ReceiveEndpoint("inventory-order-created", e =>
        {
            // Retry ladder for transient failures. See the big comment block
            // in OrderCreatedConsumer for how this maps to Azure Service Bus
            // dead-lettering.
            e.UseMessageRetry(r => r.Intervals(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15)));

            e.ConfigureConsumer<OrderCreatedConsumer>(context);
        });
    });
});

builder.Services.AddHealthChecks();

// Lets the browser-based Web UI (a different origin/port) poll /stock.
builder.Services.AddCors(options =>
{
    options.AddPolicy("WebUi", policy => policy
        .WithOrigins("http://localhost:5003")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

app.UseCors("WebUi");

app.MapHealthChecks("/health");

// Peek endpoint for demos: watch numbers drop as orders are confirmed.
app.MapGet("/stock", (IStockStore store) => Results.Ok(store.Snapshot()));

app.Run();
