using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderService.Domain.Entities;

namespace OrderService.Infrastructure.Persistence.Configurations;

public sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.ProductId).IsRequired().HasMaxLength(64);
        builder.Property(i => i.ProductName).IsRequired().HasMaxLength(256);

        // decimal without precision triggers an EF warning and provider
        // defaults. 18,2 = up to 16 digits before the comma, 2 after —
        // standard for money. (Never use float/double for money: binary
        // floats can't represent 0.1 exactly. Same trap exists in Python,
        // which is why it has the decimal module.)
        builder.Property(i => i.UnitPrice).HasPrecision(18, 2);
    }
}
