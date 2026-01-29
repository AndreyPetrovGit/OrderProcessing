namespace OrderProcessing.Entities;

public class Inventory
{
    public string ProductId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}