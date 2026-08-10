namespace RefactorOrders.Services;

public class DefaultOrderRules : IOrderRule
{
    public void Apply(OrderProcessingContext context)
    {
        if (context.CustomerTier == "vip")
        {
            context.Total = context.Total - 10m;
        }

        if (context.ShouldReview)
        {
            context.Total = context.Total + 2.5m;
        }

        if (context.LineCount > 3)
        {
            context.Total = context.Total + 5m;
        }
    }
}
