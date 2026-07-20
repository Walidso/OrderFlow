using Microsoft.EntityFrameworkCore;
using OrderService.Application.Outbox;
using OrderService.Domain.Entities;

namespace OrderService.Application.Abstractions;

/// <summary>
/// The Application layer's view of the database.
///
/// WHY an interface and not OrderDbContext directly?
/// Dependency Inversion: Application defines WHAT it needs ("give me Orders,
/// Users and a way to save"), Infrastructure decides HOW (Postgres via EF).
/// It also makes handlers trivially testable — in unit tests we plug in the
/// EF InMemory provider instead of a real database.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Order> Orders { get; }
    DbSet<User> Users { get; }

    /// <summary>
    /// Staged outbox rows. Handlers don't add to this directly — go through
    /// IOutboxWriter.Enqueue(), which is exposed here only so a single
    /// SaveChangesAsync() call commits both the business change and the
    /// outbox row atomically.
    /// </summary>
    DbSet<OutboxMessage> OutboxMessages { get; }

    /// <summary>
    /// Commits all tracked changes in ONE transaction.
    /// Python bridge: like session.commit() in SQLAlchemy.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
