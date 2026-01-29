namespace OrderProcessing.DTOs;

public class CreateOrderDto
{
    public Guid Id { get; set; }
    public int CustomerId { get; set; }
    public List<OrderItemDto> Items { get; set; } = new List<OrderItemDto>();
    public decimal? ExpectedTotalAmount { get; set; }
}

public class OrderItemDto
{
    public string ProductId { get; set; } = string.Empty;
    public int Quantity { get; set; }
}