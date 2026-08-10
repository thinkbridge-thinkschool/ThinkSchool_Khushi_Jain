using Microsoft.EntityFrameworkCore;
using RefactorOrders.Models;

namespace RefactorOrders.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>()
            .HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(x => x.OrderId);

        base.OnModelCreating(modelBuilder);
    }
}
