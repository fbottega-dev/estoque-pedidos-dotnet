using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Estoque.Api.Data;
using Estoque.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<StockDb>(options =>
{
    if (builder.Configuration["DatabaseProvider"] == "Sqlite") options.UseSqlite(builder.Configuration.GetConnectionString("Stock") ?? "Data Source=stock.db");
    else options.UseNpgsql(builder.Configuration.GetConnectionString("Stock") ?? throw new InvalidOperationException("Configure ConnectionStrings__Stock."));
});
builder.Services.AddScoped<InventoryService>();
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("api", limiter => { limiter.PermitLimit = 120; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; }));
var app = builder.Build();
var apiKey = app.Configuration["API_KEY"];
if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 16) throw new InvalidOperationException("Configure API_KEY com pelo menos 16 caracteres.");
var sqlite = app.Configuration["DatabaseProvider"] == "Sqlite";
app.UseExceptionHandler();
app.UseDefaultFiles(); app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var supplied = Encoding.UTF8.GetBytes(context.Request.Headers["X-Api-Key"].ToString());
        if (!CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes(apiKey))) { context.Response.StatusCode = 401; return; }
    }
    try { await next(); }
    catch (BadHttpRequestException e) { await Results.Problem("Formato da requisição inválido.", statusCode: e.StatusCode).ExecuteAsync(context); }
    catch (ValidationException e) { await Results.Problem(e.Message, statusCode: 400).ExecuteAsync(context); }
    catch (BusinessException e) { await Results.Problem(e.Message, statusCode: e.Status).ExecuteAsync(context); }
    catch (DbUpdateException) { await Results.Problem("Conflito ao salvar os dados.", statusCode: 409).ExecuteAsync(context); }
});
app.UseRateLimiter();
var api = app.MapGroup("/api").RequireRateLimiting("api");
api.MapGet("/products", async (StockDb db, bool low = false, int page = 0) =>
{
    if (page < 0 || page > 100000) return Results.BadRequest();
    var query = db.Products.AsNoTracking().Where(p => !low || p.Stock < p.MinimumStock);
    return Results.Ok(new { items = await query.OrderBy(p => p.Name).Skip(page * 20).Take(20).ToListAsync(), total = await query.CountAsync(), page });
});
api.MapPost("/products", async (NewProduct input, InventoryService service) => { var product = await service.Create(input); return Results.Created($"/api/products/{product.Id}", product); });
api.MapPost("/products/{id:int}/receive", async (int id, StockEntry input, InventoryService service) => { await service.Receive(id, input); return Results.NoContent(); });
api.MapPost("/orders", async (NewOrder input, [FromHeader(Name = "Idempotency-Key")] Guid? requestKey, InventoryService service) =>
{
    var order = await service.Sell(input, requestKey);
    return Results.Created($"/api/orders/{order.Id}", new { order.Id, order.ProductId, order.Quantity, order.UnitPrice, order.Customer, order.CreatedAt });
});
api.MapGet("/orders", async (StockDb db) => await db.Orders.AsNoTracking().OrderByDescending(o => o.Id).Take(50).Select(o => new { o.Id, o.ProductId, product = o.Product.Name, o.Customer, o.Quantity, o.UnitPrice, o.CreatedAt }).ToListAsync());
api.MapGet("/movements", async (StockDb db) => await db.Movements.AsNoTracking().OrderByDescending(m => m.Id).Take(100).Select(m => new { m.Id, m.ProductId, product = m.Product.Name, m.Quantity, m.Reason, m.CreatedAt }).ToListAsync());
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StockDb>();
    if (sqlite) db.Database.EnsureCreated(); else db.Database.Migrate();
}
app.Run();
public partial class Program { }
