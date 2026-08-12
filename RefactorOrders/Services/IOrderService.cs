using RefactorOrders.Models;

namespace RefactorOrders.Services;

public interface IOrderService
{
    Task<OrderProcessingResult> CreateOrderAsync(OrderRequest request, CancellationToken cancellationToken);
}
