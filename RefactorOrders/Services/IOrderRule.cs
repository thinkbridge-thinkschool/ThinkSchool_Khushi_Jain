namespace RefactorOrders.Services;

public interface IOrderRule
{
    void Apply(OrderProcessingContext context);
}
