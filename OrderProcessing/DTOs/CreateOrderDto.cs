namespace OrderProcessing.DTOs;

public class CreateOrderDto
{
    public Guid Id { get; set; }
    public int CustomerId { get; set; }
    public List<string> Items { get; set; } = new List<string>();
}