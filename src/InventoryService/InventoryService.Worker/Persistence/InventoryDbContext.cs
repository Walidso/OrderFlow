using InventoryService.Worker.Idempotency;
using InventoryService.Worker.Stock;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Worker.Persistence;

/// <summary>
/// Inventory's own database — separate from OrderService's Postgres, per
/// the "services never share a database" rule this whole project follows
/// (see README "Why do the two services share no database"). Its own
/// docker-compose Postgres container, its own connection string, its own
/// migrations.
/// </summary>
public class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }

    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<ProcessedOrder> ProcessedOrders => Set<ProcessedOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
