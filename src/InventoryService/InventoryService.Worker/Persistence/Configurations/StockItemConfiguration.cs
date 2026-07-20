using InventoryService.Worker.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryService.Worker.Persistence.Configurations;

public sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.ToTable("StockItems");
        builder.HasKey(s => s.ProductId);

        builder.Property(s => s.ProductId).HasMaxLength(64);
        builder.Property(s => s.AvailableQuantity).IsRequired();

        // Seeded demo inventory for the fruit store 🍎 — versioned in the
        // migration (via HasData) instead of a startup check-and-seed step,
        // so `dotnet ef migrations add` is the one place this data changes.
        builder.HasData(
            StockItem.Create("APPLE-1", 100),   // plenty in stock — happy path
            StockItem.Create("BANANA-1", 50),
            StockItem.Create("MANGO-1", 3),      // scarce — order 4+ to see StockRejected
            StockItem.Create("DURIAN-1", 999));  // the consumer throws on purpose to
                                                  // demonstrate retry + the _error queue
    }
}
