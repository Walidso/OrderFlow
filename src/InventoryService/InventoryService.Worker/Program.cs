using InventoryService.Worker.Consumers;
using InventoryService.Worker.Idempotency;
using InventoryService.Worker.Persistence;
using InventoryService.Worker.Stock;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ============================================================================
// The Inventory Service is a lightweight event-driven worker.
// It hosts no controllers — just a MassTransit consumer + two tiny endpoints
// (/health for orchestration, /stock so you can peek at inventory in demos).
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// ------------------- Database -------------------
// Its OWN Postgres, separate from OrderService's — see README "Persistent
// inventory" / "Why do the two services share no database". Stock and the
// idempotency guard both live here now instead of in-process memory, so
// neither forgets anything on restart.
builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("InventoryDb")));

// Scoped, not Singleton: MassTransit resolves consumers (and therefore
// their dependencies) per message with a scoped lifetime, exactly like
// OrderService's StockReservedConsumer takes a scoped DbContext per message.
builder.Services.AddScoped<IStockStore, EfStockStore>();
builder.Services.AddScoped<IProcessedOrderStore, EfProcessedOrderStore>();

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

builder.Services.AddHealthChecks()
    .AddDbContextCheck<InventoryDbContext>();

// ------------------- Observability -------------------
// Same tracing stack as OrderService.Infrastructure — see its
// DependencyInjection.cs comment for why AddNpgsql() needs the explicit
// static call (OpenTelemetry's hosting builder implements both
// TracerProviderBuilder and IServiceCollection, colliding with EF Core's
// own unrelated AddNpgsql<TContext> extension). Listening for the
// "MassTransit" ActivitySource here is what joins this consumer's spans to
// the same trace as the order that triggered it.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("inventory-service"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation(options =>
            options.Filter = httpContext => httpContext.Request.Path != "/health");
        global::Npgsql.TracerProviderBuilderExtensions.AddNpgsql(tracing);
        tracing.AddSource("MassTransit");
        tracing.AddOtlpExporter(otlp => otlp.Endpoint =
            new Uri(builder.Configuration["Otlp:Endpoint"] ?? "http://localhost:4317"));
    });

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
app.MapGet("/stock", async (IStockStore store) => Results.Ok(await store.SnapshotAsync()));

// ---- Apply EF Core migrations on startup ----
// Same pragmatic "just works" choice as OrderService.Api — see its
// Program.cs and INTERVIEW_DEFENSE.md for why this is wrong for a
// multi-replica production deployment.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

    if (db.Database.IsNpgsql())
    {
        db.Database.Migrate();
    }
    else
    {
        // Test doubles for this service (see InventoryService.UnitTests)
        // build InventoryDbContext straight against the model instead of
        // Postgres-specific migration SQL.
        db.Database.EnsureCreated();
    }
}

app.Run();
