namespace OrderProcessing.Entities;

public class Order
{
    public Guid Id { get; set; }
    public int CustomerId { get; set; }
    public List<OrderItem> Items { get; set; } = new List<OrderItem>();
    public decimal TotalAmount { get; set; }
}