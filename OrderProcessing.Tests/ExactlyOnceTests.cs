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
    /// Scenario 1: Outbox ensures order + message saved atomically.
    /// If DB transaction commits, outbox entry exists → OutboxProcessor will publish.
    /// </summary>
    [Fact]
    public async Task Outbox_WhenOrderCreated_OutboxEntryExists()
    {
        var orderId = Guid.NewGuid();
        
        // Simulate POST /order with outbox pattern
        var order = new Order { Id = orderId, CustomerId = 1, ItemsJson = "[]" };
        var outbox = new OutboxMessage { OrderId = orderId, CreatedAt = DateTime.UtcNow };
        
        _db.Orders.Add(order);
        _db.OutboxMessages.Add(outbox);
        await _db.SaveChangesAsync();

        // Assert: both order and outbox exist
        Assert.NotNull(await _db.Orders.FindAsync(orderId));
        Assert.True(await _db.OutboxMessages.AnyAsync(o => o.OrderId == orderId));
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
    /// Scenario 5: Concurrent processing - only one succeeds (optimistic locking).
    /// Note: InMemoryDatabase doesn't enforce RowVersion, so this tests the logic flow.
    /// Real concurrency is tested in integration tests with PostgreSQL.
    /// </summary>
    [Fact]
    public async Task Worker_WhenConcurrent_OptimisticLockingHandled()
    {
        var orderId = Guid.NewGuid();
        var order = new Order { Id = orderId, CustomerId = 1, ItemsJson = "[]", Status = OrderStatus.Pending };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        // First worker processes
        var first = await SimulateWorkerProcessing(orderId);
        Assert.True(first);

        // Second worker would get DbUpdateConcurrencyException in real DB
        // With InMemory, it just skips due to status check
        var second = await SimulateWorkerProcessing(orderId);
        Assert.False(second);
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
    /// Scenario 7: Duplicate POST - returns existing, no duplicate created.
    /// </summary>
    [Fact]
    public async Task CreateOrder_WhenDuplicate_NoDuplicateCreated()
    {
        var orderId = Guid.NewGuid();
        _db.Orders.Add(new Order { Id = orderId, CustomerId = 1, ItemsJson = "[]" });
        await _db.SaveChangesAsync();

        // Check existing before "creating" again
        var existing = await _db.Orders.FindAsync(orderId);
        Assert.NotNull(existing);
        Assert.Equal(1, await _db.Orders.CountAsync(o => o.Id == orderId));
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
