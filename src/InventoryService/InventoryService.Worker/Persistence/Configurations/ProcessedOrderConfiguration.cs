using InventoryService.Worker.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryService.Worker.Persistence.Configurations;

public sealed class ProcessedOrderConfiguration : IEntityTypeConfiguration<ProcessedOrder>
{
    public void Configure(EntityTypeBuilder<ProcessedOrder> builder)
    {
        builder.ToTable("ProcessedOrders");
        builder.HasKey(p => p.OrderId);

        builder.Property(p => p.ProcessedOnUtc).IsRequired();
    }
}
