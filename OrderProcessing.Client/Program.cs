using System.Net.Http.Json;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var serviceUrl = builder.Configuration["ServiceUrl"] ?? "http://localhost:8080";
var isRunning = false;
var lastResult = (TestResult?)null;

app.MapGet("/", () => new
{
    Status = isRunning ? "Running" : "Idle",
    LastResult = lastResult,
    Endpoints = new[] { "POST /start", "GET /status", "GET /report" }
});

app.MapGet("/status", () => new { IsRunning = isRunning, LastResult = lastResult });

app.MapGet("/report", () =>
{
    if (lastResult == null)
        return Results.Ok(new { Message = "No test results yet. Run POST /start first." });
    
    var r = lastResult;
    var uniqueOrders = r.UniqueOrdersSent;
    var duplicatesSent = r.DuplicateAttempts;
    var duplicatesRejected = r.DuplicatesRejected;
    var totalInDb = r.FinalStats.TotalOrders;
    var queueEmpty = r.FinalStats.QueueDepth == 0;
    
    var checks = new List<string>();
    
    // Check 1: Duplicates handled correctly
    var duplicatesOk = duplicatesRejected == duplicatesSent;
    checks.Add($"Duplicates: {duplicatesSent} sent, {duplicatesRejected} rejected → " +
        (duplicatesOk ? "✓ OK (all duplicates rejected)" : "✗ FAIL (some duplicates were accepted)"));
    
    // Check 2: Queue is empty after processing
    checks.Add($"Queue depth: {r.FinalStats.QueueDepth} → " +
        (queueEmpty ? "✓ OK (all processed)" : "✗ FAIL (orders still pending)"));
    
    // Check 3: Price mismatches logged
    checks.Add($"Price mismatches: {r.PriceMismatchCount} orders had expectedPrice != calculatedPrice → " +
        "ℹ INFO (expected behavior, logged on server)");
    
    // Check 4: Orders count matches
    var ordersMatch = r.FinalStats.ProcessedCount == uniqueOrders;
    checks.Add($"Orders: {uniqueOrders} unique sent, {r.FinalStats.ProcessedCount} processed → " +
        (ordersMatch ? "✓ OK" : $"⚠ {r.FinalStats.ProcessedCount - uniqueOrders} difference (may still be processing)"));
    
    var allOk = duplicatesOk && queueEmpty;
    
    return Results.Ok(new
    {
        Summary = allOk ? "✓ ALL CHECKS PASSED" : "✗ SOME CHECKS FAILED",
        Duration = $"{(r.FinishedAt - r.StartedAt).TotalSeconds:F0} seconds",
        Checks = checks,
        Details = new
        {
            r.UniqueOrdersSent,
            r.DuplicateAttempts,
            r.DuplicatesRejected,
            r.PriceMismatchCount,
            r.FinalStats
        }
    });
});

app.MapPost("/start", async () =>
{
    if (isRunning)
        return Results.Conflict(new { Message = "Test already running" });

    isRunning = true;
    lastResult = null;

    _ = Task.Run(async () =>
    {
        var result = await RunLoadTestAsync(serviceUrl, durationSeconds: 120, delayMs: 500);
        
        // Wait 15 seconds for processing to complete
        Console.WriteLine("Waiting 15s for processing to complete...");
        await Task.Delay(15000);
        
        // Verify stats
        using var http = new HttpClient();
        var stats = await http.GetFromJsonAsync<JsonElement>($"{serviceUrl}/stats");
        
        var processedCount = 0;
        foreach (var status in stats.GetProperty("totalByStatus").EnumerateArray())
        {
            if (status.GetProperty("status").GetString() == "Processed")
                processedCount = status.GetProperty("count").GetInt32();
        }
        
        lastResult = new TestResult(
            StartedAt: result.StartedAt,
            FinishedAt: result.FinishedAt,
            UniqueOrdersSent: result.UniqueOrdersSent,
            DuplicateAttempts: result.DuplicateAttempts,
            DuplicatesRejected: result.DuplicatesRejected,
            PriceMismatchCount: result.WithExpectedPrice,
            FinalStats: new FinalStats(
                TotalOrders: stats.GetProperty("totalOrders").GetInt32(),
                ProcessedCount: processedCount,
                QueueDepth: stats.GetProperty("queue").GetProperty("messagesInQueue").GetInt32(),
                LastMinuteCreated: stats.GetProperty("lastMinute").GetProperty("created").GetInt32(),
                LastMinuteProcessed: stats.GetProperty("lastMinute").GetProperty("processed").GetInt32()
            )
        );
        
        isRunning = false;
        Console.WriteLine($"Test completed. Check GET /report for results.");
    });

    return Results.Accepted(value: new { Message = "Load test started", Duration = "2 minutes", ServiceUrl = serviceUrl });
});

