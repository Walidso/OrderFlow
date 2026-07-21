using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OrderService.Api.Middleware;
using OrderService.Application;
using OrderService.Infrastructure;
using OrderService.Infrastructure.Outbox;
using OrderService.Infrastructure.Persistence;

// ============================================================================
// Program.cs = the composition root: everything gets wired together here,
// and ONLY here. Read it top-to-bottom as: (1) register services,
// (2) build the app, (3) order the middleware pipeline, (4) run.
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// ---- Layers register themselves (see each layer's DependencyInjection.cs) ----
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddControllers();

// ---- CORS: lets the browser-based Web UI (served from a different origin/
// port) call this API. AllowAnyHeader is required so the Authorization
// header survives the preflight OPTIONS request. ----
builder.Services.AddCors(options =>
{
    options.AddPolicy("WebUi", policy => policy
        .WithOrigins("http://localhost:5003")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// ---- API versioning: routes look like /api/v1/orders ----
// Versioning from day one means v2 can ship later WITHOUT breaking v1
// clients — much cheaper to add now than to retrofit.
builder.Services
    .AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true; // adds api-supported-versions header
    })
    .AddMvc()
    .AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'VVV";           // v1, v2, ...
        options.SubstituteApiVersionInUrl = true;      // fills {version} in routes
    });

// ---- Swagger / OpenAPI with a "paste your JWT here" button ----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "OrderFlow — Order Service",
        Version = "v1",
        Description = "Creates orders, authenticates users, and reacts to inventory events."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Register or log in, copy the token, click Authorize and paste it."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ---- JWT authentication ----
// This middleware VALIDATES incoming tokens; creating them is
// Infrastructure's JwtTokenGenerator. Validation steps, in order:
//   1. Parse the three token parts.
//   2. Recompute the HMAC-SHA256 signature with our key; reject on mismatch
//      (someone tampered with the payload or signed with the wrong key).
//   3. Check issuer + audience match what we expect.
//   4. Check the expiry (exp) hasn't passed.
// Only then does the request count as authenticated and claims populate
// HttpContext.User.
var jwtSection = builder.Configuration.GetSection("Jwt");
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSection["Key"]
                    ?? throw new InvalidOperationException("Jwt:Key is not configured."))),
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateLifetime = true,
            // Default skew is 5 minutes(!) to tolerate clock drift between
            // servers; we tighten it so expired tokens die quickly.
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

// ---- Health checks: /health returns Unhealthy if the DB OR the broker is
// unreachable ---- docker-compose and orchestrators use this to know when
// we're actually up. Without the RabbitMQ check, this service could report
// Healthy while every message it publishes silently fails.
var rabbitMqUri = new Uri(
    $"amqp://{builder.Configuration["RabbitMq:Username"] ?? "guest"}:" +
    $"{builder.Configuration["RabbitMq:Password"] ?? "guest"}@" +
    $"{builder.Configuration["RabbitMq:Host"] ?? "localhost"}:5672");

builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrderDbContext>()
    .AddRabbitMQ(rabbitConnectionString: rabbitMqUri.ToString());

// ---- Rate limiting: brute-force / DoS protection on the endpoints most
// worth abusing. Partitioned per caller (not one global counter) — a
// single attacker maxing out their own limit shouldn't lock out everyone
// else hitting the same endpoint. ----
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Register + login are unauthenticated by definition, so the only
    // identity available to partition on is the caller's IP.
    options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = 5,
            QueueLimit = 0
        }));

    // Order creation is authenticated, so partition on the JWT's user id
    // rather than IP — matches how the rest of the app treats the token as
    // the caller's real identity (see OrdersController.CurrentUserId).
    options.AddPolicy("orders", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromSeconds(10),
            PermitLimit = 20,
            QueueLimit = 0
        }));
});

var app = builder.Build();

// ============================================================================
// MIDDLEWARE PIPELINE — ORDER IS EVERYTHING.
// Error handling goes FIRST so its try/catch wraps every later component.
// Authentication must run BEFORE authorization (you must know WHO before
// you can decide WHAT they may do).
// ============================================================================
app.UseMiddleware<GlobalExceptionHandlingMiddleware>();

app.UseSwagger();
app.UseSwaggerUI(); // portfolio project: Swagger stays on in all environments

app.UseCors("WebUi");

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHealthChecks("/health");

// ---- Diagnostics: outbox rows the dispatcher has given up on ----
// "Never silently drop a failed message" is already the rule for consumer
// poison messages (RabbitMQ's `_error` queue) — this is the same rule
// applied to the outbox side. Once a row's RetryCount reaches MaxRetries,
// OutboxDispatcher stops picking it up; this is where a human finds it
// instead of it just sitting invisibly in the table.
app.MapGet("/diagnostics/outbox/dead-letters", async (OrderDbContext db, IOptions<OutboxOptions> outboxOptions) =>
{
    var deadLetters = await db.OutboxMessages
        .Where(m => m.ProcessedOnUtc == null && m.RetryCount >= outboxOptions.Value.MaxRetries)
        .OrderBy(m => m.OccurredOnUtc)
        .Select(m => new { m.Id, m.Type, m.OccurredOnUtc, m.RetryCount, m.Error })
        .ToListAsync();

    return Results.Ok(deadLetters);
});

// ---- Apply EF Core migrations on startup ----
// For a local/demo system this is the pragmatic choice: `docker compose up`
// and the schema just exists. In production with multiple replicas you'd
// run migrations as a separate deploy step instead (see INTERVIEW_DEFENSE).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

    if (db.Database.IsNpgsql())
    {
        // Runs every migration the database hasn't applied yet
        // (tracked in the __EFMigrationsHistory table).
        db.Database.Migrate();
    }
    else
    {
        // Integration tests swap Postgres for in-memory SQLite. Migrations
        // are Postgres-specific SQL, so for tests we build the schema
        // straight from the model instead.
        db.Database.EnsureCreated();
    }
}

app.Run();

// Makes the auto-generated Program class visible to WebApplicationFactory
// in the integration test project. Without this line those tests can't boot
// the app in-memory.
public partial class Program { }
