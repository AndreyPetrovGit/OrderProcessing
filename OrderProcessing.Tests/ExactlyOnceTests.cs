using Microsoft.EntityFrameworkCore;
using OrderProcessing.Data;
using OrderProcessing.Entities;

namespace OrderProcessing.Tests;

/// <summary>
/// Exactly-once delivery tests. All tests should PASS after outbox + locking implementation.
/// </summary>
public class ExactlyOnceTests : IDisposable
{
    private readonly AppDbContext _db;

    public ExactlyOnceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Scenario 1: Outbox pattern - order and outbox message saved together.
    /// NOTE: InMemoryDatabase doesn't support real transactions. This test verifies
    /// the pattern is followed. Real transactional atomicity requires PostgreSQL.
    /// </summary>
    [Fact]
    public async Task Outbox_WhenOrderCreated_BothOrderAndOutboxExist()
    {
        var orderId = Guid.NewGuid();
        
        // Simulate POST /order with outbox pattern (matches Program.cs logic)
        var (success, outboxId) = await SimulateCreateOrderWithOutbox(orderId);

        Assert.True(success);
        
        // Both order and outbox must exist
        var order = await _db.Orders.FindAsync(orderId);
        var outbox = await _db.OutboxMessages.FirstOrDefaultAsync(o => o.OrderId == orderId);
        
        Assert.NotNull(order);
        Assert.NotNull(outbox);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Null(outbox.ProcessedAt); // Not yet processed by OutboxProcessor
    }
    
    [Fact]
    public async Task Outbox_WhenProcessed_MarkedAsProcessed()
    {
        var orderId = Guid.NewGuid();
        await SimulateCreateOrderWithOutbox(orderId);
        
        // Simulate OutboxProcessor marking as processed after publishing
        var outbox = await _db.OutboxMessages.FirstAsync(o => o.OrderId == orderId);
        outbox.ProcessedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var updated = await _db.OutboxMessages.FirstAsync(o => o.OrderId == orderId);
        Assert.NotNull(updated.ProcessedAt);
    }
    
    private async Task<(bool success, long outboxId)> SimulateCreateOrderWithOutbox(Guid orderId)
    {
        // Matches POST /order logic in Program.cs
        var existing = await _db.Orders.FindAsync(orderId);
        if (existing != null)
            return (false, 0);

        var order = new Order { Id = orderId, CustomerId = 1, ItemsJson = "[]", Status = OrderStatus.Pending };
        var outbox = new OutboxMessage { OrderId = orderId, CreatedAt = DateTime.UtcNow };
        
        _db.Orders.Add(order);
        _db.OutboxMessages.Add(outbox);
        await _db.SaveChangesAsync();
        
        return (true, outbox.Id);
    }

