using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderService.Domain.Entities;

namespace OrderService.Infrastructure.Persistence.Configurations;

/// <summary>
/// Fluent mapping for Order. This is where "C# class" meets "SQL table".
/// The migration files were generated FROM this model — if you change
/// something here, run `dotnet ef migrations add <Name>` to capture it.
/// </summary>
public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(o => o.Id);

        // Store the enum as TEXT ("Pending") instead of an int (0).
        // Trade-off: slightly more storage, massively more readable data —
        // and renumbering the enum can never corrupt historical rows.
        builder.Property(o => o.Status)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(o => o.CreatedAtUtc).IsRequired();

        // One order -> many items; deleting an order deletes its items.
        // WithOne() has no navigation back to Order on OrderItem — we don't
        // need item.Order in our code, so we don't model it.
        builder.HasMany(o => o.Items)
            .WithOne()
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // Tell EF to read/write the private "_items" field instead of the
        // read-only Items property — this is what lets the Domain expose an
        // immutable collection while EF can still populate it.
        builder.Navigation(o => o.Items)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // "Total" has no setter and no backing field mapped -> EF ignores it
        // automatically. It is computed in C#, never stored.
    }
}
