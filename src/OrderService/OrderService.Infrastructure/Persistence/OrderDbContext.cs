using Microsoft.EntityFrameworkCore;
using OrderService.Application.Abstractions;
using OrderService.Domain.Entities;

namespace OrderService.Infrastructure.Persistence;

/// <summary>
/// The EF Core DbContext — the bridge between C# objects and SQL tables.
/// Python bridge: roughly a SQLAlchemy Session + declarative Base combined.
///
/// It implements IApplicationDbContext so the Application layer can depend
/// on the interface while DI hands it this concrete class at runtime.
/// </summary>
public class OrderDbContext : DbContext, IApplicationDbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }

    // Set<T>() instead of auto-properties keeps these non-nullable without
    // compiler warnings — a common modern EF idiom.
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Pick up every IEntityTypeConfiguration<T> in this assembly.
        // Keeping mapping in separate config classes (instead of one giant
        // OnModelCreating) scales much better as entities grow.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
