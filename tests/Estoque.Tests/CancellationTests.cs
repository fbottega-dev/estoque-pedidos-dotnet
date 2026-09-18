using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Estoque.Api.Data;
using Estoque.Api.Domain;
using Estoque.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Estoque.Tests;

public class CancellationTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly StockDb db;
    private readonly InventoryService service;

    public CancellationTests()
    {
        connection.Open();
        db = new StockDb(new DbContextOptionsBuilder<StockDb>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        service = new InventoryService(db);
    }

    public void Dispose() { db.Dispose(); connection.Dispose(); }

    [Fact]
    public async Task CancellationRestoresTheSoldQuantityAndKeepsTheOrderHistory()
    {
        var product = await service.Create(new("CANCEL", "Mouse", "Fornecedor", 1, 40));
        await service.Receive(product.Id, new(5, "Entrada"));
        var sale = await service.Sell(new(product.Id, 3, "Ana"));

        var cancelled = await service.Cancel(sale.Id, new("  Cliente desistiu  "));

        Assert.Equal(sale.Id, cancelled.Id);
        Assert.Equal(sale.CreatedAt, cancelled.CreatedAt);
        Assert.Equal(3, cancelled.Quantity);
        Assert.Equal(40, cancelled.UnitPrice);
        Assert.Equal("Ana", cancelled.Customer);
        Assert.NotNull(cancelled.CancelledAt);
        Assert.Equal(DateTimeKind.Utc, cancelled.CancelledAt.Value.Kind);
        Assert.Equal("Cliente desistiu", cancelled.CancellationReason);
        Assert.Equal(5, await db.Products.AsNoTracking().Select(p => p.Stock).SingleAsync());
        Assert.Equal(1, await db.Orders.CountAsync());
        var movements = await db.Movements.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(new[] { 5, -3, 3 }, movements.Select(m => m.Quantity));
        Assert.All(movements, movement => Assert.Equal(product.Id, movement.ProductId));
        var saved = await db.Orders.AsNoTracking().SingleAsync();
        Assert.Equal(cancelled.CancelledAt, saved.CancelledAt);
        Assert.Equal(cancelled.CancellationReason, saved.CancellationReason);
    }

    [Theory]
    [InlineData("Cliente desistiu")]
    [InlineData("Outro motivo")]
    public async Task RepeatedCancellationKeepsTheFirstReasonAndDoesNotRestockTwice(string retryReason)
    {
        var product = await service.Create(new("REPEAT", "Teclado", "Fornecedor", 1, 80));
        await service.Receive(product.Id, new(3, "Entrada"));
        var sale = await service.Sell(new(product.Id, 2, "Ana"));
        var first = await service.Cancel(sale.Id, new("Cliente desistiu"));
        await service.Sell(new(product.Id, 1, "Bia"));

        var retry = await service.Cancel(sale.Id, new(retryReason));

        Assert.Equal(first.CancelledAt, retry.CancelledAt);
        Assert.Equal("Cliente desistiu", retry.CancellationReason);
        Assert.Equal(2, await db.Products.AsNoTracking().Select(p => p.Stock).SingleAsync());
        Assert.Equal(2, await db.Orders.CountAsync());
        Assert.Equal(4, await db.Movements.CountAsync());
    }

    [Fact]
    public async Task InvalidReasonsLeaveTheSaleAndStockUnchanged()
    {
        var product = await service.Create(new("REASON", "Monitor", "Fornecedor", 1, 200));
        await service.Receive(product.Id, new(1, "Entrada"));
        var sale = await service.Sell(new(product.Id, 1, "Cliente"));

        foreach (var reason in new string?[] { null, "", "   ", new string('x', 161) })
            await Assert.ThrowsAsync<ValidationException>(() => service.Cancel(sale.Id, new(reason!)));

        var saved = await db.Orders.AsNoTracking().SingleAsync();
        Assert.Null(saved.CancelledAt);
        Assert.Null(saved.CancellationReason);
        Assert.Equal(0, await db.Products.AsNoTracking().Select(p => p.Stock).SingleAsync());
        Assert.Equal(2, await db.Movements.CountAsync());
    }

    [Fact]
    public async Task MissingOrderReturnsNotFoundWithoutCreatingMovements()
    {
        var error = await Assert.ThrowsAsync<BusinessException>(() => service.Cancel(999, new("Pedido incorreto")));

        Assert.Equal(404, error.Status);
        Assert.Empty(await db.Orders.ToListAsync());
        Assert.Empty(await db.Movements.ToListAsync());
    }

    [Fact]
    public async Task StockLimitRollsBackTheCancellationAndAllowsAnotherAttempt()
    {
        var product = await service.Create(new("LIMIT", "Produto", "Fornecedor", 1, 10));
        await service.Receive(product.Id, new(1, "Entrada"));
        var sale = await service.Sell(new(product.Id, 1, "Ana"));
        await service.Receive(product.Id, new(1_000_000, "Reposição"));

        var error = await Assert.ThrowsAsync<BusinessException>(() => service.Cancel(sale.Id, new("Desistência")));

        Assert.Equal(409, error.Status);
        var saved = await db.Orders.AsNoTracking().SingleAsync();
        Assert.Null(saved.CancelledAt);
        Assert.Null(saved.CancellationReason);
        Assert.Equal(1_000_000, await db.Products.AsNoTracking().Select(p => p.Stock).SingleAsync());
        Assert.Equal(3, await db.Movements.CountAsync());

        await service.Sell(new(product.Id, 1, "Bia"));
        var cancelled = await service.Cancel(sale.Id, new("Cliente confirmou a desistência"));
        Assert.NotNull(cancelled.CancelledAt);
        Assert.Equal("Cliente confirmou a desistência", cancelled.CancellationReason);
        Assert.Equal(1_000_000, await db.Products.AsNoTracking().Select(p => p.Stock).SingleAsync());
        Assert.Equal(5, await db.Movements.CountAsync());
    }

    [Fact]
    public async Task ReplayingACancelledSaleReturnsItsCancellationWithoutSellingAgain()
    {
        var product = await service.Create(new("REPLAY", "Produto", "Fornecedor", 1, 10));
        await service.Receive(product.Id, new(2, "Entrada"));
        var key = Guid.NewGuid();
        var request = new NewOrder(product.Id, 1, "Ana");
        var sale = await service.Sell(request, key);
        var cancelled = await service.Cancel(sale.Id, new("Desistência"));

        var replay = await service.Sell(request, key);

        Assert.Equal(sale.Id, replay.Id);
        Assert.Equal(cancelled.CancelledAt, replay.CancelledAt);
        Assert.Equal(cancelled.CancellationReason, replay.CancellationReason);
        Assert.Equal(2, await db.Products.AsNoTracking().Select(p => p.Stock).SingleAsync());
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(3, await db.Movements.CountAsync());
        var newSale = await service.Sell(request, Guid.NewGuid());
        Assert.NotEqual(sale.Id, newSale.Id);
        Assert.Null(newSale.CancelledAt);
        Assert.Equal(1, await db.Products.AsNoTracking().Select(p => p.Stock).SingleAsync());
    }

    [Fact]
    public async Task ConcurrentCancellationsRestoreStockOnlyOnce()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var options = new DbContextOptionsBuilder<StockDb>().UseSqlite("Data Source=" + file + ";Pooling=False").Options;
        try
        {
            int orderId;
            await using (var setup = new StockDb(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var inventory = new InventoryService(setup);
                var product = await inventory.Create(new("RACE-CANCEL", "Produto", "Fornecedor", 1, 10));
                await inventory.Receive(product.Id, new(5, "Entrada"));
                orderId = (await inventory.Sell(new(product.Id, 3, "Cliente"))).Id;
            }
            using var ready = new CountdownEvent(2);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<Order> Cancel(string reason)
            {
                await using var context = new StockDb(options);
                ready.Signal();
                await start.Task;
                return await new InventoryService(context).Cancel(orderId, new(reason));
            }
            var first = Cancel("Primeira tentativa");
            var second = Cancel("Segunda tentativa");
            Assert.True(ready.IsSet);
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            Assert.NotNull(results[0].CancelledAt);
            Assert.Equal(results[0].CancelledAt, results[1].CancelledAt);
            Assert.Equal(results[0].CancellationReason, results[1].CancellationReason);
            Assert.Contains(results[0].CancellationReason, new[] { "Primeira tentativa", "Segunda tentativa" });
            await using var check = new StockDb(options);
            Assert.Equal(5, (await check.Products.SingleAsync()).Stock);
            Assert.Equal(1, await check.Orders.CountAsync());
            Assert.Equal(new[] { 5, -3, 3 }, await check.Movements.OrderBy(m => m.Id).Select(m => m.Quantity).ToArrayAsync());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task ApiValidatesCancellationAndReturnsTheSavedStateOnRetryAndSaleReplay()
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
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/orders/1/cancel", new { reason = "Desistência" })).StatusCode);
            client.DefaultRequestHeaders.Add("X-Api-Key", "test-key-at-least-16");
            var productResponse = await client.PostAsJsonAsync("/api/products", new NewProduct("API-CANCEL", "Produto", "Fornecedor", 1, 10));
            productResponse.EnsureSuccessStatusCode();
            var productId = (await productResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            (await client.PostAsJsonAsync($"/api/products/{productId}/receive", new StockEntry(2, "Entrada"))).EnsureSuccessStatusCode();
            client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var body = new NewOrder(productId, 1, "Ana");
            var sale = await client.PostAsJsonAsync("/api/orders", body);
            Assert.Equal(HttpStatusCode.Created, sale.StatusCode);
            var original = await sale.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(JsonValueKind.Null, original.GetProperty("cancelledAt").ValueKind);
            var orderId = original.GetProperty("id").GetInt32();

            foreach (var reason in new string?[] { null, "", "   ", new string('x', 161) })
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/orders/{orderId}/cancel", new { reason })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/orders/999/cancel", new { reason = "Desistência" })).StatusCode);

            var cancelled = await client.PostAsJsonAsync($"/api/orders/{orderId}/cancel", new { reason = "Cliente desistiu" });
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            var savedJson = await cancelled.Content.ReadAsStringAsync();
            var saved = JsonSerializer.Deserialize<JsonElement>(savedJson);
            Assert.Equal("Cliente desistiu", saved.GetProperty("cancellationReason").GetString());
            Assert.Equal(JsonValueKind.String, saved.GetProperty("cancelledAt").ValueKind);
            var retry = await client.PostAsJsonAsync($"/api/orders/{orderId}/cancel", new { reason = "Outro motivo" });
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(savedJson, await retry.Content.ReadAsStringAsync());
            var replay = await client.PostAsJsonAsync("/api/orders", body);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(savedJson, await replay.Content.ReadAsStringAsync());

            var orders = await client.GetFromJsonAsync<JsonElement>("/api/orders");
            var listed = Assert.Single(orders.EnumerateArray());
            Assert.Equal(saved.GetProperty("cancelledAt").GetString(), listed.GetProperty("cancelledAt").GetString());
            Assert.Equal("Cliente desistiu", listed.GetProperty("cancellationReason").GetString());
            var products = await client.GetFromJsonAsync<JsonElement>("/api/products");
            Assert.Equal(2, Assert.Single(products.GetProperty("items").EnumerateArray()).GetProperty("stock").GetInt32());
            var movements = await client.GetFromJsonAsync<JsonElement>("/api/movements");
            Assert.Equal(3, movements.GetArrayLength());
        }
        finally { File.Delete(file); }
    }
}
