namespace OrderProcessing.Entities;

public enum OrderStatus
{
    Pending,
    Processed
}

public class Order
{
    public Guid Id { get; set; }
    public int CustomerId { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public decimal? TotalAmount { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}