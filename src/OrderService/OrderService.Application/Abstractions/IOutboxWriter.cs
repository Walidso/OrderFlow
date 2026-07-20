namespace OrderService.Application.Abstractions;

/// <summary>
/// Where handlers enqueue integration events instead of publishing them
/// directly. Enqueue() only stages a row on the current DbContext — it does
/// NOT save. That's deliberate: the caller's own SaveChangesAsync() is what
/// commits the outbox row in the SAME transaction as whatever business
/// change caused it (e.g. the new Order). A separate relay publishes it to
/// the broker afterwards. See OutboxMessage for the full rationale.
/// </summary>
public interface IOutboxWriter
{
    void Enqueue<TEvent>(TEvent @event) where TEvent : class;
}
