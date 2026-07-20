using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderService.Infrastructure.Outbox;

/// <summary>
/// Polls the outbox on a timer for the lifetime of the process. Runs in its
/// own DI scope per tick (IOutboxDispatcher and its OrderDbContext are
/// scoped, and a BackgroundService itself is a singleton, so it can't just
/// constructor-inject them).
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
                // A poll failing (e.g. DB briefly unreachable) must not kill
                // the loop — log it and try again next tick.
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
