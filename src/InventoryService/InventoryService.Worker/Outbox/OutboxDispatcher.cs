using System.Text.Json;
using InventoryService.Worker.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InventoryService.Worker.Outbox;

/// <summary>
/// The relay half of Inventory's outbox — same mechanics as
/// OrderService.Infrastructure.Outbox.OutboxDispatcher (see its comments for
/// the full reasoning on SKIP LOCKED and backoff). Publishes via
/// MassTransit's IPublishEndpoint directly rather than a custom
/// IEventPublisher port: this service doesn't otherwise practice that
/// port/adapter split (EfStockStore/EfProcessedOrderStore already take
/// InventoryDbContext directly), so introducing one just for this would be
/// inconsistent with how the rest of this project is built.
/// </summary>
public interface IOutboxDispatcher
{
    Task<int> DispatchPendingAsync(CancellationToken cancellationToken = default);
}

public sealed class OutboxDispatcher : IOutboxDispatcher
{
    private readonly InventoryDbContext _db;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly OutboxOptions _options;

    public OutboxDispatcher(
        InventoryDbContext db,
        IPublishEndpoint publishEndpoint,
        ILogger<OutboxDispatcher> logger,
        IOptions<OutboxOptions> options)
    {
        _db = db;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        var usesPostgres = _db.Database.IsNpgsql();

        IDbContextTransaction? transaction = usesPostgres
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var pending = usesPostgres
                ? await ClaimPendingRowsAsync(cancellationToken)
                : await SelectPendingRowsAsync(cancellationToken);

            if (pending.Count == 0)
            {
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return 0;
            }

            foreach (var message in pending)
                await DispatchOneAsync(message, cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);

            return pending.Count;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    // FOR UPDATE SKIP LOCKED: lets multiple replicas of this service poll
    // concurrently without duplicating work — see the OrderService
    // dispatcher's comments for the full explanation. Postgres-only; the
    // plain LINQ query below is what test doubles (SQLite/InMemory) use.
    private Task<List<OutboxMessage>> ClaimPendingRowsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        return _db.OutboxMessages
            .FromSqlInterpolated($@"
                SELECT * FROM ""OutboxMessages""
                WHERE ""ProcessedOnUtc"" IS NULL
                  AND ""RetryCount"" < {_options.MaxRetries}
                  AND (""NextAttemptUtc"" IS NULL OR ""NextAttemptUtc"" <= {now})
                ORDER BY ""OccurredOnUtc""
                LIMIT {_options.BatchSize}
                FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);
    }

    private Task<List<OutboxMessage>> SelectPendingRowsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        return _db.OutboxMessages
            .Where(m => m.ProcessedOnUtc == null
                && m.RetryCount < _options.MaxRetries
                && (m.NextAttemptUtc == null || m.NextAttemptUtc <= now))
            .OrderBy(m => m.OccurredOnUtc)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);
    }

    private async Task DispatchOneAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var type = Type.GetType(message.Type)
                ?? throw new InvalidOperationException($"Unknown outbox message type '{message.Type}'.");
            var payload = JsonSerializer.Deserialize(message.Content, type)
                ?? throw new InvalidOperationException("Outbox payload deserialized to null.");

            await _publishEndpoint.Publish(payload, type, cancellationToken);
            message.MarkProcessed(DateTime.UtcNow);

            _logger.LogInformation("Outbox: dispatched {Type} ({Id})", type.Name, message.Id);
        }
        catch (Exception ex)
        {
            message.MarkFailed(ex.Message);

            if (message.RetryCount >= _options.MaxRetries)
            {
                _logger.LogError(ex,
                    "Outbox: message {Id} ({Type}) abandoned after {RetryCount} attempts",
                    message.Id, message.Type, message.RetryCount);
            }
            else
            {
                _logger.LogWarning(ex,
                    "Outbox: failed to dispatch message {Id} (attempt {Attempt}), retrying at {NextAttemptUtc}",
                    message.Id, message.RetryCount, message.NextAttemptUtc);
            }
        }
    }
}
