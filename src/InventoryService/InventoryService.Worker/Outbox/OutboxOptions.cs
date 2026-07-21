namespace InventoryService.Worker.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>How often the relay polls for unprocessed rows.</summary>
    public int PollingIntervalSeconds { get; set; } = 2;

    /// <summary>Max rows dispatched per poll — keeps one slow poll from blocking the next.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// After this many failed attempts a row is left alone (still visible in
    /// the table with its last Error) instead of retried forever — the
    /// outbox equivalent of a poison-message error queue.
    /// </summary>
    public int MaxRetries { get; set; } = 5;
}