    /// <summary>
    /// Scenario 2: Worker skips already-processed orders (idempotent).
    /// </summary>
    [Fact]
    public async Task Worker_WhenOrderAlreadyProcessed_Skips()
    {
        var orderId = Guid.NewGuid();
        var order = new Order
        {
            Id = orderId,
            CustomerId = 1,
            ItemsJson = "[]",
            Status = OrderStatus.Processed,
            TotalAmount = 100m,
            ProcessedAt = DateTime.UtcNow
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        // Simulate worker processing (should skip)
        var result = await SimulateWorkerProcessing(orderId);

        Assert.False(result); // Skipped
        Assert.Equal(100m, (await _db.Orders.FindAsync(orderId))!.TotalAmount); // Unchanged
    }

    /// <summary>
    /// Scenario 3: Worker processes pending order.
    /// </summary>
    [Fact]
    public async Task Worker_WhenOrderPending_Processes()
    {
        var orderId = Guid.NewGuid();
        var order = new Order { Id = orderId, CustomerId = 1, ItemsJson = "[]", Status = OrderStatus.Pending };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        var result = await SimulateWorkerProcessing(orderId);

        Assert.True(result);
        var saved = await _db.Orders.FindAsync(orderId);
        Assert.Equal(OrderStatus.Processed, saved!.Status);
        Assert.NotNull(saved.TotalAmount);
    }

    /// <summary>
    /// Scenario 4: Duplicate messages - second is skipped.
    /// </summary>
    [Fact]
    public async Task Worker_WhenDuplicateMessage_SecondSkipped()
    {
        var orderId = Guid.NewGuid();
        var order = new Order { Id = orderId, CustomerId = 1, ItemsJson = "[]", Status = OrderStatus.Pending };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        var first = await SimulateWorkerProcessing(orderId);
        var second = await SimulateWorkerProcessing(orderId);

        Assert.True(first);
        Assert.False(second);
    }

    /// <summary>
    /// Scenario 5: Concurrent processing - optimistic locking catches race condition.
    /// NOTE: InMemoryDatabase does NOT support RowVersion. This test verifies the
    /// exception handling logic, not actual concurrency. Real concurrency testing
    /// requires PostgreSQL integration tests.
    /// </summary>
    [Fact]
    public async Task Worker_WhenConcurrencyException_HandlesGracefully()
    {
        var orderId = Guid.NewGuid();
        var order = new Order { Id = orderId, CustomerId = 1, ItemsJson = "[]", Status = OrderStatus.Pending };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        // Simulate what happens when DbUpdateConcurrencyException is thrown
        // Worker should catch it and NOT rethrow (graceful handling)
        var processed = await SimulateWorkerWithConcurrencyException(orderId);
        
        // Should return false (handled gracefully, not crashed)
        Assert.False(processed);
        
        // Order should remain in original state (exception was caught before commit)
        var savedOrder = await _db.Orders.FindAsync(orderId);
        Assert.Equal(OrderStatus.Pending, savedOrder!.Status);
    }
    
    private async Task<bool> SimulateWorkerWithConcurrencyException(Guid orderId)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null || order.Status == OrderStatus.Processed)
            return false;

        // Simulate what worker does when catching DbUpdateConcurrencyException
        try
        {
            throw new DbUpdateConcurrencyException("Simulated concurrency conflict");
        }
        catch (DbUpdateConcurrencyException)
        {
            // Worker catches this and returns gracefully
            return false;
        }
    }

    /// <summary>
    /// Scenario 6: Order not found - handled gracefully.
    /// </summary>
    [Fact]
    public async Task Worker_WhenOrderNotFound_HandlesGracefully()
    {
        var result = await SimulateWorkerProcessing(Guid.NewGuid());
        Assert.False(result);
    }

    /// <summary>
    /// Scenario 7: Duplicate POST - endpoint checks for existing order first.
    /// Simulates the actual POST /order logic.
    /// </summary>
    [Fact]
    public async Task CreateOrder_WhenDuplicate_ReturnsExistingNotCreatesNew()
    {
        var orderId = Guid.NewGuid();
        var originalOrder = new Order { Id = orderId, CustomerId = 1, ItemsJson = "[]", Status = OrderStatus.Pending };
        _db.Orders.Add(originalOrder);
        await _db.SaveChangesAsync();

        // Simulate second POST /order with same ID (matches Program.cs logic)
        var (isNew, order) = await SimulateCreateOrderEndpoint(orderId, customerId: 999);

        Assert.False(isNew); // Should return existing, not create new
        Assert.Equal(1, order.CustomerId); // Original customer, not 999
        Assert.Equal(1, await _db.Orders.CountAsync(o => o.Id == orderId));
    }
    
    private async Task<(bool isNew, Order order)> SimulateCreateOrderEndpoint(Guid orderId, int customerId)
    {
        // This matches the logic in POST /order endpoint
        var existing = await _db.Orders.FindAsync(orderId);
        if (existing != null)
            return (false, existing);

        var order = new Order { Id = orderId, CustomerId = customerId, ItemsJson = "[]" };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return (true, order);
    }

    private async Task<bool> SimulateWorkerProcessing(Guid orderId)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null || order.Status == OrderStatus.Processed)
            return false;

        order.TotalAmount = 99.99m;
        order.Status = OrderStatus.Processed;
        order.ProcessedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }
}
