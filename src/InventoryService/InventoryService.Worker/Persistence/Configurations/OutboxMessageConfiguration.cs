using InventoryService.Worker.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryService.Worker.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Type).IsRequired().HasMaxLength(512);
        builder.Property(m => m.Content).IsRequired();
        builder.Property(m => m.OccurredOnUtc).IsRequired();
        builder.Property(m => m.Error).HasMaxLength(2048);

        // The dispatcher's hot-path query: unprocessed rows whose backoff
        // window (if any) has elapsed, oldest first.
        builder.HasIndex(m => new { m.ProcessedOnUtc, m.NextAttemptUtc, m.OccurredOnUtc });
    }
}
