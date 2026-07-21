using Microsoft.Extensions.Options;

namespace InventoryService.Worker.Outbox;

/// <summary>
/// Polls the outbox on a timer for the lifetime of the process — identical
/// shape to OrderService's OutboxDispatcherBackgroundService. Runs in its
/// own DI scope per tick since IOutboxDispatcher (and its InventoryDbContext)
/// are scoped, while this background service itself is a singleton.
/// </summary>
public sealed class OutboxDispatcherBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcherBackgroundService> _logger;
    private readonly OutboxOptions _options;

    public OutboxDispatcherBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxDispatcherBackgroundService> logger,
        IOptions<OutboxOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.PollingIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
                await dispatcher.DispatchPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Outbox dispatch loop failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }
}
