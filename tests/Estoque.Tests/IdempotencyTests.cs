using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Estoque.Api.Data;
using Estoque.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Estoque.Tests;

public class IdempotencyTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly StockDb db;
    private readonly InventoryService service;

    public IdempotencyTests()
    {
        connection.Open();
        db = new StockDb(new DbContextOptionsBuilder<StockDb>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        service = new InventoryService(db);
    }

    public void Dispose() { db.Dispose(); connection.Dispose(); }

    [Fact]
    public async Task RetryReturnsOriginalOrderEvenAfterStockRunsOut()
    {
        var product = await service.Create(new("RETRY", "Mouse", "Fornecedor", 1, 40));
        await service.Receive(product.Id, new(1, "Entrada"));
        var key = Guid.NewGuid();
        var request = new NewOrder(product.Id, 1, "Cliente");

        var first = await service.Sell(request, key);
        var retry = await service.Sell(request, key);

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(first.CreatedAt, retry.CreatedAt);
        Assert.Equal(0, await db.Products.AsNoTracking().Select(p => p.Stock).SingleAsync());
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(2, await db.Movements.CountAsync());
    }

    [Fact]
    public async Task SameKeyWithDifferentDataIsRejectedWithoutChangingStock()
    {
        var product = await service.Create(new("CONFLICT", "Teclado", "Fornecedor", 1, 80));
        await service.Receive(product.Id, new(5, "Entrada"));
        var key = Guid.NewGuid();
        await service.Sell(new(product.Id, 1, "Ana"), key);

        await Assert.ThrowsAsync<BusinessException>(() => service.Sell(new(product.Id, 2, "Ana"), key));
        await Assert.ThrowsAsync<BusinessException>(() => service.Sell(new(product.Id, 1, "Bia"), key));
        Assert.Equal(4, await db.Products.AsNoTracking().Select(p => p.Stock).SingleAsync());
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task FailedSaleDoesNotReserveTheRequestKey()
    {
        var product = await service.Create(new("LATER", "Monitor", "Fornecedor", 1, 200));
        var key = Guid.NewGuid();
        var request = new NewOrder(product.Id, 1, "Cliente");
        await Assert.ThrowsAsync<BusinessException>(() => service.Sell(request, key));
        await service.Receive(product.Id, new(1, "Reposição"));
        await service.Sell(request, key);
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task ConcurrentRetriesCreateOnlyOneOrder(int initialStock)
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var options = new DbContextOptionsBuilder<StockDb>().UseSqlite("Data Source=" + file + ";Pooling=False").Options;
        var key = Guid.NewGuid();
        try
        {
            await using (var setup = new StockDb(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var inventory = new InventoryService(setup);
                var product = await inventory.Create(new("RACE", "Produto", "Fornecedor", 1, 10));
                await inventory.Receive(product.Id, new(initialStock, "Entrada"));
            }
            async Task<int> Buy()
            {
                await using var context = new StockDb(options);
                return (await new InventoryService(context).Sell(new(1, 1, "Cliente"), key)).Id;
            }
            var orders = await Task.WhenAll(Task.Run(Buy), Task.Run(Buy));
            Assert.Equal(orders[0], orders[1]);
            await using var check = new StockDb(options);
            Assert.Equal(initialStock - 1, (await check.Products.SingleAsync()).Stock);
            Assert.Equal(1, await check.Orders.CountAsync());
            Assert.Equal(2, await check.Movements.CountAsync());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task ApiAcceptsRetryHeaderAndRejectsMalformedOrReusedKey()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["API_KEY"] = "test-key-at-least-16",
                    ["DatabaseProvider"] = "Sqlite",
                    ["ConnectionStrings:Stock"] = "Data Source=" + file + ";Pooling=False"
                })));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", "test-key-at-least-16");
            var productResponse = await client.PostAsJsonAsync("/api/products", new NewProduct("API", "Produto", "Fornecedor", 1, 10));
            productResponse.EnsureSuccessStatusCode();
            var id = (await productResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            (await client.PostAsJsonAsync($"/api/products/{id}/receive", new StockEntry(2, "Entrada"))).EnsureSuccessStatusCode();

            client.DefaultRequestHeaders.Add("Idempotency-Key", "not-a-uuid");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/orders", new NewOrder(id, 1, "Cliente"))).StatusCode);
            client.DefaultRequestHeaders.Remove("Idempotency-Key");
            client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var first = await client.PostAsJsonAsync("/api/orders", new NewOrder(id, 1, "Cliente"));
            var second = await client.PostAsJsonAsync("/api/orders", new NewOrder(id, 1, "Cliente"));
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/orders", new NewOrder(id, 2, "Cliente"))).StatusCode);
        }
        finally { File.Delete(file); }
    }
}
