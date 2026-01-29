using Microsoft.EntityFrameworkCore;
using OrderProcessing.Entities;

namespace OrderProcessing.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Inventory> Inventory => Set<Inventory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.Status).HasConversion<string>();
        });

        modelBuilder.Entity<Inventory>(e =>
        {
            e.HasKey(i => i.ProductId);
        });
    }
}
