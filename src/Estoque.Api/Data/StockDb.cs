using Estoque.Api.Domain;
using Microsoft.EntityFrameworkCore;
namespace Estoque.Api.Data;

public sealed class StockDb(DbContextOptions<StockDb> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Movement> Movements => Set<Movement>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Product>().HasIndex(p => p.Sku).IsUnique();
        model.Entity<Product>().Property(p => p.Sku).HasMaxLength(40);
        model.Entity<Product>().Property(p => p.Name).HasMaxLength(120);
        model.Entity<Product>().Property(p => p.Supplier).HasMaxLength(120);
        model.Entity<Product>().Property(p => p.Price).HasPrecision(12, 2);
        model.Entity<Product>().ToTable(t => t.HasCheckConstraint("CK_Product_Stock", "\"Stock\" >= 0"));
        model.Entity<Order>().Property(o => o.UnitPrice).HasPrecision(12, 2);
        model.Entity<Order>().Property(o => o.CreatedAt).HasConversion(
            value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        model.Entity<Order>().HasIndex(o => o.RequestKey).IsUnique();
        model.Entity<Order>().Property(o => o.Customer).HasMaxLength(120);
        model.Entity<Order>().HasOne(o => o.Product).WithMany().HasForeignKey(o => o.ProductId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Movement>().Property(m => m.Reason).HasMaxLength(200);
        model.Entity<Movement>().HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}
