using RefactorOrders.Models;

namespace RefactorOrders.Repositories;

public interface IOrderRepository
{
    Task<bool> HasOrderForCustomerAsync(string customerName, CancellationToken cancellationToken);

    Task CreateOrderAsync(Order order, CancellationToken cancellationToken);

    Task<Order?> GetLatestOrderAsync(CancellationToken cancellationToken);
}
