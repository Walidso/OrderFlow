using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions; // built-in RemoveAll<T>
using OrderService.Infrastructure.Persistence;

namespace OrderService.IntegrationTests;

/// <summary>
/// Boots the REAL app in memory (real Program.cs, real DI, real middleware,
/// real JWT validation) with exactly two swaps:
///
///   PostgreSQL -> in-memory SQLite   (no Docker needed to run tests)
///   RabbitMQ   -> MassTransit test harness (in-memory bus that RECORDS
///                                           published messages so we can
///                                           assert on them)
///
/// Everything else is untouched — that's the point of integration tests.
/// </summary>
public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    // One OPEN connection for the factory's lifetime. SQLite's ":memory:"
    // database lives exactly as long as its connection — close it and the
    // schema evaporates mid-test.
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        builder.ConfigureServices(services =>
        {
            // Remove the Postgres registration before adding the SQLite one,
            // or UseNpgsql sticks around alongside it.
            services.RemoveAll<DbContextOptions<OrderDbContext>>();

            services.AddDbContext<OrderDbContext>(options => options.UseSqlite(_connection));

            // Replaces the RabbitMQ transport with an in-memory one and adds
            // ITestHarness for asserting "was event X published?".
            services.AddMassTransitTestHarness();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }
}
