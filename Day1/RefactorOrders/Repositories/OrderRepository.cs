using Microsoft.EntityFrameworkCore;
using RefactorOrders.Data;
using RefactorOrders.Models;

namespace RefactorOrders.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly ApplicationDbContext _context;

    public OrderRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> HasOrderForCustomerAsync(string customerName, CancellationToken cancellationToken)
    {
        return await _context.Orders.AnyAsync(x => x.CustomerName == customerName, cancellationToken);
    }

    public async Task CreateOrderAsync(Order order, CancellationToken cancellationToken)
    {
        _context.Orders.Add(order);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<Order?> GetLatestOrderAsync(CancellationToken cancellationToken)
    {
        return await _context.Orders
            .Include(x => x.Items)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
