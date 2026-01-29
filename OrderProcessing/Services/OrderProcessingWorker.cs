using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderProcessing.Data;
using OrderProcessing.Entities;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderProcessing.Services;

public class OrderProcessingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqService _rabbitMqService;
    private readonly ILogger<OrderProcessingWorker> _logger;

    public OrderProcessingWorker(
        IServiceScopeFactory scopeFactory,
        RabbitMqService rabbitMqService,
        ILogger<OrderProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _rabbitMqService = rabbitMqService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = _rabbitMqService.GetChannel();
        var queueName = _rabbitMqService.GetQueueName();

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                var orderMessage = JsonSerializer.Deserialize<OrderMessage>(message);

                if (orderMessage != null)
                {
                    await ProcessOrderAsync(orderMessage.OrderId);
                }

                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing order");
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
            }
        };

        await channel.BasicConsumeAsync(queue: queueName, autoAck: false, consumer: consumer);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessOrderAsync(Guid orderId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null)
        {
            _logger.LogWarning("Order {OrderId} not found", orderId);
            return;
        }

        if (order.Status == OrderStatus.Processed)
        {
            _logger.LogInformation("Order {OrderId} already processed", orderId);
            return;
        }

        // Simulate business logic: calculate price and apply delay
        await Task.Delay(1000);
        
        // Calculate TotalAmount (random for demo, real impl would query price catalog)
        var calculatedAmount = Math.Round((decimal)(Random.Shared.NextDouble() * 500 + 10), 2);
        
        if (order.TotalAmount.HasValue)
        {
            // Client provided expected price - compare with calculated
            var diff = Math.Abs(order.TotalAmount.Value - calculatedAmount);
            _logger.LogInformation("Order {OrderId}: expected={Expected}, calculated={Calculated}, diff={Diff}",
                orderId, order.TotalAmount.Value, calculatedAmount, diff);
        }
        
        // Always set to calculated amount (overwrite expected with actual)
        order.TotalAmount = calculatedAmount;
        order.Status = OrderStatus.Processed;
        order.ProcessedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        _logger.LogInformation("Order {OrderId} processed successfully", orderId);
    }

    private record OrderMessage(Guid OrderId);
}
