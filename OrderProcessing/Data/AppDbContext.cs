using Microsoft.EntityFrameworkCore;
using OrderProcessing.Entities;

namespace OrderProcessing.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Inventory> Inventory => Set<Inventory>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.Status).HasConversion<string>();
            e.Property(o => o.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<Inventory>(e =>
        {
            e.HasKey(i => i.ProductId);
            e.HasData(
                new Inventory { ProductId = "PROD-001", Quantity = 100, Price = 29.99m },
                new Inventory { ProductId = "PROD-002", Quantity = 50, Price = 49.99m },
                new Inventory { ProductId = "PROD-003", Quantity = 200, Price = 9.99m }
            );
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.ProcessedAt);
        });
    }
}
