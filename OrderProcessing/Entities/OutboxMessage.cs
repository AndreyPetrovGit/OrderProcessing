namespace OrderProcessing.Entities;

public class OutboxMessage
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
