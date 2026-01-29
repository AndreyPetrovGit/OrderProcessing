using Microsoft.EntityFrameworkCore;
using OrderProcessing.Data;

namespace OrderProcessing.Services;

public class OutboxProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRabbitMqService _rabbit;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(IServiceScopeFactory scopeFactory, IRabbitMqService rabbit, ILogger<OutboxProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _rabbit = rabbit;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingMessages(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox processor error");
            }
            
            await Task.Delay(1000, ct);
        }
    }

    private async Task ProcessPendingMessages(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var messages = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        foreach (var msg in messages)
        {
            try
            {
                await _rabbit.PublishOrderAsync(msg.OrderId);
                msg.ProcessedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                _logger.LogDebug("Outbox: published order {OrderId}", msg.OrderId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Outbox: failed to publish order {OrderId}", msg.OrderId);
            }
        }
    }
}
