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

    /// <summary>
    /// Earliest the dispatcher should try this row again. Null means "try on
    /// the very next poll" — the state a fresh row starts in, and also what
    /// a successful dispatch doesn't need to touch. Set by MarkFailed with
    /// an exponential backoff, so a broker outage doesn't get hammered every
    /// single poll tick for the row's whole retry budget.
    /// </summary>
    public DateTime? NextAttemptUtc { get; private set; }

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
    ///
    /// Exponential backoff (capped at 60s), same increasing-gaps idea as the
    /// RabbitMQ consumer's 1s/5s/15s retry ladder: a transient blip gets
    /// retried almost immediately, but a broker that's genuinely down for a
    /// while doesn't get hammered every single poll tick.
    /// </summary>
    public void MarkFailed(string error)
    {
        RetryCount++;
        Error = error;
        var delaySeconds = Math.Min(60, Math.Pow(2, RetryCount));
        NextAttemptUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
    }
}
