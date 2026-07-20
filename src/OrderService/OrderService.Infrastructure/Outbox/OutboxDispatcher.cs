using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderService.Application.Abstractions;
using OrderService.Infrastructure.Persistence;

namespace OrderService.Infrastructure.Outbox;

/// <summary>
/// The relay half of the outbox pattern. Reads unprocessed OutboxMessage
/// rows, resolves each one's CLR type from its stored name, deserializes,
/// and publishes through the same IEventPublisher port CreateOrderCommand
/// used to go through directly. A failed publish (broker down, etc.) marks
/// the row failed and leaves it unprocessed for the next poll — it never
/// throws out of the batch, so one bad message can't starve the rest.
///
/// Injects OrderDbContext directly rather than IApplicationDbContext,
/// mirroring StockReservedConsumer/StockRejectedConsumer: this runs outside
/// an HTTP request, in its own DI scope created per poll, so there's no
/// "current request" DbContext to share.
/// </summary>
public interface IOutboxDispatcher
{
    /// <summary>Dispatches up to one batch of pending messages. Returns how many were attempted.</summary>
    Task<int> DispatchPendingAsync(CancellationToken cancellationToken = default);
}

public sealed class OutboxDispatcher : IOutboxDispatcher
{
    private readonly OrderDbContext _db;
    private readonly IEventPublisher _publisher;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly OutboxOptions _options;

    public OutboxDispatcher(
        OrderDbContext db,
        IEventPublisher publisher,
        ILogger<OutboxDispatcher> logger,
        IOptions<OutboxOptions> options)
    {
        _db = db;
        _publisher = publisher;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _db.OutboxMessages
            .Where(m => m.ProcessedOnUtc == null && m.RetryCount < _options.MaxRetries)
            .OrderBy(m => m.OccurredOnUtc)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0) return 0;

        foreach (var message in pending)
        {
            try
            {
                var type = Type.GetType(message.Type)
                    ?? throw new InvalidOperationException($"Unknown outbox message type '{message.Type}'.");
                var payload = JsonSerializer.Deserialize(message.Content, type)
                    ?? throw new InvalidOperationException("Outbox payload deserialized to null.");

                await _publisher.PublishAsync(payload, type, cancellationToken);
                message.MarkProcessed(DateTime.UtcNow);

                _logger.LogInformation("Outbox: dispatched {Type} ({Id})", type.Name, message.Id);
            }
            catch (Exception ex)
            {
                // Never let one poison message take down the batch — record
                // the failure and let the next poll retry it.
                message.MarkFailed(ex.Message);
                _logger.LogWarning(ex,
                    "Outbox: failed to dispatch message {Id} (attempt {Attempt})",
                    message.Id, message.RetryCount);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return pending.Count;
    }
}
