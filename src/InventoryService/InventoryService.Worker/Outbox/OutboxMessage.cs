namespace InventoryService.Worker.Outbox;

/// <summary>
/// Inventory's own outbox row — same shape and purpose as
/// OrderService.Application.Outbox.OutboxMessage, but a separate type: the
/// two services share NO code except OrderFlow.Contracts (see README "Why
/// do the two services share no database" — the same "no shared code
/// beyond published contracts" rule applies here).
///
/// This exists so StockReservationCoordinator can commit the stock update,
/// the idempotency marker, AND the outbound event (StockReserved /
/// StockRejected) in ONE transaction — closing the exact gap that a
/// separate "mark processed, then publish" step would reopen (see the
/// coordinator's comments for the full reasoning).
/// </summary>
public class OutboxMessage
{
    public string Type { get; private set; } = default!;
    public string Content { get; private set; } = default!;

    public Guid Id { get; private set; }
    public DateTime OccurredOnUtc { get; private set; }
    public DateTime? ProcessedOnUtc { get; private set; }
    public int RetryCount { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Earliest the dispatcher should retry this row — see MarkFailed.</summary>
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
    /// Exponential backoff (capped at 60s) so a broker outage isn't hammered
    /// every poll tick — same idea as the RabbitMQ consumer's 1s/5s/15s
    /// retry ladder.
    /// </summary>
    public void MarkFailed(string error)
    {
        RetryCount++;
        Error = error;
        var delaySeconds = Math.Min(60, Math.Pow(2, RetryCount));
        NextAttemptUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
    }
}
