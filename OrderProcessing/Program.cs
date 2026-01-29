using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderProcessing.Data;
using OrderProcessing.DTOs;
using OrderProcessing.Entities;
using OrderProcessing.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres") 
    ?? "Host=localhost;Port=5432;Database=ordersdb;Username=postgres;Password=postgres";
var rabbitHost = builder.Configuration["RabbitMq:HostName"] ?? "localhost";
var rabbitQueue = builder.Configuration["RabbitMq:QueueName"] ?? "orders_queue";

builder.Services.AddDbContext<AppDbContext>(options => 
    options.UseNpgsql(connectionString));

builder.Services.AddSingleton<IRabbitMqService>(sp => 
    RabbitMqService.CreateAsync(rabbitHost, rabbitQueue).GetAwaiter().GetResult());
builder.Services.AddSingleton(sp => (RabbitMqService)sp.GetRequiredService<IRabbitMqService>());

builder.Services.AddHostedService<OrderProcessingWorker>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddOpenApi();

var app = builder.Build();

// Auto-migrate database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPost("/order", async (CreateOrderDto dto, AppDbContext db) =>
{
    var existing = await db.Orders.FindAsync(dto.Id);
    if (existing != null)
        return Results.Accepted($"/order/{dto.Id}", new { dto.Id, Message = "Order already exists" });

    // Outbox pattern: save order + outbox in single transaction
    await using var tx = await db.Database.BeginTransactionAsync();
    
    var order = new Order
    {
        Id = dto.Id,
        CustomerId = dto.CustomerId,
        ItemsJson = JsonSerializer.Serialize(dto.Items),
        TotalAmount = dto.ExpectedTotalAmount,
        Status = OrderStatus.Pending,
        CreatedAt = DateTime.UtcNow
    };
    db.Orders.Add(order);

    var outbox = new OutboxMessage { OrderId = dto.Id, CreatedAt = DateTime.UtcNow };
    db.OutboxMessages.Add(outbox);

    await db.SaveChangesAsync();
    await tx.CommitAsync();

    return Results.Accepted($"/order/{dto.Id}", new { dto.Id, Message = "Order accepted for processing" });
})
.WithName("PostOrder");

app.MapGet("/order/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var order = await db.Orders.FindAsync(id);
    return order is null ? Results.NotFound() : Results.Ok(order);
})
.WithName("GetOrder");

app.MapGet("/guid", () => Guid.NewGuid())
    .WithName("GetGuid");

app.MapGet("/stats", async (StatsService statsService) =>
    Results.Ok(await statsService.GetStatsAsync()))
.WithName("GetStats");

app.Run();