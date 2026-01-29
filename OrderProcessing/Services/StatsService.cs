using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderProcessing.Data;
using OrderProcessing.Entities;

namespace OrderProcessing.Services;

public class StatsService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public StatsService(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<StatsResult> GetStatsAsync()
    {
        var now = DateTime.UtcNow;
        var oneMinuteAgo = now.AddMinutes(-1);

        var totalByStatus = await _db.Orders
            .GroupBy(o => o.Status)
            .Select(g => new StatusCount(g.Key.ToString(), g.Count()))
            .ToListAsync();

        var createdLastMinute = await _db.Orders
            .CountAsync(o => o.CreatedAt >= oneMinuteAgo);

        var processedLastMinute = await _db.Orders
            .CountAsync(o => o.Status == OrderStatus.Processed && o.ProcessedAt >= oneMinuteAgo);

        var queueDepth = await GetQueueDepthAsync();

        return new StatsResult(
            Timestamp: now,
            Queue: new QueueStats(queueDepth),
            LastMinute: new LastMinuteStats(createdLastMinute, processedLastMinute),
            TotalByStatus: totalByStatus,
            TotalOrders: totalByStatus.Sum(x => x.Count)
        );
    }

    private async Task<int?> GetQueueDepthAsync()
    {
        try
        {
            var rabbitHost = _config["RabbitMq:HostName"] ?? "localhost";
            var queueName = _config["RabbitMq:QueueName"] ?? "orders_queue";

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("guest:guest")));

            var response = await http.GetAsync($"http://{rabbitHost}:15672/api/queues/%2F/{queueName}");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                return json.GetProperty("messages").GetInt32();
            }
        }
        catch
        {
            // RabbitMQ Management API not available
        }

        return null;
    }
}

public record StatsResult(
    DateTime Timestamp,
    QueueStats Queue,
    LastMinuteStats LastMinute,
    List<StatusCount> TotalByStatus,
    int TotalOrders
);

public record QueueStats(int? MessagesInQueue);
public record LastMinuteStats(int Created, int Processed);
public record StatusCount(string Status, int Count);
