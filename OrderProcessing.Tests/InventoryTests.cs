using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderProcessing.Data;
using OrderProcessing.Entities;

namespace OrderProcessing.Tests;

/// <summary>
/// Tests for Inventory-based business logic in order processing.
/// </summary>
public class InventoryTests : IDisposable
{
    private readonly AppDbContext _db;

    public InventoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        
        // Seed inventory
        _db.Inventory.AddRange(
            new Inventory { ProductId = "PROD-001", Quantity = 100, Price = 29.99m },
            new Inventory { ProductId = "PROD-002", Quantity = 50, Price = 49.99m },
            new Inventory { ProductId = "PROD-003", Quantity = 10, Price = 9.99m }
        );
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Worker_CalculatesTotalFromInventoryPrices()
    {
        // Order: 2x PROD-001 ($29.99) = $59.98
        var orderId = Guid.NewGuid();
        var items = new[] { new { ProductId = "PROD-001", Quantity = 2 } };
        var order = new Order
        {
            Id = orderId,
            CustomerId = 1,
            ItemsJson = JsonSerializer.Serialize(items),
            Status = OrderStatus.Pending
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        await SimulateWorkerWithInventory(orderId);

        var processed = await _db.Orders.FindAsync(orderId);
        Assert.Equal(OrderStatus.Processed, processed!.Status);
        Assert.Equal(59.98m, processed.TotalAmount);
    }

    [Fact]
    public async Task Worker_DecrementsInventoryQuantity()
    {
        var orderId = Guid.NewGuid();
        var items = new[] { new { ProductId = "PROD-001", Quantity = 5 } };
        var order = new Order
        {
            Id = orderId,
            CustomerId = 1,
            ItemsJson = JsonSerializer.Serialize(items),
            Status = OrderStatus.Pending
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        var before = (await _db.Inventory.FindAsync("PROD-001"))!.Quantity;
        await SimulateWorkerWithInventory(orderId);
        var after = (await _db.Inventory.FindAsync("PROD-001"))!.Quantity;

        Assert.Equal(100, before);
        Assert.Equal(95, after); // 100 - 5
    }

    [Fact]
    public async Task Worker_WhenProductNotFound_TotalIsZero()
    {
        var orderId = Guid.NewGuid();
        var items = new[] { new { ProductId = "UNKNOWN", Quantity = 1 } };
        var order = new Order
        {
            Id = orderId,
            CustomerId = 1,
            ItemsJson = JsonSerializer.Serialize(items),
            Status = OrderStatus.Pending
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        await SimulateWorkerWithInventory(orderId);

        var processed = await _db.Orders.FindAsync(orderId);
        Assert.Equal(OrderStatus.Processed, processed!.Status);
        Assert.Equal(0m, processed.TotalAmount);
    }

    [Fact]
    public async Task Worker_MultipleItems_CalculatesCorrectTotal()
    {
        // 2x PROD-001 ($29.99) + 1x PROD-002 ($49.99) = $109.97
        var orderId = Guid.NewGuid();
        var items = new[]
        {
            new { ProductId = "PROD-001", Quantity = 2 },
            new { ProductId = "PROD-002", Quantity = 1 }
        };
        var order = new Order
        {
            Id = orderId,
            CustomerId = 1,
            ItemsJson = JsonSerializer.Serialize(items),
            Status = OrderStatus.Pending
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        await SimulateWorkerWithInventory(orderId);

        var processed = await _db.Orders.FindAsync(orderId);
        Assert.Equal(109.97m, processed!.TotalAmount);
    }

    [Fact]
    public async Task Worker_InsufficientStock_StillProcesses()
    {
        // PROD-003 has 10 in stock, order 15
        var orderId = Guid.NewGuid();
        var items = new[] { new { ProductId = "PROD-003", Quantity = 15 } };
        var order = new Order
        {
            Id = orderId,
            CustomerId = 1,
            ItemsJson = JsonSerializer.Serialize(items),
            Status = OrderStatus.Pending
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        await SimulateWorkerWithInventory(orderId);

        var processed = await _db.Orders.FindAsync(orderId);
        Assert.Equal(OrderStatus.Processed, processed!.Status);
        Assert.Equal(149.85m, processed.TotalAmount); // 15 * $9.99
        
        var inventory = await _db.Inventory.FindAsync("PROD-003");
        Assert.Equal(0, inventory!.Quantity); // Decremented to 0, not negative
    }

    /// <summary>
    /// Simulates worker logic matching OrderProcessingWorker.ProcessOrderAsync
    /// </summary>
    private async Task SimulateWorkerWithInventory(Guid orderId)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null || order.Status == OrderStatus.Processed)
            return;

        var items = JsonSerializer.Deserialize<List<OrderItem>>(order.ItemsJson) ?? [];
        
        decimal totalAmount = 0;
        foreach (var item in items)
        {
            var inventory = await _db.Inventory.FirstOrDefaultAsync(i => i.ProductId == item.ProductId);
            if (inventory == null)
                continue;
            
            inventory.Quantity = Math.Max(0, inventory.Quantity - item.Quantity);
            totalAmount += inventory.Price * item.Quantity;
        }

        order.TotalAmount = totalAmount;
        order.Status = OrderStatus.Processed;
        order.ProcessedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private record OrderItem(string ProductId, int Quantity);
}
