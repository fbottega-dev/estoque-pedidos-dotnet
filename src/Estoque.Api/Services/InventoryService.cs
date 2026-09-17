using System.ComponentModel.DataAnnotations;
using Estoque.Api.Data;
using Estoque.Api.Domain;
using Microsoft.EntityFrameworkCore;
namespace Estoque.Api.Services;

public record NewProduct([property: Required, StringLength(40, MinimumLength = 2)] string Sku,
    [property: Required, StringLength(120)] string Name, [property: Required, StringLength(120)] string Supplier,
    [property: Range(0, 1000000)] int MinimumStock, [property: Range(typeof(decimal), "0.01", "9999999999.99", ParseLimitsInInvariantCulture = true)] decimal Price);
public record NewOrder([property: Range(1, int.MaxValue)] int ProductId, [property: Range(1, 1000000)] int Quantity,
    [property: Required, StringLength(120)] string Customer);
public record StockEntry([property: Range(1, 1000000)] int Quantity, [property: Required, StringLength(200)] string Reason);
public sealed class BusinessException(string message, int status = 409) : Exception(message)
{
    public int Status { get; } = status;
}
public sealed class InventoryService(StockDb db)
{
    public static void Validate(object input)
    {
        Validator.ValidateObject(input, new ValidationContext(input), true);
    }
    public async Task<Product> Create(NewProduct input)
    {
        Validate(input);
        if (input.Price != decimal.Round(input.Price, 2)) throw new BusinessException("Use no máximo duas casas decimais.", 400);
        var sku = input.Sku.Trim().ToUpperInvariant();
        if (await db.Products.AnyAsync(p => p.Sku == sku)) throw new BusinessException("SKU já cadastrado.");
        var product = new Product { Sku = sku, Name = input.Name.Trim(), Supplier = input.Supplier.Trim(), MinimumStock = input.MinimumStock, Price = input.Price };
        db.Products.Add(product); await db.SaveChangesAsync(); return product;
    }
    public async Task Receive(int id, StockEntry input)
    {
        Validate(input);
        await using var tx = await db.Database.BeginTransactionAsync();
        var updated = await db.Products.Where(p => p.Id == id && p.Stock <= 1000000 - input.Quantity)
            .ExecuteUpdateAsync(update => update.SetProperty(p => p.Stock, p => p.Stock + input.Quantity));
        if (updated == 0) throw new BusinessException("Produto inexistente ou limite de estoque excedido.");
        db.Movements.Add(new Movement { ProductId = id, Quantity = input.Quantity, Reason = input.Reason.Trim() });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task<Order> Sell(NewOrder input, Guid? requestKey = null)
    {
        Validate(input);
        if (requestKey == Guid.Empty) throw new BusinessException("A chave do pedido não pode ser um UUID vazio.", 400);
        var replay = await FindPreviousOrder(input, requestKey);
        if (replay is not null) return replay;

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            // O saldo é alterado no banco para que duas vendas não usem a mesma unidade.
            var updated = await db.Products.Where(p => p.Id == input.ProductId && p.Stock >= input.Quantity)
                .ExecuteUpdateAsync(update => update.SetProperty(p => p.Stock, p => p.Stock - input.Quantity));
            if (updated == 0)
            {
                // Outra tentativa do mesmo pedido pode ter acabado de consumir o saldo.
                replay = await FindPreviousOrder(input, requestKey);
                if (replay is not null) return replay;
                throw new BusinessException("Produto inexistente ou estoque insuficiente.");
            }
            var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == input.ProductId);
            var order = new Order { RequestKey = requestKey, ProductId = product.Id, Quantity = input.Quantity, UnitPrice = product.Price, Customer = input.Customer.Trim() };
            db.Orders.Add(order);
            db.Movements.Add(new Movement { ProductId = product.Id, Quantity = -input.Quantity, Reason = "Pedido: " + input.Customer.Trim() });
            await db.SaveChangesAsync();
            await db.Entry(order).ReloadAsync();
            await tx.CommitAsync();
            return order;
        }
        catch (DbUpdateException) when (requestKey.HasValue)
        {
            // A restrição única resolve a corrida entre duas requisições com a mesma chave.
            // Desfaz também a baixa de estoque da tentativa que perdeu essa corrida.
            await tx.RollbackAsync();
            await tx.DisposeAsync();
            db.ChangeTracker.Clear();
            replay = await FindPreviousOrder(input, requestKey);
            if (replay is not null) return replay;
            throw;
        }
    }

    private async Task<Order?> FindPreviousOrder(NewOrder input, Guid? requestKey)
    {
        if (!requestKey.HasValue) return null;
        var previous = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.RequestKey == requestKey);
        if (previous is null) return null;
        if (previous.ProductId != input.ProductId || previous.Quantity != input.Quantity || previous.Customer != input.Customer.Trim())
            throw new BusinessException("Esta chave já foi usada em um pedido com outros dados.");
        return previous;
    }
}
