namespace RefactorOrders.Models;

public class OrderRequest
{
    public string? CustomerName { get; set; }

    public string? CustomerNotes { get; set; }

    public string? CouponCode { get; set; }

    public bool ExpressShipping { get; set; }

    public List<OrderItemRequest>? Items { get; set; }
}

public class OrderItemRequest
{
    public string? Sku { get; set; }

    public string? Description { get; set; }

    public decimal UnitPrice { get; set; }

    public int Quantity { get; set; }

    public string? WarehouseCode { get; set; }
}

public class OrderCreateResponse
{
    public bool Success { get; set; }

    public int? OrderId { get; set; }

    public string? OrderNumber { get; set; }

    public string? CustomerName { get; set; }

    public decimal? TotalAmount { get; set; }

    public decimal? ShippingAmount { get; set; }

    public decimal? DiscountAmount { get; set; }

    public bool? ExpressShipping { get; set; }

    public string? PriorityCode { get; set; }

    public string? RegionCode { get; set; }

    public bool? ReviewRequired { get; set; }

    public int? LineCount { get; set; }

    public string? Summary { get; set; }

    public string? RawNote { get; set; }

    public string? Message { get; set; }

    public string? Error { get; set; }
}

public class OrderProcessingResult
{
    public bool IsSuccess { get; init; }

    public OrderCreateResponse? Response { get; init; }

    public string? ErrorMessage { get; init; }
}

public class Order
{
    public int Id { get; set; }

    public string CustomerName { get; set; } = string.Empty;

    public string? CustomerNotes { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal ShippingAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public bool ExpressShipping { get; set; }

    public string? PriorityCode { get; set; }

    public List<OrderItem> Items { get; set; } = new();
}

public class OrderItem
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public decimal UnitPrice { get; set; }

    public int Quantity { get; set; }

    public string WarehouseCode { get; set; } = string.Empty;
}
