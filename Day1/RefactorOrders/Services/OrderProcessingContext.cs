namespace RefactorOrders.Services;

public class OrderProcessingContext
{
    public string CustomerName { get; set; } = string.Empty;
    public string? CustomerNotes { get; set; }
    public string? CouponCode { get; set; }
    public bool ExpressShipping { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TotalTax { get; set; }
    public decimal Shipping { get; set; }
    public decimal Discount { get; set; }
    public decimal Total { get; set; }
    public int LineCount { get; set; }
    public bool ShouldReview { get; set; }
    public string? CustomerTier { get; set; }
    public string? PriorityCode { get; set; }
    public string? RegionCode { get; set; }
    public string? WarehouseHint { get; set; }
    public string LegacyNote { get; set; } = string.Empty;
}