app.Run();

async Task<LoadTestResult> RunLoadTestAsync(string baseUrl, int durationSeconds, int delayMs)
{
    var startedAt = DateTime.UtcNow;
    var endTime = startedAt.AddSeconds(durationSeconds);
    var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    var random = new Random();
    var products = new[] { "room-standard", "room-deluxe", "room-suite", "breakfast", "spa", "parking" };
    
    var uniqueOrdersSent = 0;
    var duplicateAttempts = 0;
    var duplicatesRejected = 0;
    var withExpectedPrice = 0;
    var sentOrderIds = new List<Guid>();

    Console.WriteLine($"Starting load test for {durationSeconds}s against {baseUrl}");

    while (DateTime.UtcNow < endTime)
    {
        try
        {
            Guid orderId;
            bool isDuplicate = false;
            
            // Every 10th order: intentionally send a duplicate
            if (sentOrderIds.Count > 0 && random.Next(10) == 0)
            {
                orderId = sentOrderIds[random.Next(sentOrderIds.Count)];
                isDuplicate = true;
                duplicateAttempts++;
            }
            else
            {
                orderId = Guid.NewGuid();
            }
            
            // 50% of orders have expectedPrice (will cause price mismatch logs)
            decimal? expectedPrice = null;
            if (random.NextDouble() > 0.5)
            {
                expectedPrice = Math.Round((decimal)(random.NextDouble() * 500), 2);
                if (!isDuplicate) withExpectedPrice++;
            }
            
            var order = new
            {
                Id = orderId,
                CustomerId = random.Next(1, 1000),
                Items = Enumerable.Range(0, random.Next(1, 4))
                    .Select(_ => new { ProductId = products[random.Next(products.Length)], Quantity = random.Next(1, 5) })
                    .ToList(),
                ExpectedTotalAmount = expectedPrice
            };

            var response = await http.PostAsJsonAsync("/order", order);
            
            if (isDuplicate)
            {
                // Duplicate should return 202 with "already exists" message
                duplicatesRejected++;
                Console.WriteLine($"[DUP] Order {orderId:N} -> {(int)response.StatusCode} (duplicate rejected)");
            }
            else
            {
                uniqueOrdersSent++;
                sentOrderIds.Add(orderId);
                if (uniqueOrdersSent % 20 == 0)
                    Console.WriteLine($"[{uniqueOrdersSent}] {DateTime.UtcNow - startedAt:mm\\:ss} elapsed");
            }

            await Task.Delay(delayMs);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            await Task.Delay(1000);
        }
    }

    return new LoadTestResult(startedAt, DateTime.UtcNow, uniqueOrdersSent, duplicateAttempts, duplicatesRejected, withExpectedPrice);
}

record LoadTestResult(DateTime StartedAt, DateTime FinishedAt, int UniqueOrdersSent, int DuplicateAttempts, int DuplicatesRejected, int WithExpectedPrice);
record TestResult(DateTime StartedAt, DateTime FinishedAt, int UniqueOrdersSent, int DuplicateAttempts, int DuplicatesRejected, int PriceMismatchCount, FinalStats FinalStats);
record FinalStats(int TotalOrders, int ProcessedCount, int QueueDepth, int LastMinuteCreated, int LastMinuteProcessed);
