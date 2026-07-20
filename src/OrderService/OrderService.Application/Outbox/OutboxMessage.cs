namespace OrderService.Application.Outbox;

/// <summary>
/// A durable row representing "an event that MUST eventually reach the
/// broker". This is the core of the Transactional Outbox pattern: instead of
/// publishing directly, handlers write a row like this to the SAME database,
/// in the SAME transaction, as the business change that caused it. A
/// separate relay (see Infrastructure/Outbox/OutboxDispatcher.cs) publishes
/// it afterwards and marks it processed.
///
/// Why this closes the gap described in the README/INTERVIEW_DEFENSE: an EF
/// SaveChangesAsync() call is one atomic transaction. If the order insert
/// commits, this row commits with it — there is no window where an order
/// exists with no corresponding outbox row. If the process crashes right
/// after, the row is still there for the relay to pick up on restart.
/// </summary>
public class OutboxMessage
{
    /// <summary>Assembly-qualified CLR type name, so the relay knows what to deserialize into.</summary>
    public string Type { get; private set; } = default!;

    /// <summary>The event, serialized as JSON.</summary>
    public string Content { get; private set; } = default!;

    public Guid Id { get; private set; }
    public DateTime OccurredOnUtc { get; private set; }
    public DateTime? ProcessedOnUtc { get; private set; }
    public int RetryCount { get; private set; }
    public string? Error { get; private set; }

    private OutboxMessage() { } // for EF Core only

    public static OutboxMessage Create(string type, string content, DateTime occurredOnUtc) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Content = content,
        OccurredOnUtc = occurredOnUtc
    };

    public void MarkProcessed(DateTime processedOnUtc)
    {
        ProcessedOnUtc = processedOnUtc;
        Error = null;
    }

    /// <summary>
    /// A failed publish attempt is not a business error — the row stays
    /// unprocessed so the next poll retries it. RetryCount lets the
    /// dispatcher eventually give up on a poison message instead of
    /// hammering a broker/payload that will never succeed.
    /// </summary>
    public void MarkFailed(string error)
    {
        RetryCount++;
        Error = error;
    }
}
