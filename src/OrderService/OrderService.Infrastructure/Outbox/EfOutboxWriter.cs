using System.Text.Json;
using OrderService.Application.Abstractions;
using OrderService.Application.Outbox;

namespace OrderService.Infrastructure.Outbox;

/// <summary>
/// The concrete IOutboxWriter: stages a serialized row on the SAME
/// IApplicationDbContext the caller is already using. It never calls
/// SaveChangesAsync itself — that's what makes the write atomic with
/// whatever else the caller is persisting in the same unit of work.
/// </summary>
public sealed class EfOutboxWriter : IOutboxWriter
{
    private readonly IApplicationDbContext _db;

    public EfOutboxWriter(IApplicationDbContext db) => _db = db;

    public void Enqueue<TEvent>(TEvent @event) where TEvent : class
    {
        var type = typeof(TEvent).AssemblyQualifiedName
            ?? throw new InvalidOperationException($"'{typeof(TEvent)}' has no assembly-qualified name.");
        var content = JsonSerializer.Serialize(@event);

        _db.OutboxMessages.Add(OutboxMessage.Create(type, content, DateTime.UtcNow));
    }
}
