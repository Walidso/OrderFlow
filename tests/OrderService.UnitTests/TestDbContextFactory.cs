using Microsoft.EntityFrameworkCore;
using OrderService.Infrastructure.Persistence;

namespace OrderService.UnitTests;

/// <summary>
/// ============================ UNIT vs INTEGRATION ==========================
/// UNIT TESTS (this project) test ONE handler in isolation:
///   - real database   -> replaced by EF's InMemory provider (fast, no I/O)
///   - message broker  -> replaced by an NSubstitute mock of IEventPublisher
///   - HTTP layer      -> doesn't exist; we call Handle() directly
/// They answer: "is the LOGIC of this handler correct?"
/// They run in milliseconds, so you can run hundreds on every save.
///
/// INTEGRATION TESTS (../OrderService.IntegrationTests) boot the ENTIRE app
/// in memory with WebApplicationFactory and fire real HTTP requests. They
/// answer: "do all the pieces — routing, JWT middleware, validation
/// pipeline, error middleware, EF, DI wiring — actually work TOGETHER?"
/// Slower, fewer, but they catch the bugs unit tests can't (a missing
/// [Authorize], a middleware in the wrong order, a DI registration typo).
///
/// Healthy ratio: many fast unit tests, a thinner layer of integration
/// tests over the critical flows — the classic "test pyramid".
/// ============================================================================
/// </summary>
public static class TestDbContextFactory
{
    public static OrderDbContext Create()
    {
        // A UNIQUE database name per call gives every test a private, empty
        // database. Shared state between tests = flaky tests.
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new OrderDbContext(options);
    }
}
