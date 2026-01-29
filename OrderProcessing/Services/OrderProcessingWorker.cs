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

        // Parse order items
        var items = JsonSerializer.Deserialize<List<OrderItem>>(order.ItemsJson) ?? [];
        
        // Calculate total from inventory prices and check stock
        decimal totalAmount = 0;
        foreach (var item in items)
        {
            var inventory = await db.Inventory.FirstOrDefaultAsync(i => i.ProductId == item.ProductId);
            if (inventory == null)
            {
                _logger.LogWarning("Order {OrderId}: Product {ProductId} not found in inventory", orderId, item.ProductId);
                continue;
            }
            
            if (inventory.Quantity < item.Quantity)
            {
                _logger.LogWarning("Order {OrderId}: Insufficient stock for {ProductId} (need {Need}, have {Have})",
                    orderId, item.ProductId, item.Quantity, inventory.Quantity);
            }
            
            // Decrement inventory
            inventory.Quantity = Math.Max(0, inventory.Quantity - item.Quantity);
            totalAmount += inventory.Price * item.Quantity;
        }

        // Simulate processing delay
        await Task.Delay(100);
        
        order.TotalAmount = totalAmount;
        order.Status = OrderStatus.Processed;
        order.ProcessedAt = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync();
            _logger.LogInformation("Order {OrderId} processed: {ItemCount} items, total={Total}", 
                orderId, items.Count, totalAmount);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogWarning("Order {OrderId} was already processed by another worker (concurrency)", orderId);
        }
    }
    
    private record OrderItem(string ProductId, int Quantity);

    private record OrderMessage(Guid OrderId);
}
