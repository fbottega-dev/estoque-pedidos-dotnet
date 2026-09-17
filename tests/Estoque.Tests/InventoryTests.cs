using System.Net;
using System.Net.Http.Json;
using Estoque.Api.Data;
using Estoque.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Tests;

public class InventoryTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly StockDb db;
    private readonly InventoryService service;
    public InventoryTests() { connection.Open(); db = new StockDb(new DbContextOptionsBuilder<StockDb>().UseSqlite(connection).Options); db.Database.EnsureCreated(); service = new InventoryService(db); }
    public void Dispose() { db.Dispose(); connection.Dispose(); }
    [Fact]
    public async Task SaleRegistersOrderAndMovementAndCannotOversell()
    {
        var p = await service.Create(new("SKU-01", "Teclado", "Fornecedor", 2, 100));
        await service.Receive(p.Id, new(3, "Saldo inicial"));
        var order = await service.Sell(new(p.Id, 2, "Cliente"));
        Assert.Equal(100, order.UnitPrice);
        Assert.Equal(1, await db.Products.AsNoTracking().Where(x => x.Id == p.Id).Select(x => x.Stock).SingleAsync());
        await Assert.ThrowsAsync<BusinessException>(() => service.Sell(new(p.Id, 2, "Outro cliente")));
        Assert.Equal(1, await db.Orders.CountAsync()); Assert.Equal(2, await db.Movements.CountAsync());
    }
    [Fact]
    public async Task ValidationRejectsNegativeStockAndDuplicateSku()
    {
        var p = await service.Create(new("sku-02", "Mouse", "Fornecedor", 1, 20));
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => service.Receive(p.Id, new(-1, "Inválido")));
        await Assert.ThrowsAsync<BusinessException>(() => service.Create(new("SKU-02", "Outro", "Fornecedor", 0, 20)));
        await Assert.ThrowsAsync<BusinessException>(() => service.Create(new("SKU-03", "Outro", "Fornecedor", 0, 20.001m)));
    }
    [Fact]
    public async Task FailedSaleLeavesNoOrderOrMovement()
    {
        var p = await service.Create(new("SKU-04", "Monitor", "Fornecedor", 1, 300));
        await Assert.ThrowsAsync<BusinessException>(() => service.Sell(new(p.Id, 1, "Cliente")));
        Assert.Empty(await db.Orders.ToListAsync()); Assert.Empty(await db.Movements.ToListAsync());
    }
    [Fact]
    public async Task OnlyOneBuyerGetsLastUnit()
    {
        var filename = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var options = new DbContextOptionsBuilder<StockDb>().UseSqlite("Data Source=" + filename + ";Pooling=False").Options;
        try
        {
            await using (var setup = new StockDb(options)) { await setup.Database.EnsureCreatedAsync(); var s = new InventoryService(setup); var p = await s.Create(new("LAST", "Última unidade", "Fornecedor", 1, 5)); await s.Receive(p.Id, new(1, "Entrada")); }
            async Task<bool> Buy() { await using var context = new StockDb(options); try { await new InventoryService(context).Sell(new(1, 1, "Cliente")); return true; } catch (BusinessException) { return false; } }
            var results = await Task.WhenAll(Task.Run(Buy), Task.Run(Buy)); Assert.Single(results, x => x);
            await using var check = new StockDb(options); Assert.Equal(0, (await check.Products.SingleAsync()).Stock); Assert.Equal(1, await check.Orders.CountAsync());
        }
        finally { File.Delete(filename); }
    }
}
public class ApiTests
{
    [Fact]
    public async Task ApiRequiresKeyAndValidatesRequests()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["API_KEY"] = "test-key-at-least-16", ["DatabaseProvider"] = "Sqlite", ["ConnectionStrings:Stock"] = "Data Source=" + file + ";Pooling=False" })));
            using var client = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/products")).StatusCode);
            client.DefaultRequestHeaders.Add("X-Api-Key", "test-key-at-least-16");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/products", new { sku = "", name = "", supplier = "", minimumStock = -1, price = -1 })).StatusCode);
        }
        finally { File.Delete(file); }
    }
}
