using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Estoque.Api.Data;

public sealed class StockDbFactory : IDesignTimeDbContextFactory<StockDb>
{
    public StockDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<StockDb>()
        .UseNpgsql("Host=localhost;Database=stock;Username=stock")
        .Options);
}
